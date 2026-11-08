using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPlugin;

/// <summary>A faction's or an OCT player's grids: their value in SC and their PCU.</summary>
public struct FactionGrids
{
    public long Value;
    public long Pcu;
}

/// <summary>A grid as a node counted it: its biggest owner, its value in milli-SC and its PCU.</summary>
public struct LeaderboardGrid
{
    public long Id;
    public long Owner;
    public long Milli;
    public long Pcu;
}

/// <summary>
/// On the World Authority: every grid's latest count, whichever node made it. A grid that moved to
/// another node counts once, and a grid in an offline partition counts as its node last counted it
/// before the partition went offline, until it wakes (SE-1270). A grid gone from its node's reports
/// for <see cref="GoneAfter"/>, and in no offline partition, is forgotten.
/// </summary>
public sealed class LeaderboardBook
{
    private sealed class Entry
    {
        public LeaderboardGrid Grid;
        public string Node;
        public DateTime Seen;
    }

    private readonly Dictionary<long, Entry> grids = new();
    private readonly Dictionary<string, DateTime> reports = new();

    /// <summary>How long a node reports without a grid before it is gone: a handover or a wake isn't its end.</summary>
    public TimeSpan GoneAfter;

    /// <summary>A node silent this long holds none of the grids it reported.</summary>
    public TimeSpan SilentAfter;

    /// <summary>The grids in offline partitions; null while unknown, and then no grid is forgotten.</summary>
    public IReadOnlyDictionary<long, DateTime> Offline;

    public int Count => grids.Count;

    public void Report(string node, IEnumerable<LeaderboardGrid> counted, DateTime now)
    {
        reports[node] = now;
        foreach (var grid in counted)
            grids[grid.Id] = new Entry
            {
                Grid = grid,
                Node = node,
                Seen = now,
            };
    }

    /// <summary>The grids to rank now, and how many of them are in offline partitions.</summary>
    public List<LeaderboardGrid> Current(DateTime now, out int offline)
    {
        var current = new List<LeaderboardGrid>();
        offline = 0;
        foreach (var (id, entry) in grids.ToList())
        {
            if (Offline?.ContainsKey(id) == true)
            {
                // Once its partition wakes, maybe on another node, that node gets GoneAfter from
                // now to report it
                entry.Seen = now;
                offline++;
                current.Add(entry.Grid);
            }
            else if (
                reports.TryGetValue(entry.Node, out var last)
                && now - last < SilentAfter
                && !(last > entry.Seen && last - entry.Seen >= GoneAfter)
            )
                current.Add(entry.Grid);
            else if (Offline != null)
                grids.Remove(id);
        }
        return current;
    }

    /// <summary>id, owner, milli-SC, PCU, node: a record per line, tab-separated</summary>
    public string Serialize() =>
        string.Concat(
            grids.Values.Select(e =>
                LeaderboardMath.EncodeGrid(e.Grid).TrimEnd('\n')
                + "\t"
                + LeaderboardMath.Clean(e.Node)
                + "\n"
            )
        );

    /// <summary>A saved book; every grid counts as seen now. Throws FormatException or IndexOutOfRangeException.</summary>
    public void Load(string text, DateTime now)
    {
        foreach (var f in LeaderboardMath.Records(text))
        {
            var grid = LeaderboardMath.DecodeGrid(f);
            grids[grid.Id] = new Entry
            {
                Grid = grid,
                Node = f[4],
                Seen = now,
            };
            // Its node gets SilentAfter to report again
            reports[f[4]] = now;
        }
    }
}

/// <summary>A faction, or a player of OCT's without a tag, on the leaderboard.</summary>
public sealed class LeaderboardRow
{
    public string Tag = "";
    public string Name = "";

    // SC: the bank account with the money held for its market orders, and its grids
    public long Cash;
    public long Grids;
    public long Pcu;

    public long Total => Cash + Grids;
}

/// <summary>
/// The leaderboard's arithmetic, text and wire formats, without the game, so tests/leaderboard
/// checks them alone. Prices are milli-SC per unit and amounts raw units, as in the market.
/// </summary>
public static class LeaderboardMath
{
    public const int Width = 45;

    /// <summary>milli-SC of a raw amount at a price.</summary>
    public static decimal Value(long raw, long price) => (decimal)raw / Amounts.Unit * price;

    /// <summary>milli-SC as whole SC, rounded down, at most long.MaxValue.</summary>
    public static long Sc(decimal milli) =>
        milli >= (decimal)long.MaxValue * 1000 ? long.MaxValue : (long)(milli / 1000);

    /// <summary>The middle of OCT's bid and ask, averaged over the stations that trade the item; 0: none does.</summary>
    public static long MidPrice(IEnumerable<(long Bid, long Ask)> stations)
    {
        var mids = stations
            .Where(s => s.Bid > 0 && s.Ask > 0)
            .Select(s => (s.Bid + s.Ask) / 2m)
            .ToList();
        return mids.Count == 0 ? 0 : (long)Math.Round(mids.Average());
    }

    /// <summary>milli-SC as a grid's value: whole milli-SC, at most long.MaxValue.</summary>
    public static long Milli(decimal milli) => milli >= long.MaxValue ? long.MaxValue : (long)milli;

