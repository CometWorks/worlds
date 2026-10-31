using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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

    // Movable, nobody's, and nothing on it works: armor and decor
    Wreck = 4,

    // Movable and without power
    Unpowered = 8,

    Shipyard = 16,
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
            (Name ?? "").Replace('\t', ' ').Replace('\n', ' ')
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
}

internal enum ContractVerdict
{
    Keep,
    Withdraw,
    Finish,

    // Finish, and the node holding the target closes it
    Scrap,
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

    // Grids in offline partitions and since when, from the gateway; null while unknown, when nothing
    // can be said about a grid no report names
    public Dictionary<long, DateTime> Offline;

    private readonly Dictionary<string, DateTime> reports = new();
    private readonly Dictionary<long, DateTime> closing = new();

    /// <summary>A node's rows. The answer: "watched ids;ids to close", comma separated.</summary>
    public string Report(string node, IEnumerable<ContractGrid> rows, DateTime now)
    {
        reports[node] = now;
        foreach (var row in rows)
        {
            if (closing.ContainsKey(row.Id))
                continue;
            row.Node = node;
            row.Seen = now;
            Grids[row.Id] = row;
        }
        foreach (var id in closing.Where(c => now >= c.Value).Select(c => c.Key).ToList())
            closing.Remove(id);
        return string.Join(",", Targets.Values.Select(t => t.Grid).Distinct())
            + ";"
            + string.Join(",", closing.Keys);
    }

    public static (HashSet<long> Watched, List<long> Close) ParseAnswer(string answer)
    {
        static IEnumerable<long> Ids(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(i => long.Parse(i, CultureInfo.InvariantCulture));
        var parts = answer.Split(';');
        return (Ids(parts[0]).ToHashSet(), Ids(parts[1]).ToList());
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

    public void Forget(DateTime now)
    {
        if (Offline == null)
            return;
        var targeted = Targets.Values.Select(t => t.Grid).ToHashSet();
        foreach (
            var id in Grids
                .Values.Where(r =>
                    !targeted.Contains(r.Id)
                    && !Offline.ContainsKey(r.Id)
                    && now - r.Seen >= StaleAfter
                )
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

    private IEnumerable<ContractGrid> Free()
    {
        var targeted = Targets.Values.Select(t => t.Grid).ToHashSet();
        return Grids.Values.Where(r => !targeted.Contains(r.Id));
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

    /// <summary>The derelicts in range of the station, the nearest first.</summary>
    public List<ContractGrid> Derelicts(ContractPoint station, double meters, int wanted) =>
        Free()
            .Where(r =>
                r.Flags.HasFlag(ContractGridFlags.Derelict)
                && r.Position.Distance(station) <= meters
            )
            .OrderBy(r => r.Position.Distance(station))
            .ThenBy(r => r.Id)
            .Take(Math.Max(0, wanted))
            .ToList();

    /// <summary>
    /// A wreck in an offline partition: nothing on it works and nobody owns it, at once; any other
    /// grid without power once its partition has been offline for offlineFor. A visit brings the
    /// partition online and starts that time anew.
    /// </summary>
    public static bool IsWreck(ContractGridFlags flags, TimeSpan offline, TimeSpan offlineFor) =>
        flags.HasFlag(ContractGridFlags.Wreck)
        || (flags.HasFlag(ContractGridFlags.Unpowered) && offline >= offlineFor);

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
    /// no longer exists. hunterNear: the player who took it is near the target.
    /// </summary>
    public ContractVerdict Judge(
        ContractTarget target,
        bool active,
        ContractPoint? stationAt,
        bool hunterNear,
        double shipyardSearchMeters,
        double salvageRadius,
        DateTime now
    )
    {
        var gone = IsGone(target.Grid, now);
        Grids.TryGetValue(target.Grid, out var row);
        if (hunterNear)
            target.HunterSeen = now;
        if (row != null)
            target.Position = row.Position;
        if (!active)
            return !gone && stationAt != null && Wanted(target.Kind, row.Flags)
                ? ContractVerdict.Keep
                : ContractVerdict.Withdraw;

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
                if (gone || stationAt == null)
                    return ContractVerdict.Fail;
                return AtShipyard(row, stationAt.Value, shipyardSearchMeters, salvageRadius)
                    ? ContractVerdict.Scrap
                    : ContractVerdict.Keep;
            default:
                if (gone)
                    return witnessed ? ContractVerdict.Finish : ContractVerdict.Fail;
                if (!row.Flags.HasFlag(ContractGridFlags.Unpowered))
                    return ContractVerdict.Fail;
                if (
                    stationAt != null
                    && AtShipyard(row, stationAt.Value, shipyardSearchMeters, salvageRadius)
                )
                    return ContractVerdict.Scrap;
                return hunterNear && row.Blocks <= target.Size * DoneShare
                    ? ContractVerdict.Scrap
                    : ContractVerdict.Keep;
        }
    }

    // Once posted, a wreck stays one while it has no power: the contract itself wakes its partition
    private static bool Wanted(char kind, ContractGridFlags flags) =>
        flags.HasFlag(
            kind switch
            {
                'b' => ContractGridFlags.Bounty,
                's' => ContractGridFlags.Derelict,
                _ => ContractGridFlags.Unpowered,
            }
        );

    // Awake too: only a node can scrap it, and a sleeping grid hasn't just been brought there
    private bool AtShipyard(
        ContractGrid row,
        ContractPoint station,
        double search,
        double radius
    ) =>
        Offline?.ContainsKey(row.Id) != true
        && Shipyard(station, search) is { } shipyard
        && row.Position.Distance(shipyard.Position) <= radius;

    /// <summary>The grid is to be closed by the node holding it, and is no candidate any more.</summary>
    public void Scrap(long grid, DateTime now)
    {
        Grids.Remove(grid);
        closing[grid] = now + ClosingFor;
    }

    // --- saved in the world ---

    // contract:kind:grid:station:size:x:y:z:hunterSeen;...
    public string SerializeTargets()
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return string.Join(
            ";",
            Targets.Select(t =>
                $"{t.Key}:{t.Value.Kind}:{t.Value.Grid}:{t.Value.Station}:{N(t.Value.Size)}:"
                + $"{N(t.Value.Position.X)}:{N(t.Value.Position.Y)}:{N(t.Value.Position.Z)}:"
                + t.Value.HunterSeen.Ticks.ToString(CultureInfo.InvariantCulture)
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
            };
        }
    }

    public string SerializeGrids() => string.Join("\n", Grids.Values.Select(g => g.Encode()));

    public void LoadGrids(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var row = ContractGrid.Decode(line);
            Grids[row.Id] = row;
        }
    }
}
