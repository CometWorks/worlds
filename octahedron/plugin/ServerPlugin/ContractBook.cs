using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPlugin;

internal readonly struct ContractPoint(double x, double y, double z)
{
    public readonly double X = x;
    public readonly double Y = y;
    public readonly double Z = z;

    public double Distance(ContractPoint other) =>
        Math.Sqrt(
            (X - other.X) * (X - other.X)
                + (Y - other.Y) * (Y - other.Y)
                + (Z - other.Z) * (Z - other.Z)
        );
}

[Flags]
internal enum ContractGridFlags
{
    None = 0,

    // An NPC grid of another faction than OCT and the wreck factions, near a station
    Bounty = 1,

    // Movable and nobody's or a wreck faction's, near a station
    Derelict = 2,

    // Nobody's, and nothing on it works: armor and decor. Static or not.
    Wreck = 4,

    // Without power. Static or not.
    Unpowered = 8,

    Shipyard = 16,

    // Someone sits in it or controls it: it is in use, whatever else it looks like
    Seated = 32,

    // Nothing on it can have an owner, armor mostly: giving it away changes nothing
    Unownable = 64,

    // An NPC's movable grid, not OCT's, joined by a connector or landing gear to a player's ship
    Towed = 128,

    // In an enabled safe zone
    SafeZone = 256,
}

// A grid as the node simulating it last reported it. Sent as a line to the World Authority.
internal sealed class ContractGrid
{
    public long Id;
    public ContractGridFlags Flags;
    public ContractPoint Position;
    public int Blocks;
    public double MassKg;
    public double Threat;
    public string Name;
    public string Node = "";
    public DateTime Seen;

    // Its big owner, and who built most of it: a disowned ship still has a builder
    public long Owner;
    public long Builder;

    // The PCU of the grid and its rotor and piston parts
    public int Pcu;

    // Towed: the player whose ship holds it
    public long Tower;

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public string Encode() =>
        string.Join(
            "\t",
            Id.ToString(CultureInfo.InvariantCulture),
            ((int)Flags).ToString(CultureInfo.InvariantCulture),
            N(Position.X),
            N(Position.Y),
            N(Position.Z),
            Blocks.ToString(CultureInfo.InvariantCulture),
            N(MassKg),
            N(Threat),
            Node,
            Seen.Ticks.ToString(CultureInfo.InvariantCulture),
            (Name ?? "").Replace('\t', ' ').Replace('\n', ' '),
            Owner.ToString(CultureInfo.InvariantCulture),
            Builder.ToString(CultureInfo.InvariantCulture),
            Pcu.ToString(CultureInfo.InvariantCulture),
            Tower.ToString(CultureInfo.InvariantCulture)
        );

    public static ContractGrid Decode(string line)
    {
        var p = line.Split('\t');
        double D(int i) => double.Parse(p[i], CultureInfo.InvariantCulture);
        return new ContractGrid
        {
            Id = long.Parse(p[0], CultureInfo.InvariantCulture),
            Flags = (ContractGridFlags)int.Parse(p[1], CultureInfo.InvariantCulture),
            Position = new ContractPoint(D(2), D(3), D(4)),
            Blocks = int.Parse(p[5], CultureInfo.InvariantCulture),
            MassKg = D(6),
            Threat = D(7),
            Node = p[8],
            Seen = new DateTime(long.Parse(p[9], CultureInfo.InvariantCulture), DateTimeKind.Utc),
            Name = p[10],
            Owner = long.Parse(p[11], CultureInfo.InvariantCulture),
            Builder = long.Parse(p[12], CultureInfo.InvariantCulture),
            Pcu = int.Parse(p[13], CultureInfo.InvariantCulture),
            Tower = long.Parse(p[14], CultureInfo.InvariantCulture),
        };
    }
}

// What a bounty, salvage or wreck contract watches. Kind: b, s or w. Size: the threat score of a
// bounty's target or the blocks of a wreck when posted.
internal sealed class ContractTarget(
    char kind,
    long grid,
    long station,
    double size,
    ContractPoint position
)
{
    public readonly char Kind = kind;
    public readonly long Grid = grid;
    public readonly long Station = station;
    public readonly double Size = size;
    public ContractPoint Position = position;

    // The last time the player who took it was near the target
    public DateTime HunterSeen = DateTime.MinValue;

    // Salvage and wrecks: whose it was when posted. They can't sell it to OCT.
    public long Owner;
    public long Builder;
}

