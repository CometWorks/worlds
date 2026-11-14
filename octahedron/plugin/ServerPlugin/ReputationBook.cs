using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ServerPlugin;

/// <summary>What changed a member's reputation</summary>
internal enum ReputationKind
{
    /// <summary>Grinding, shooting or blowing up another member's grid: the value lost, negative</summary>
    Damage,

    /// <summary>A collision with another member's grid, one ram per incident: the value lost, negative</summary>
    Ram,

    /// <summary>A payment into OCT's account: the SC paid</summary>
    Deposit,

    /// <summary>Blocks shared with OCT, positive, or unshared, negative: their value</summary>
    Share,

    /// <summary>An admin's change</summary>
    Admin,
}

internal sealed class ReputationSettings
{
    public long Start = 200_000;
    public long KickBelow = -200_000;
    public double KickAfterSeconds = 3600;

    /// <summary>Seconds left at which the member is warned again, largest first</summary>
    public double[] WarnSeconds = [1800, 600, 300];
    public long RamGraceSeconds = 3600;

    public static double[] ParseWarnings(string text) =>
        (text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t =>
                double.TryParse(
                    t.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var s
                )
                    ? s
                    : -1
            )
            .Where(s => s > 0)
            .OrderByDescending(s => s)
            .ToArray();
}

/// <summary>
/// The members' reputations and their countdowns to removal, where they are authoritative: a plain
/// server or the World Authority. No game types, so the tests run it without a game.
/// </summary>
internal sealed class ReputationBook
{
    private sealed class Entry
    {
        public decimal Value;

        // Seconds below KickBelow so far, -1 while above, Removed once the countdown ended
        public double Below = -1;

        // Warnings of this countdown given
        public int Warned;
        public bool Negative;

        // Unix seconds of the last ram forgiven as an accident, and its incident
        public long FreeRamAt;
        public string FreeIncident;
    }

    private const double Removed = -2;

    private readonly Dictionary<long, Entry> entries = new();

    // The start the reputations were counted from: saves from before it was kept used 10,000
    private const long OldStart = 10_000;
    private long start = OldStart;

    public ReputationSettings Settings = new();

    /// <summary>A change the save should keep</summary>
    public bool Dirty { get; set; }

    public decimal Get(long identity) =>
        entries.TryGetValue(identity, out var e) ? e.Value : Settings.Start;

    /// <summary>Seconds left before the member's removal, null when no countdown runs</summary>
    public double? Left(long identity) =>
        entries.TryGetValue(identity, out var e) && e.Below >= 0
            ? Math.Max(0, Settings.KickAfterSeconds - e.Below)
            : null;

    private Entry Of(long identity)
    {
        if (!entries.TryGetValue(identity, out var e))
            entries[identity] = e = new Entry { Value = Settings.Start };
        return e;
    }

    /// <summary>
    /// A new Settings.Start moves everyone's reputation by as much (SE-1495): a member keeps what
    /// they gained or lost since they started, from the new start.
    /// </summary>
    public void Rebase()
    {
        if (Settings.Start == start)
            return;
        var by = Settings.Start - start;
        foreach (var e in entries.Values)
        {
            e.Value += by;
            e.Negative = e.Value < 0;
        }
        start = Settings.Start;
        Dirty = true;
    }

    /// <summary>An admin's change, which warns nobody</summary>
    public void Set(long identity, decimal value)
    {
        var e = Of(identity);
        e.Value = value;
        e.Negative = value < 0;
        Dirty = true;
    }

    /// <summary>
    /// Adds a change. The first ram in RamGraceSeconds is forgiven, with every later change of the same
    /// incident. Returns what to tell the member, or null.
    /// </summary>
    public string Apply(
        long identity,
        ReputationKind kind,
        decimal amount,
        string incident,
        long now
    )
    {
        var e = Of(identity);
        if (kind == ReputationKind.Ram && incident != null)
        {
            if (incident == e.FreeIncident)
                return null;
            if (
                Settings.RamGraceSeconds > 0
                && (e.FreeRamAt == 0 || now - e.FreeRamAt >= Settings.RamGraceSeconds)
            )
            {
                e.FreeRamAt = now;
                e.FreeIncident = incident;
                Dirty = true;
                return $"Your ram of another member's grid was taken as an accident: no reputation lost. "
                    + $"Another one within {Duration(Settings.RamGraceSeconds)} costs reputation.";
            }
        }
        if (amount == 0)
            return null;
        e.Value += amount;
        Dirty = true;
        if (e.Value >= 0)
        {
            e.Negative = false;
            return null;
        }
        if (e.Negative)
            return null;
        e.Negative = true;
        return $"Your reputation in OCT is below zero: {Sc(e.Value)}. Damaging grids of other members lowers it, "
            + "paying into OCT's account and sharing your grids with OCT raise it. Type !oct rep to see it.";
    }