    /// <summary>
    /// The grids by faction or OCT player, as <paramref name="key"/> gives it for their owner; null
    /// leaves the grid out. A value is rounded down to SC once per key.
    /// </summary>
    public static Dictionary<long, FactionGrids> Sum(
        IEnumerable<LeaderboardGrid> grids,
        Func<long, long?> key
    )
    {
        var milli = new Dictionary<long, decimal>();
        var pcu = new Dictionary<long, long>();
        foreach (var grid in grids)
        {
            if (key(grid.Owner) is not { } k)
                continue;
            milli.TryGetValue(k, out var value);
            pcu.TryGetValue(k, out var blocks);
            milli[k] = value + grid.Milli;
            pcu[k] = checked(blocks + grid.Pcu);
        }
        return milli.ToDictionary(
            m => m.Key,
            m => new FactionGrids { Value = Sc(m.Value), Pcu = pcu[m.Key] }
        );
    }

    /// <summary>Richest first; the same total by PCU, then by tag.</summary>
    public static List<LeaderboardRow> Rank(IEnumerable<LeaderboardRow> rows) =>
        rows.OrderByDescending(r => r.Total)
            .ThenByDescending(r => r.Pcu)
            .ThenBy(r => r.Tag, StringComparer.Ordinal)
            .ToList();

    /// <summary>The board's text from ranked rows, for a Monospace panel <see cref="Width"/> characters wide.</summary>
    /// <paramref name="players"/>: OCT's players are on it too, so it is no faction board.
    public static string Format(
        IReadOnlyList<LeaderboardRow> ranked,
        int rows,
        DateTime updated,
        bool players = false
    )
    {
        var text = new StringBuilder();
        text.Append(Center(players ? "LEADERBOARD" : "FACTION LEADERBOARD")).Append("\n\n");
        text.Append(" #  ")
            .Append("Tag".PadRight(5))
            .Append(' ')
            .Append((players ? "Name" : "Faction").PadRight(13))
            .Append(' ')
            .Append("Value SC".PadLeft(13))
            .Append(' ')
            .Append("PCU".PadLeft(7))
            .Append('\n');
        if (ranked.Count == 0)
            text.Append(Center(players ? "Nobody yet" : "No factions yet")).Append('\n');
        for (int i = 0; i < Math.Min(rows, ranked.Count); i++)
        {
            var r = ranked[i];
            text.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2))
                .Append("  ")
                .Append(Cut(r.Tag, 5).PadRight(5))
                .Append(' ')
                .Append(Cut(r.Name, 13).PadRight(13))
                .Append(' ')
                .Append(N(r.Total).PadLeft(13))
                .Append(' ')
                .Append(N(r.Pcu).PadLeft(7))
                .Append('\n');
        }
        text.Append("\nValue: bank account and grids at station\n");
        text.Append("prices. Updated ")
            .Append(updated.ToString("HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC.\n");
        return text.ToString();
    }

    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Center(string text) => text.PadLeft((Width + text.Length) / 2);

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text.Substring(0, length);

    // --- wire formats: a record per line, tab-separated ---

    internal static string Clean(string text) =>
        (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static long L(string text) => long.Parse(text, CultureInfo.InvariantCulture);

    private static string S(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static IEnumerable<string[]> Records(string text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t'));

    /// <summary>item, price</summary>
    public static string EncodePrices(IReadOnlyDictionary<string, long> prices) =>
        string.Concat(prices.Select(p => $"{Clean(p.Key)}\t{S(p.Value)}\n"));

    public static Dictionary<string, long> DecodePrices(string text) =>
        Records(text).ToDictionary(f => f[0], f => L(f[1]));

    /// <summary>grid id, owner, milli-SC, PCU</summary>
    public static string EncodeGrid(LeaderboardGrid g) =>
        $"{S(g.Id)}\t{S(g.Owner)}\t{S(g.Milli)}\t{S(g.Pcu)}\n";

    internal static LeaderboardGrid DecodeGrid(string[] f) =>
        new()
        {
            Id = L(f[0]),
            Owner = L(f[1]),
            Milli = L(f[2]),
            Pcu = L(f[3]),
        };

    public static string EncodeGrids(IEnumerable<LeaderboardGrid> grids) =>
        string.Concat(grids.Select(EncodeGrid));

    public static List<LeaderboardGrid> DecodeGrids(string text) =>
        Records(text).Select(DecodeGrid).ToList();

    /// <summary>tag, name, cash, grids, PCU</summary>
    public static string EncodeRows(IEnumerable<LeaderboardRow> rows) =>
        string.Concat(
            rows.Select(r =>
                $"{Clean(r.Tag)}\t{Clean(r.Name)}\t{S(r.Cash)}\t{S(r.Grids)}\t{S(r.Pcu)}\n"
            )
        );

    public static List<LeaderboardRow> DecodeRows(string text) =>
        Records(text)
            .Select(f => new LeaderboardRow
            {
                Tag = f[0],
                Name = f[1],
                Cash = L(f[2]),
                Grids = L(f[3]),
                Pcu = L(f[4]),
            })
            .ToList();
}