internal enum ContractVerdict
{
    Keep,
    Withdraw,
    Finish,

    // Finish, and the node holding the target closes it
    Scrap,

    // Finish, and the node holding the target gives it to the player who took the contract
    Give,
    Fail,
}

/// <summary>
/// The rules of the contracts that watch grids, without the game, so tests/contracts/ContractTests
/// runs them against any clock. Kept by the authority: the server itself or, on a cluster, the World
/// Authority, which has no grids. The nodes report the grids they simulate, and the targets every
/// time, so the book holds what each grid last looked like, in an offline partition too. Time is
/// always an argument.
/// </summary>
internal sealed class ContractBook(TimeSpan goneAfter)
{
    public const double DoneShare = 0.25;
    public static readonly TimeSpan WitnessSlack = TimeSpan.FromMinutes(1);

    // A candidate no report named for this long is no candidate any more; a full scan is a minute
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(150);

    // How long a target's node may stay silent before the target counts as gone anyway
    public static readonly TimeSpan SilentNodeAfter = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan ClosingFor = TimeSpan.FromMinutes(10);

    public readonly Dictionary<long, ContractTarget> Targets = new();
    public readonly Dictionary<long, ContractGrid> Grids = new();

    // Grids towed in and given away, grid -> the player, 0 once a report shows they have it. A given
    // grid never gets another contract, or a player could disown it and tow it in again for the pay.
    public readonly Dictionary<long, long> Given = new();

    // Grids in offline partitions and since when, from the gateway; null while unknown, when nothing
    // can be said about a grid no report names
    public Dictionary<long, DateTime> Offline;

    // Reports served since the authority started, for !oct contracts
    public long Reports;

    private readonly Dictionary<string, DateTime> reports = new();
    private readonly Dictionary<long, DateTime> closing = new();

    // Chat for a player: the World Authority has no clients, so each node gets it once and the one
    // with the player delivers it
    private sealed record Message(long Player, string Text, DateTime Until, HashSet<string> Told);

    private readonly List<Message> messages = [];

    // What the nodes must report every time: the targets, and the grids still to be given away
    public IEnumerable<long> Watched =>
        Targets
            .Values.Select(t => t.Grid)
            .Concat(Given.Where(g => g.Value != 0).Select(g => g.Key))
            .Distinct();

    /// <summary>
    /// A node's rows. The answer, comma separated: "watched ids;ids to close;grid:player to give;
    /// player:base64 text to tell".
    /// The orders go to every node; the one that holds the grid carries them out, after a handover
    /// the new one, and they repeat until a report shows it done.
    /// </summary>
    public string Report(string node, IEnumerable<ContractGrid> rows, DateTime now)
    {
        Reports++;
        reports[node] = now;
        foreach (var row in rows)
        {
            if (closing.ContainsKey(row.Id))
                continue;
            row.Node = node;
            row.Seen = now;
            Grids[row.Id] = row;
            if (
                Given.TryGetValue(row.Id, out var taker)
                && taker != 0
                && (row.Owner == taker || row.Flags.HasFlag(ContractGridFlags.Unownable))
            )
                Given[row.Id] = 0;
        }
        foreach (var id in closing.Where(c => now >= c.Value).Select(c => c.Key).ToList())
            closing.Remove(id);
        return string.Join(",", Watched)
            + ";"
            + string.Join(",", closing.Keys)
            + ";"
            + string.Join(",", Given.Where(g => g.Value != 0).Select(g => $"{g.Key}:{g.Value}"))
            + ";"
            + string.Join(",", Tells(node, now));
    }

    private IEnumerable<string> Tells(string node, DateTime now)
    {
        messages.RemoveAll(m => now >= m.Until);
        return messages
            .Where(m => m.Told.Add(node))
            .Select(m => $"{m.Player}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Text))}")
            .ToList();
    }

    public void Tell(long player, string text, DateTime now) =>
        messages.Add(new Message(player, text, now + StaleAfter, []));