    /// <summary>
    /// Runs the countdowns by the seconds passed. member says who is still in OCT: the others' countdowns
    /// stop. Adds what to tell whom, and whom to remove from OCT now.
    /// </summary>
    public void Tick(
        double seconds,
        Func<long, bool> member,
        List<(long Identity, string Text)> tell,
        List<long> kick,
        Func<long, bool> leader = null
    )
    {
        foreach (var (identity, e) in entries)
        {
            var inOct = member(identity);
            // OCT's leaders are never removed: no countdown, a running one stops without a word
            if (inOct && leader?.Invoke(identity) == true)
            {
                if (e.Below != -1)
                {
                    e.Below = -1;
                    Dirty = true;
                }
                continue;
            }
            // Removed: again, without a word, until the faction has them out
            if (e.Below == Removed)
            {
                if (inOct && e.Value < Settings.KickBelow)
                    kick.Add(identity);
                else
                {
                    e.Below = -1;
                    Dirty = true;
                }
                continue;
            }
            if (e.Value >= Settings.KickBelow || !inOct)
            {
                if (e.Below < 0)
                    continue;
                e.Below = -1;
                Dirty = true;
                if (inOct)
                    tell.Add(
                        (
                            identity,
                            $"Your reputation is back above {Sc(Settings.KickBelow)}: you stay in OCT."
                        )
                    );
                continue;
            }
            Dirty = true;
            if (e.Below < 0)
            {
                e.Below = 0;
                // Warnings at or above the whole countdown say no more than its start
                e.Warned = Settings.WarnSeconds.Count(w => w >= Settings.KickAfterSeconds);
                tell.Add(
                    (
                        identity,
                        $"Your reputation in OCT is {Sc(e.Value)}, below {Sc(Settings.KickBelow)}. "
                            + $"If it stays there for {Duration(Settings.KickAfterSeconds)}, you are removed from OCT. "
                            + "Pay into OCT's account or share your grids with OCT to raise it."
                    )
                );
                continue;
            }
            e.Below += seconds;
            var left = Settings.KickAfterSeconds - e.Below;
            if (left <= 0)
            {
                kick.Add(identity);
                e.Below = Removed;
                tell.Add(
                    (
                        identity,
                        $"You were removed from OCT: your reputation stayed below {Sc(Settings.KickBelow)} "
                            + $"for {Duration(Settings.KickAfterSeconds)} ({Sc(e.Value)}), "
                            + "from damaging grids of other members."
                    )
                );
                continue;
            }
            var warning = -1;
            while (e.Warned < Settings.WarnSeconds.Length && left <= Settings.WarnSeconds[e.Warned])
                warning = e.Warned++;
            if (warning >= 0)
                tell.Add(
                    (
                        identity,
                        $"{Duration(Settings.WarnSeconds[warning])} left before you are removed from OCT "
                            + $"for low reputation ({Sc(e.Value)}, below {Sc(Settings.KickBelow)})."
                    )
                );
        }
    }

    /// <summary>!oct rep's answer: the reputation, the removal threshold and the countdown if one runs</summary>
    public string Describe(long identity, bool leader = false)
    {
        var text = $"Your reputation in OCT: {Sc(Get(identity))}. ";
        if (leader)
            return text + "OCT's leaders are never removed from OCT.";
        text += $"Removal from OCT below {Sc(Settings.KickBelow)}";
        return Left(identity) is { } seconds
            ? text + $": removed in {Duration(seconds)} unless it rises."
            : text + ".";
    }

    public static string Sc(decimal value) =>
        Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);

    public static string Duration(double seconds) =>
        seconds >= 120
            ? $"{Math.Round(seconds / 60):0} minutes"
            : $"{Math.Round(seconds):0} seconds";

    // --- the save: the start, then one line per member ---

    private const string StartLine = "start";

    public string Serialize() =>
        string.Join(
            "\n",
            entries
                .Select(p =>
                    string.Join(
                        " ",
                        p.Key.ToString(CultureInfo.InvariantCulture),
                        p.Value.Value.ToString(CultureInfo.InvariantCulture),
                        p.Value.Below.ToString("R", CultureInfo.InvariantCulture),
                        p.Value.Warned.ToString(CultureInfo.InvariantCulture),
                        p.Value.Negative ? "1" : "0",
                        p.Value.FreeRamAt.ToString(CultureInfo.InvariantCulture)
                    )
                )
                .Prepend($"{StartLine} {start.ToString(CultureInfo.InvariantCulture)}")
        );

    public static ReputationBook Deserialize(string text)
    {
        var book = new ReputationBook();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            if (f[0] == StartLine)
            {
                book.start = long.Parse(f[1], CultureInfo.InvariantCulture);
                continue;
            }
            book.entries[long.Parse(f[0], CultureInfo.InvariantCulture)] = new Entry
            {
                Value = decimal.Parse(f[1], CultureInfo.InvariantCulture),
                Below = double.Parse(f[2], CultureInfo.InvariantCulture),
                Warned = int.Parse(f[3], CultureInfo.InvariantCulture),
                Negative = f[4] == "1",
                FreeRamAt = long.Parse(f[5], CultureInfo.InvariantCulture),
            };
        }
        return book;
    }

    // --- the nodes' changes to the authority: one line per member, kind and incident ---

    public static string EncodeChanges(
        IEnumerable<(long Identity, ReputationKind Kind, string Incident, decimal Amount)> changes
    ) =>
        string.Join(
            "\n",
            changes.Select(c =>
                string.Join(
                    " ",
                    c.Identity.ToString(CultureInfo.InvariantCulture),
                    c.Kind,
                    c.Amount.ToString(CultureInfo.InvariantCulture),
                    c.Incident ?? "-"
                )
            )
        );

    public static List<(
        long Identity,
        ReputationKind Kind,
        string Incident,
        decimal Amount
    )> DecodeChanges(string text) =>
        (text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .Select(f =>
                (
                    long.Parse(f[0], CultureInfo.InvariantCulture),
                    (ReputationKind)Enum.Parse(typeof(ReputationKind), f[1]),
                    f[3] == "-" ? null : f[3],
                    decimal.Parse(f[2], CultureInfo.InvariantCulture)
                )
            )
            .ToList();
}
