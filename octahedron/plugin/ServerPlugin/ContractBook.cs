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

    // Movable, big, and nobody's or a wreck faction's
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

    // Big, and nobody's or a wreck faction's, static or not
    Abandoned = 512,

    // Big, and an NPC's of another faction than OCT and the wreck factions
    Npc = 1024,

    // Big, and a bot player's (ShipyardBotSteamIds)
    Bot = 2048,

    // Movable, with no working cockpit or remote control
    Disabled = 4096,

    // Nobody's or a wreck faction's, smaller than a wreck: an area cleanup contract's piece
    Debris = 8192,
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

    // What its blocks' components are worth at OCT's market prices, SC: no cargo, no tank fill
    public double Value;

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
            Tower.ToString(CultureInfo.InvariantCulture),
            N(Value)
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
            // Absent in rows saved before component values
            Value = p.Length > 15 ? D(15) : 0,
        };
    }
}

// A grid of an offline partition as the gateway's row says it was at the freeze. A null fact is unknown:
// a row read back from a Final save has none of them.
internal sealed record ContractOfflineGrid(
    long Id,
    string Name,
    bool Static,
    int Blocks,
    ContractPoint Position,
    long[] Owners,
    double? MassKg,
    long? Group,
    bool? Functional,
    bool? Powered,
    bool? Station
);

// A physical group of those that is a wreck candidate, and its grids
internal sealed record ContractOfflineRow(ContractGrid Grid, long[] Members);

// What a bounty, salvage, wreck or area contract watches. Kind: b, s (salvage, before SE-1385), w or
// a. Size: the threat score of a bounty's target, the blocks of a wreck or the radius of an area
// when posted. Grid: an area's first piece.
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

    // Wrecks and areas: what it was worth when posted, SC; the most a tow-in pays
    public double Value;

    // Areas: the debris pieces it was made for
    public long[] Members = [];

    public IEnumerable<long> Grids => Members.Length > 0 ? Members : [Grid];
}

// A wreck or a debris area to post: Grid is the wreck's row, or an area's first piece
internal sealed record ContractHunt(
    char Kind,
    ContractGrid Grid,
    ContractPoint Position,
    double Value,
    double Radius,
    long[] Members
);

// What makes a grid a wreck hunt's target. OwnerAway: the owner and their faction haven't been on
// for the days, an NPC always. Near: a player is within sync range of the point.
internal sealed class ContractHuntRules
{
    public double MinValue;
    public TimeSpan NpcDown = TimeSpan.FromMinutes(10);
    public TimeSpan BotDown = TimeSpan.FromHours(1);
    public double DebrisLink = 300;
    public int DebrisMinPieces = 5;
    public double DebrisMaxRadius = 2_000;
    public Func<long, bool> OwnerAway = _ => false;
    public Func<ContractPoint, bool> PlayerNear = _ => false;
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

    // Tows over the player's limits, grid to who and at which station, until the grid is gone. The
    // node that holds the grid breaks it down.
    public readonly Dictionary<long, (long Player, string Station)> Breakdowns = new();

    // Grids in offline partitions and since when, from the gateway; null while unknown, when nothing
    // can be said about a grid no report names
    public Dictionary<long, DateTime> Offline;

    // The gateway's rows that could be wrecks, for the grids that went offline with no report naming
    // them. Not saved: the gateway has them. A contract on one puts its row into Grids.
    public List<ContractOfflineRow> OfflineRows = [];

    // Reports served since the authority started, for !oct contracts
    public long Reports;

    public ContractHuntRules Rules = new();

    // Wrecks handed in that their player may have scrapped instead of keeping them, until when.
    // Not saved: a restart ends the choice.
    public readonly Dictionary<long, (long Player, string Station, DateTime Until)> Scrappable =
        new();

    // NPC and bot grids without power or control, since when; not saved, a restart starts anew
    private readonly Dictionary<long, DateTime> downSince = new();

    private readonly Dictionary<string, DateTime> reports = new();
    private readonly Dictionary<long, DateTime> closing = new();

    // Chat for a player: the World Authority has no clients, so each node gets it once and the one
    // with the player delivers it
    private sealed record Message(long Player, string Text, DateTime Until, HashSet<string> Told);