    public static (
        HashSet<long> Watched,
        List<long> Close,
        List<(long Grid, long Player)> Give,
        List<(long Player, string Text)> Tell
    ) ParseAnswer(string answer)
    {
        static IEnumerable<long> Ids(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(i => long.Parse(i, CultureInfo.InvariantCulture));
        var parts = answer.Split(';');
        var give = parts[2]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Split(':'))
            .Select(g =>
                (
                    long.Parse(g[0], CultureInfo.InvariantCulture),
                    long.Parse(g[1], CultureInfo.InvariantCulture)
                )
            )
            .ToList();
        var tell = parts[3]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Split(':'))
            .Select(m =>
                (
                    long.Parse(m[0], CultureInfo.InvariantCulture),
                    Encoding.UTF8.GetString(Convert.FromBase64String(m[1]))
                )
            )
            .ToList();
        return (Ids(parts[0]).ToHashSet(), Ids(parts[1]).ToList(), give, tell);
    }

    /// <summary>
    /// No node has the grid. A grid in an offline partition isn't gone. Otherwise the node last
    /// holding it must have reported without it for goneAfter, so a handover or a slow node start
    /// loses nothing; a node silent for much longer doesn't hold a target up forever.
    /// </summary>
    public bool IsGone(long grid, DateTime now)
    {
        if (!Grids.TryGetValue(grid, out var row))
            return true;
        if (Offline == null || Offline.ContainsKey(grid))
            return false;
        if (now - row.Seen >= SilentNodeAfter)
            return true;
        return reports.TryGetValue(row.Node, out var last)
            && last > row.Seen
            && last - row.Seen >= goneAfter;
    }

    // A grid in an offline partition is where it was: once that partition wakes, its node gets
    // goneAfter to load it and report it, counted from now and not from before it slept
    public void Forget(DateTime now)
    {
        if (Offline == null)
            return;
        foreach (var row in Grids.Values.Where(r => Offline.ContainsKey(r.Id)))
            row.Seen = now;
        foreach (var id in Given.Keys.Where(g => Given[g] != 0 && IsGone(g, now)).ToList())
            Given[id] = 0;
        var targeted = Watched.ToHashSet();
        foreach (
            var id in Grids
                .Values.Where(r => !targeted.Contains(r.Id) && now - r.Seen >= StaleAfter)
                .Select(r => r.Id)
                .ToList()
        )
            Grids.Remove(id);
    }

    public ContractGrid Shipyard(ContractPoint station, double searchMeters) =>
        Grids
            .Values.Where(r =>
                r.Flags.HasFlag(ContractGridFlags.Shipyard)
                && r.Position.Distance(station) <= searchMeters
            )
            .OrderBy(r => r.Position.Distance(station))
            .FirstOrDefault();

    // A grid in an offline partition keeps its last report, so a zone active when the partition went
    // offline counts as active for as long as it sleeps. Emulating the zone's chip use belongs here.
    public bool InActiveSafeZone(ContractGrid row) => row.Flags.HasFlag(ContractGridFlags.SafeZone);

    private IEnumerable<ContractGrid> Free()
    {
        var targeted = Targets.Values.Select(t => t.Grid).ToHashSet();
        return Grids.Values.Where(r =>
            !targeted.Contains(r.Id) && !Given.ContainsKey(r.Id) && !InActiveSafeZone(r)
        );
    }

    /// <summary>The bounty targets in range of the station, the highest threat first.</summary>
    public List<ContractGrid> Bounties(
        ContractPoint station,
        double meters,
        double minThreat,
        int wanted
    ) =>
        Free()
            .Where(r =>
                r.Flags.HasFlag(ContractGridFlags.Bounty)
                && r.Threat >= minThreat
                && r.Position.Distance(station) <= meters
            )
            .OrderByDescending(r => r.Threat)
            .ThenBy(r => r.Id)
            .Take(Math.Max(0, wanted))
            .ToList();

    /// <summary>
    /// The derelicts in range of the station, the nearest first. One already at the shipyard gets no
    /// contract: nobody would have to tow it to own it.
    /// </summary>
    public List<ContractGrid> Derelicts(
        ContractPoint station,
        double meters,
        int wanted,
        double shipyardSearchMeters = 0,
        double salvageRadius = 0
    ) =>
        Free()
            .Where(r =>
                r.Flags.HasFlag(ContractGridFlags.Derelict)
                && !r.Flags.HasFlag(ContractGridFlags.Seated)
                && r.Position.Distance(station) <= meters
                && !InReach(r.Position, station, shipyardSearchMeters, salvageRadius)
            )
            .OrderBy(r => r.Position.Distance(station))
            .ThenBy(r => r.Id)
            .Take(Math.Max(0, wanted))
            .ToList();

    /// <summary>
    /// NPC grids towed to a shipyard of one of the stations, for whoever holds them. A grid a contract
    /// someone took is about goes that contract's way, unless it is a bounty of the same player, which
    /// taking over the target completes.
    /// </summary>
    public List<ContractGrid> TowedIn(
        IEnumerable<ContractPoint> stations,
        double shipyardSearchMeters,
        double salvageRadius,
        Func<ContractTarget, long> takenBy
    ) =>
        Grids
            .Values.Where(r =>
                r.Flags.HasFlag(ContractGridFlags.Towed)
                && !r.Flags.HasFlag(ContractGridFlags.Seated)
                && r.Tower != 0
                && !Given.ContainsKey(r.Id)
                && Targets.Values.All(t =>
                    t.Grid != r.Id || takenBy(t) == 0 || (t.Kind == 'b' && takenBy(t) == r.Tower)
                )
                && stations.Any(s => AtShipyard(r, s, shipyardSearchMeters, salvageRadius))
            )
            .ToList();

    /// <summary>
    /// A wreck in an offline partition: nothing on it works and nobody owns it, at once; any other
    /// grid without power once its partition has been offline for offlineFor. A visit brings the
    /// partition online and starts that time anew. A grid someone sits in is no wreck.
    /// </summary>
    public static bool IsWreck(ContractGridFlags flags, TimeSpan offline, TimeSpan offlineFor) =>
        !flags.HasFlag(ContractGridFlags.Seated)
        && (
            flags.HasFlag(ContractGridFlags.Wreck)
            || (flags.HasFlag(ContractGridFlags.Unpowered) && offline >= offlineFor)
        );

    /// <summary>Wanted wrecks picked at random from the pool biggest ones, by blocks.</summary>
    public List<ContractGrid> Wrecks(
        DateTime now,
        TimeSpan offlineFor,
        int pool,
        int wanted,
        Random random
    )
    {
        if (Offline == null)
            return [];
        var biggest = Free()
            .Where(r =>
                Offline.TryGetValue(r.Id, out var since)
                && IsWreck(r.Flags, now - since, offlineFor)
            )
            .OrderByDescending(r => r.Blocks)
            .ThenBy(r => r.Id)
            .Take(Math.Max(0, pool))
            .ToList();
        var picked = new List<ContractGrid>();
        while (picked.Count < wanted && biggest.Count > 0)
        {
            var i = random.Next(biggest.Count);
            picked.Add(biggest[i]);
            biggest.RemoveAt(i);
        }
        return picked;
    }

    /// <summary>
    /// What happens to a contract now. Active: a player took it. stationAt: null when the station
    /// no longer exists. hunterNear: the player who took it is near the target. hunterOwns: that
    /// player or their faction owned or built the target when it was posted.
    /// </summary>
    public ContractVerdict Judge(
        ContractTarget target,
        bool active,
        ContractPoint? stationAt,
        bool hunterNear,
        double shipyardSearchMeters,
        double salvageRadius,
        DateTime now,
        long hunter = 0,
        bool hunterOwns = false
    )
    {
        var gone = IsGone(target.Grid, now);
        Grids.TryGetValue(target.Grid, out var row);
        if (hunterNear)
            target.HunterSeen = now;
        if (row != null)
            target.Position = row.Position;
        if (!active)
            return
                !gone
                && stationAt != null
                && !Given.ContainsKey(target.Grid)
                && !InActiveSafeZone(row)
                && Wanted(target.Kind, row.Flags)
                ? ContractVerdict.Keep
                : ContractVerdict.Withdraw;
        if (hunterOwns && target.Kind != 'b')
            return ContractVerdict.Fail;

        var witnessed = row != null && target.HunterSeen >= row.Seen - WitnessSlack;
        switch (target.Kind)
        {
            case 'b':
                if (gone)
                    return witnessed ? ContractVerdict.Finish : ContractVerdict.Fail;
                return
                    !row.Flags.HasFlag(ContractGridFlags.Bounty)
                    || row.Threat <= target.Size * DoneShare
                    ? ContractVerdict.Finish
                    : ContractVerdict.Keep;
            case 's':
                if (gone || stationAt == null || Claimed(row, target, hunter))
                    return ContractVerdict.Fail;
                return
                    hunterNear
                    && !row.Flags.HasFlag(ContractGridFlags.Seated)
                    && AtShipyard(row, stationAt.Value, shipyardSearchMeters, salvageRadius)
                    ? ContractVerdict.Give
                    : ContractVerdict.Keep;
            default:
                if (gone)
                    return witnessed ? ContractVerdict.Finish : ContractVerdict.Fail;
                // The hunter may repair it to fly it in; power from anyone else means its owner is back
                if (
                    Claimed(row, target, hunter)
                    || (!row.Flags.HasFlag(ContractGridFlags.Unpowered) && row.Owner != hunter)
                )
                    return ContractVerdict.Fail;
                if (!hunterNear || row.Flags.HasFlag(ContractGridFlags.Seated))
                    return ContractVerdict.Keep;
                if (
                    stationAt != null
                    && AtShipyard(row, stationAt.Value, shipyardSearchMeters, salvageRadius)
                )
                    return ContractVerdict.Give;
                return row.Blocks <= target.Size * DoneShare
                    ? ContractVerdict.Scrap
                    : ContractVerdict.Keep;
        }
    }

    // Once posted, a wreck stays one while it has no power: the contract itself wakes its partition
    private static bool Wanted(char kind, ContractGridFlags flags) =>
        kind == 'b'
            ? flags.HasFlag(ContractGridFlags.Bounty)
            : !flags.HasFlag(ContractGridFlags.Seated)
                && flags.HasFlag(
                    kind == 's' ? ContractGridFlags.Derelict : ContractGridFlags.Unpowered
                );

    // Someone else put their own blocks on it and owns it now, its old owner by welding or hacking it
    // back included: the hunter takes nothing from a player. Who owned it when posted, or a wreck
    // faction, doesn't count as someone else.
    private static bool Claimed(ContractGrid row, ContractTarget target, long hunter) =>
        row.Owner != 0 && row.Owner != target.Owner && row.Owner != hunter;

    public bool InReach(
        ContractPoint position,
        ContractPoint station,
        double search,
        double radius
    ) =>
        Shipyard(station, search) is { } shipyard && position.Distance(shipyard.Position) <= radius;

    // Awake too: only a node can act on it, and a sleeping grid hasn't just been brought there
    private bool AtShipyard(
        ContractGrid row,
        ContractPoint station,
        double search,
        double radius
    ) => Offline?.ContainsKey(row.Id) != true && InReach(row.Position, station, search, radius);

    /// <summary>The grid is to be closed by the node holding it, and is no candidate any more.</summary>
    public void Scrap(long grid, DateTime now)
    {
        Grids.Remove(grid);
        closing[grid] = now + ClosingFor;
    }

    /// <summary>The grid is to be given to the player by the node holding it.</summary>
    public void Give(long grid, long player) => Given[grid] = player;

    // --- saved in the world ---

    // contract:kind:grid:station:size:x:y:z:hunterSeen:owner:builder;...
    public string SerializeTargets()
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return string.Join(
            ";",
            Targets.Select(t =>
                $"{t.Key}:{t.Value.Kind}:{t.Value.Grid}:{t.Value.Station}:{N(t.Value.Size)}:"
                + $"{N(t.Value.Position.X)}:{N(t.Value.Position.Y)}:{N(t.Value.Position.Z)}:"
                + $"{t.Value.HunterSeen.Ticks.ToString(CultureInfo.InvariantCulture)}:"
                + $"{t.Value.Owner}:{t.Value.Builder}"
            )
        );
    }

    // Throws FormatException or IndexOutOfRangeException. hunterSeen is absent in older worlds.
    public void LoadTargets(string text)
    {
        static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);
        static long L(string v) => long.Parse(v, CultureInfo.InvariantCulture);
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split(':');
            Targets[L(p[0])] = new ContractTarget(
                p[1][0],
                L(p[2]),
                L(p[3]),
                D(p[4]),
                new ContractPoint(D(p[5]), D(p[6]), D(p[7]))
            )
            {
                HunterSeen =
                    p.Length > 8 ? new DateTime(L(p[8]), DateTimeKind.Utc) : DateTime.MinValue,
                Owner = p.Length > 9 ? L(p[9]) : 0,
                Builder = p.Length > 10 ? L(p[10]) : 0,
            };
        }
    }

    // grid:player, 0 once given
    public string SerializeGiven() => string.Join(";", Given.Select(g => $"{g.Key}:{g.Value}"));

    public void LoadGiven(string text)
    {
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split(':');
            Given[long.Parse(p[0], CultureInfo.InvariantCulture)] = long.Parse(
                p[1],
                CultureInfo.InvariantCulture
            );
        }
    }

    public string SerializeGrids() => string.Join("\n", Grids.Values.Select(g => g.Encode()));

    // The time the authority was down doesn't count against them: they count as seen now
    public void LoadGrids(string text, DateTime now)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var row = ContractGrid.Decode(line);
            row.Seen = now;
            Grids[row.Id] = row;
        }
    }
}