    private readonly List<Message> messages = [];

    // What the nodes must report every time: the targets, and the grids still to be given away
    public IEnumerable<long> Watched =>
        Targets
            .Values.SelectMany(t => t.Grids)
            .Concat(Given.Where(g => g.Value != 0).Select(g => g.Key))
            .Concat(Breakdowns.Keys)
            .Distinct();

    /// <summary>
    /// A node's rows. The answer, comma separated: "watched ids;ids to close;grid:player to give;
    /// player:base64 text to tell;grid:player:base64 station to break down;grid:player:base64
    /// station the player may scrap".
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
                row.Flags.HasFlag(ContractGridFlags.Npc) || row.Flags.HasFlag(ContractGridFlags.Bot)
            )
            {
                if (
                    row.Flags.HasFlag(ContractGridFlags.Unpowered)
                    || row.Flags.HasFlag(ContractGridFlags.Disabled)
                )
                    downSince.TryAdd(row.Id, now);
                else
                    downSince.Remove(row.Id);
            }
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
            + string.Join(",", Tells(node, now))
            + ";"
            + string.Join(",", Breakdowns.Select(b => $"{b.Key}:{BreakdownText(b.Value)}"))
            + ";"
            + string.Join(
                ",",
                Scrappable
                    .Where(s => now < s.Value.Until)
                    .Select(s => $"{s.Key}:{BreakdownText((s.Value.Player, s.Value.Station))}")
            );
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
        List<(long Player, string Text)> Tell,
        List<(long Grid, long Player, string Station)> BreakDown,
        List<(long Grid, long Player, string Station)> Scrappable
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
        var breakDown = parts[4]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseBreakdown)
            .ToList();
        // Absent in an older World Authority's answer
        var scrappable = (parts.Length > 5 ? parts[5] : "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseBreakdown)
            .ToList();
        return (
            Ids(parts[0]).ToHashSet(),
            Ids(parts[1]).ToList(),
            give,
            tell,
            breakDown,
            scrappable
        );
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
        foreach (var id in Breakdowns.Keys.Where(g => IsGone(g, now)).ToList())
            Breakdowns.Remove(id);
        var targeted = Watched.ToHashSet();
        foreach (
            var id in Grids
                .Values.Where(r => !targeted.Contains(r.Id) && now - r.Seen >= StaleAfter)
                .Select(r => r.Id)
                .ToList()
        )
            Grids.Remove(id);
        foreach (var id in downSince.Keys.Where(g => !Grids.ContainsKey(g)).ToList())
            downSince.Remove(id);
        foreach (var id in Scrappable.Keys.Where(g => now >= Scrappable[g].Until).ToList())
            Scrappable.Remove(id);
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
        var targeted = Targets.Values.SelectMany(t => t.Grids).ToHashSet();
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
    /// The gateway's rows as a node would report them, one per physical group, the rules of
    /// ContractGrids.Describe: a group with an unknown fact is no wreck. What the rows can't tell is
    /// fixed once the partition wakes and a node reports the grid: who built it, a seat in use, a
    /// safe zone. isOct: an identity of OCT's faction.
    /// </summary>
    public static List<ContractOfflineRow> OfflineGroups(
        IEnumerable<ContractOfflineGrid> rows,
        int minBlocks,
        Func<long, bool> isOct,
        DateTime now
    )
    {
        var found = new List<ContractOfflineRow>();
        foreach (var group in rows.GroupBy(r => r.Group))
        {
            // Mass 0 says nothing either: an older node reads a static grid's physics, which has none
            var known = group.All(r =>
                r.MassKg > 0
                && r.Group != null
                && r.Functional != null
                && r.Powered != null
                && r.Station != null
            );
            if (
                !known
                || group.Sum(r => r.Blocks) < minBlocks
                || group.Any(r => r.Station == true || r.Owners.Any(isOct) || IsLaneComputer(r))
            )
                continue;
            var flags = ContractGridFlags.None;
            if (group.All(r => r.Powered == false))
                flags |= ContractGridFlags.Unpowered;
            if (group.All(r => r.Owners.Length == 0 && r.Functional == false))
                flags |= ContractGridFlags.Wreck;
            if (group.All(r => r.Owners.Length == 0))
                flags |= ContractGridFlags.Abandoned;
            if (group.All(r => r.Owners.Length == 0 && !r.Static))
                flags |= ContractGridFlags.Derelict;
            if (flags == ContractGridFlags.None)
                continue;
            // The same grid every time, so a restart finds its contract
            var biggest = group.OrderByDescending(r => r.Blocks).ThenBy(r => r.Id).First();
            found.Add(
                new ContractOfflineRow(
                    new ContractGrid
                    {
                        Id = biggest.Id,
                        Flags = flags,
                        Position = biggest.Position,
                        Blocks = group.Sum(r => r.Blocks),
                        MassKg = group.Sum(r => r.MassKg.Value),
                        Name = biggest.Name,
                        Seen = now,
                        Owner = biggest
                            .Owners.Concat(group.SelectMany(r => r.Owners))
                            .FirstOrDefault(),
                    },
                    group.Select(r => r.Id).ToArray()
                )
            );
        }
        return found;
    }

    // ContractGrids looks for the block; a row has only the grid. build_world.py names them.
    private static bool IsLaneComputer(ContractOfflineGrid row) =>
        row.Static
        && row.Blocks == 1
        && row.Owners.Length == 0
        && row.Name?.StartsWith("TL ", StringComparison.Ordinal) == true;

    /// <summary>
    /// The candidates only the gateway knows. What a node reported wins for the whole group: a row a
    /// load has claimed stays listed until it drops, and a target or a given grid is reported by id.
    /// </summary>
    public IEnumerable<ContractGrid> Unreported() =>
        OfflineRows
            .Where(r =>
                !r.Members.Any(m =>
                    Grids.ContainsKey(m) || Given.ContainsKey(m) || closing.ContainsKey(m)
                )
            )
            .Select(r => r.Grid);

    /// <summary>
    /// A wreck hunt's target: nobody's or a wreck faction's, movable or dead; an NPC's out of power
    /// or control for Rules.NpcDown, a bot's out of power for Rules.BotDown; any other unpowered
    /// grid whose owner is away. Never one someone sits in or in a safe zone, nor debris.
    /// </summary>
    public bool IsTarget(ContractGrid r, DateTime now)
    {
        var f = r.Flags;
        if (
            f.HasFlag(ContractGridFlags.Seated)
            || InActiveSafeZone(r)
            || f.HasFlag(ContractGridFlags.Debris)
        )
            return false;
        var unpowered = f.HasFlag(ContractGridFlags.Unpowered);
        if (
            f.HasFlag(ContractGridFlags.Derelict)
            || f.HasFlag(ContractGridFlags.Wreck)
            || (f.HasFlag(ContractGridFlags.Abandoned) && unpowered)
        )
            return true;
        if (f.HasFlag(ContractGridFlags.Npc))
            return (unpowered || f.HasFlag(ContractGridFlags.Disabled))
                && Down(r, now) >= Rules.NpcDown;
        if (f.HasFlag(ContractGridFlags.Bot))
            return unpowered && Down(r, now) >= Rules.BotDown;
        return unpowered && r.Owner != 0 && Rules.OwnerAway(r.Owner);
    }

    private TimeSpan Down(ContractGrid r, DateTime now) =>
        downSince.TryGetValue(r.Id, out var since) ? now - since : TimeSpan.Zero;

    // Nothing works on it for its owner: power coming back on an owned one means the owner is back
    private static bool Dead(ContractGridFlags f) =>
        f.HasFlag(ContractGridFlags.Unpowered)
        || f.HasFlag(ContractGridFlags.Abandoned)
        || (f.HasFlag(ContractGridFlags.Npc) && f.HasFlag(ContractGridFlags.Disabled));

    /// <summary>
    /// What a grid's components are worth, SC. A gateway's row has only the mass of its blocks: it
    /// is priced at what the reported grids are worth per kg, until its partition wakes.
    /// </summary>
    public double Value(ContractGrid r)
    {
        if (r.Value > 0)
            return r.Value;
        double value = 0,
            mass = 0;
        foreach (var g in Grids.Values.Where(g => g.Value > 0 && g.MassKg > 0))
        {
            value += g.Value;
            mass += g.MassKg;
        }
        return mass > 0 ? r.MassKg * value / mass : 0;
    }

    /// <summary>
    /// The wrecks and debris areas to post, anywhere, the most valuable first, each at the nearest
    /// of the stations, at most wanted(station) there. Nothing a player is near, nothing worth less
    /// than Rules.MinValue, and no wreck already at its station's shipyard: nobody would tow it.
    /// </summary>
    public List<(ContractHunt Hunt, long Station)> Hunts(
        IReadOnlyCollection<(long Id, ContractPoint At)> stations,
        Func<long, int> wanted,
        double shipyardSearchMeters,
        double salvageRadius,
        DateTime now
    )
    {
        if (stations.Count == 0)
            return [];
        (long Id, ContractPoint At) Nearest(ContractPoint p) =>
            stations.OrderBy(s => s.At.Distance(p)).First();
        var wrecks = Free()
            .Concat(Unreported())
            .Where(r => IsTarget(r, now))
            .Select(r => new ContractHunt('w', r, r.Position, Value(r), 0, []))
            .Where(h =>
                !InReach(h.Position, Nearest(h.Position).At, shipyardSearchMeters, salvageRadius)
            );
        var picked = new List<(ContractHunt, long)>();
        var left = new Dictionary<long, int>();
        foreach (
            var hunt in wrecks
                .Concat(Areas())
                .Where(h => h.Value >= Rules.MinValue)
                .OrderByDescending(h => h.Value)
                .ThenBy(h => h.Grid.Id)
        )
        {
            var station = Nearest(hunt.Position).Id;
            if (!left.TryGetValue(station, out var n))
                n = left[station] = wanted(station);
            if (n <= 0 || Rules.PlayerNear(hunt.Position))
                continue;
            left[station] = n - 1;
            picked.Add((hunt, station));
        }
        return picked;
    }

    /// <summary>
    /// Debris pieces no contract has, in clusters: pieces within Rules.DebrisLink of another one of
    /// the cluster. One with Rules.DebrisMinPieces pieces or Rules.MinValue worth is an area: around
    /// the pieces' center, 50 m past the farthest piece, at most Rules.DebrisMaxRadius, which leaves
    /// the pieces farther out to another area.
    /// </summary>
    public List<ContractHunt> Areas()
    {
        var pieces = Free()
            .Where(r =>
                r.Flags.HasFlag(ContractGridFlags.Debris)
                && !r.Flags.HasFlag(ContractGridFlags.Seated)
                && Offline?.ContainsKey(r.Id) != true
            )
            .OrderBy(r => r.Id)
            .ToList();
        var cluster = Enumerable.Range(0, pieces.Count).ToArray();
        int Root(int i) => cluster[i] == i ? i : cluster[i] = Root(cluster[i]);
        for (var i = 0; i < pieces.Count; i++)
        for (var j = i + 1; j < pieces.Count; j++)
            if (pieces[i].Position.Distance(pieces[j].Position) <= Rules.DebrisLink)
                cluster[Root(j)] = Root(i);
        var areas = new List<ContractHunt>();
        foreach (var group in Enumerable.Range(0, pieces.Count).GroupBy(Root))
        {
            var members = group.Select(i => pieces[i]).ToList();
            var center = new ContractPoint(
                members.Average(m => m.Position.X),
                members.Average(m => m.Position.Y),
                members.Average(m => m.Position.Z)
            );
            members = members
                .Where(m => m.Position.Distance(center) <= Rules.DebrisMaxRadius - 50)
                .ToList();
            if (members.Count == 0)
                continue;
            var value = members.Sum(Value);
            if (members.Count < Rules.DebrisMinPieces && value < Rules.MinValue)
                continue;
            areas.Add(
                new ContractHunt(
                    'a',
                    members[0],
                    center,
                    value,
                    Math.Min(
                        Rules.DebrisMaxRadius,
                        members.Max(m => m.Position.Distance(center)) + 50
                    ),
                    members.Select(m => m.Id).ToArray()
                )
            );
        }
        return areas;
    }

    // An area's pieces still in it
    public List<ContractGrid> Left(ContractTarget area, DateTime now) =>
        area
            .Members.Where(m => !IsGone(m, now))
            .Select(m => Grids[m])
            .Where(r => r.Position.Distance(area.Position) <= area.Size)
            .ToList();

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
        if (target.Kind == 'a')
            return JudgeArea(target, active, stationAt, hunterNear, now);
        var gone = IsGone(target.Grid, now);
        Grids.TryGetValue(target.Grid, out var row);
        if (hunterNear)
            target.HunterSeen = now;
        if (row != null)
            target.Position = row.Position;
        // Posted from a gateway's row, which can't tell: a player can't be paid for their own disowned ship
        if (row != null && target.Builder == 0)
            target.Builder = row.Builder;
        if (!active)
            return
                !gone
                && stationAt != null
                && !Given.ContainsKey(target.Grid)
                && !InActiveSafeZone(row)
                && target.Kind switch
                {
                    'b' => row.Flags.HasFlag(ContractGridFlags.Bounty),
                    's' => Salvageable(row.Flags),
                    _ => IsTarget(row, now),
                }
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
                if (Claimed(row, target, hunter) || (!Dead(row.Flags) && row.Owner != hunter))
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

    // A salvage contract posted before SE-1385
    private static bool Salvageable(ContractGridFlags flags) =>
        !flags.HasFlag(ContractGridFlags.Seated) && flags.HasFlag(ContractGridFlags.Derelict);

    // An area is done when none of its pieces is left in it, with the player who took it near
    private ContractVerdict JudgeArea(
        ContractTarget area,
        bool active,
        ContractPoint? stationAt,
        bool hunterNear,
        DateTime now
    )
    {
        var empty = Left(area, now).Count == 0;
        if (!active)
            return !empty && stationAt != null ? ContractVerdict.Keep : ContractVerdict.Withdraw;
        return empty && hunterNear ? ContractVerdict.Finish : ContractVerdict.Keep;
    }

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

    // Given counts it as handled too: no contract and no other tow takes it now
    public void BreakDown(long grid, long player, string station)
    {
        Given[grid] = 0;
        Breakdowns[grid] = (player, station);
    }

    private static string BreakdownText((long Player, string Station) breakdown) =>
        $"{breakdown.Player}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(breakdown.Station))}";

    private static (long Grid, long Player, string Station) ParseBreakdown(string text)
    {
        var p = text.Split(':');
        return (
            long.Parse(p[0], CultureInfo.InvariantCulture),
            long.Parse(p[1], CultureInfo.InvariantCulture),
            Encoding.UTF8.GetString(Convert.FromBase64String(p[2]))
        );
    }

    // --- saved in the world ---

    // contract:kind:grid:station:size:x:y:z:hunterSeen:owner:builder:value:member|member;...
    public string SerializeTargets()
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return string.Join(
            ";",
            Targets.Select(t =>
                $"{t.Key}:{t.Value.Kind}:{t.Value.Grid}:{t.Value.Station}:{N(t.Value.Size)}:"
                + $"{N(t.Value.Position.X)}:{N(t.Value.Position.Y)}:{N(t.Value.Position.Z)}:"
                + $"{t.Value.HunterSeen.Ticks.ToString(CultureInfo.InvariantCulture)}:"
                + $"{t.Value.Owner}:{t.Value.Builder}:{N(t.Value.Value)}:"
                + string.Join("|", t.Value.Members)
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
                Value = p.Length > 11 ? D(p[11]) : 0,
                Members =
                    p.Length > 12
                        ? p[12]
                            .Split('|', StringSplitOptions.RemoveEmptyEntries)
                            .Select(L)
                            .ToArray()
                        : [],
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

    public string SerializeBreakdowns() =>
        string.Join(";", Breakdowns.Select(b => $"{b.Key}:{BreakdownText(b.Value)}"));

    public void LoadBreakdowns(string text)
    {
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (grid, player, station) = ParseBreakdown(entry);
            Breakdowns[grid] = (player, station);
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
