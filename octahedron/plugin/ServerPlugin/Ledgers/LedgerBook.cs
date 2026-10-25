using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPlugin;

/// <summary>A ledger owner: a player (identity id) or a faction (faction id).</summary>
public readonly struct LedgerOwner : IEquatable<LedgerOwner>
{
    public readonly bool Faction;
    public readonly long Id;

    public LedgerOwner(bool faction, long id)
    {
        Faction = faction;
        Id = id;
    }

    public static LedgerOwner Player(long identityId) => new(false, identityId);

    public static LedgerOwner OfFaction(long factionId) => new(true, factionId);

    public bool IsSet => Id != 0;

    public override string ToString() =>
        (Faction ? "F" : "P") + Id.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string text, out LedgerOwner owner)
    {
        owner = default;
        if (string.IsNullOrEmpty(text) || (text[0] != 'P' && text[0] != 'F'))
            return false;
        if (
            !long.TryParse(
                text.Substring(1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long id
            )
        )
            return false;
        owner = new LedgerOwner(text[0] == 'F', id);
        return true;
    }

    public bool Equals(LedgerOwner other) => Faction == other.Faction && Id == other.Id;

    public override bool Equals(object obj) => obj is LedgerOwner other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode() * 2 + (Faction ? 1 : 0);
}

public enum LedgerAction
{
    /// <summary>Items from outside into the owner's free stock.</summary>
    Deposit,

    /// <summary>Items out of the owner's free stock.</summary>
    Withdraw,

    /// <summary>Free stock of the owner to the free stock of Other.</summary>
    Transfer,

    /// <summary>Free stock of the owner into the owner's reservation, created if new.</summary>
    Reserve,

    /// <summary>Items from outside straight into the owner's reservation, created if new.</summary>
    Fill,

    /// <summary>Reservation back to its owner's free stock. No items: all of it.</summary>
    Release,

    /// <summary>Reservation to the free stock of Other, a sale. No items: all of it.</summary>
    Give,

    /// <summary>Items out of the reservation, used up. No items: all of it.</summary>
    Take,
}

/// <summary>
/// One change of a station's ledger. Amounts are MyFixedPoint raw values (millionths of a unit),
/// items are "Type/Subtype" without the MyObjectBuilder_ prefix, for example "Ingot/Iron".
/// </summary>
public sealed class LedgerOp
{
    public LedgerAction Action;
    public string Station;
    public LedgerOwner Owner;

    // Transfer and Give: the receiver
    public LedgerOwner Other;

    // Reserve, Fill, Release, Give and Take
    public string Reservation;

    // Reserve and Fill, when they create the reservation: it counts toward the owner's limit
    public bool Counted;

    public List<KeyValuePair<string, long>> Items = new();

    public LedgerOp Add(string item, long raw)
    {
        Items.Add(new KeyValuePair<string, long>(item, raw));
        return this;
    }

    // One op per line: the fixed fields, then item and amount pairs, tab-separated
    public string Encode()
    {
        var text = new StringBuilder();
        text.Append(Action)
            .Append('\t')
            .Append(Station)
            .Append('\t')
            .Append(Owner)
            .Append('\t')
            .Append(Other.IsSet ? Other.ToString() : "")
            .Append('\t')
            .Append(Reservation ?? "")
            .Append('\t')
            .Append(Counted ? '1' : '0');
        foreach (var item in Items)
            text.Append('\t')
                .Append(item.Key)
                .Append('\t')
                .Append(item.Value.ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }

    public static LedgerOp Decode(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 6 || (f.Length - 6) % 2 != 0)
            throw new FormatException("Malformed ledger op");
        var op = new LedgerOp
        {
            Action = (LedgerAction)Enum.Parse(typeof(LedgerAction), f[0]),
            Station = f[1],
            Reservation = f[4].Length == 0 ? null : f[4],
            Counted = f[5] == "1",
        };
        if (!LedgerOwner.TryParse(f[2], out op.Owner))
            throw new FormatException("Malformed ledger owner");
        if (f[3].Length > 0 && !LedgerOwner.TryParse(f[3], out op.Other))
            throw new FormatException("Malformed ledger owner");
        for (int i = 6; i < f.Length; i += 2)
            op.Add(f[i], long.Parse(f[i + 1], CultureInfo.InvariantCulture));
        return op;
    }

    public static string EncodeBatch(IEnumerable<LedgerOp> ops) =>
        string.Join("\n", ops.Select(op => op.Encode()));

    public static List<LedgerOp> DecodeBatch(string text) =>
        text.Split('\n').Where(l => l.Length > 0).Select(Decode).ToList();
}

/// <summary>What the book needs to know about the world: items, limits and who exists.</summary>
public interface ILedgerRules
{
    /// <summary>Mass of one unit in kg and whether amounts must be whole; false for an unknown item.</summary>
    bool Item(string item, out double massKg, out bool integral);

    /// <summary>The most an owner may keep at one station, in kg; 0 or less: no limit.</summary>
    double LimitKg(LedgerOwner owner);

    bool KnownStation(string station);

    bool KnownOwner(LedgerOwner owner);
}

/// <summary>
/// The warehouse ledgers of every station: how much of each item every player and faction owns
/// there, free or reserved. Pure bookkeeping without game types, so it is tested on its own
/// (tests/ledgers). Runs on one thread, the World Authority's game thread on a cluster.
/// </summary>
public sealed class LedgerBook
{
    public const long Unit = 1_000_000;
    private const string Header = "octahedron-ledgers 1";

    public sealed class Reservation
    {
        public LedgerOwner Owner;
        public bool Counted;
        public Dictionary<string, long> Items = new();

        public Reservation Clone() =>
            new()
            {
                Owner = Owner,
                Counted = Counted,
                Items = new Dictionary<string, long>(Items),
            };
    }

    public sealed class Station
    {
        public Dictionary<LedgerOwner, Dictionary<string, long>> Free = new();
        public Dictionary<string, Reservation> Reservations = new();

        public Station Clone() =>
            new()
            {
                Free = Free.ToDictionary(p => p.Key, p => new Dictionary<string, long>(p.Value)),
                Reservations = Reservations.ToDictionary(p => p.Key, p => p.Value.Clone()),
            };

        public bool Empty => Free.Count == 0 && Reservations.Count == 0;
    }

    private readonly ILedgerRules rules;
    private readonly Dictionary<string, Station> stations = new();

    public LedgerBook(ILedgerRules rules)
    {
        this.rules = rules;
    }

    public IReadOnlyDictionary<string, Station> Stations => stations;

    public Station Get(string station) => stations.TryGetValue(station, out var s) ? s : null;

    /// <summary>The owner's free items at the station; empty if none.</summary>
    public IReadOnlyDictionary<string, long> FreeOf(string station, LedgerOwner owner) =>
        Get(station)?.Free.TryGetValue(owner, out var items) == true
            ? items
            : new Dictionary<string, long>();

    /// <summary>The owner's reservations at the station, by id.</summary>
    public IEnumerable<KeyValuePair<string, Reservation>> ReservationsOf(
        string station,
        LedgerOwner owner
    ) =>
        Get(station)?.Reservations.Where(r => r.Value.Owner.Equals(owner))
        ?? Enumerable.Empty<KeyValuePair<string, Reservation>>();

    /// <summary>What counts toward the owner's limit at the station: free items and counted reservations, kg.</summary>
    public double MassKg(string station, LedgerOwner owner)
    {
        var s = Get(station);
        if (s == null)
            return 0;
        double mass = s.Free.TryGetValue(owner, out var free) ? Mass(free) : 0;
        foreach (var r in s.Reservations.Values)
            if (r.Counted && r.Owner.Equals(owner))
                mass += Mass(r.Items);
        return mass;
    }

    private double Mass(Dictionary<string, long> items)
    {
        double mass = 0;
        foreach (var item in items)
            if (rules.Item(item.Key, out double kg, out _))
                mass += kg * item.Value / Unit;
        return mass;
    }

    /// <summary>
    /// Applies the ops in order, all or none. Returns null when they all applied, else why
    /// not, with nothing changed.
    /// </summary>
    public string Apply(IReadOnlyList<LedgerOp> ops)
    {
        if (ops.Count == 0)
            return "Nothing to do.";

        // A copy of every station the ops touch, put back if one fails
        var backup = new Dictionary<string, Station>();
        foreach (var op in ops)
            if (op.Station != null && !backup.ContainsKey(op.Station))
                backup[op.Station] = Get(op.Station)?.Clone();

        string error;
        try
        {
            error = ApplyAll(ops);
        }
        catch (OverflowException)
        {
            error = "The amount is too large.";
        }

        if (error != null)
        {
            foreach (var b in backup)
                if (b.Value == null)
                    stations.Remove(b.Key);
                else
                    stations[b.Key] = b.Value;
            return error;
        }

        foreach (var name in backup.Keys)
            if (stations.TryGetValue(name, out var s) && s.Empty)
                stations.Remove(name);
        return null;
    }

    private string ApplyAll(IReadOnlyList<LedgerOp> ops)
    {
        // The limit is checked once all ops applied, against the masses before the batch: a
        // batch may take out before it puts in, and an owner over the limit may still lower it
        var before = new Dictionary<(string, LedgerOwner), double>();
        foreach (var op in ops)
        foreach (var owner in new[] { op.Owner, op.Other })
            if (op.Station != null && owner.IsSet)
                before[(op.Station, owner)] = MassKg(op.Station, owner);

        var raised = new HashSet<(string, LedgerOwner)>();
        foreach (var op in ops)
        {
            string error = Check(op) ?? Apply(op, raised);
            if (error != null)
                return error;
        }

        foreach (var key in raised)
        {
            var (station, owner) = key;
            double limit = rules.LimitKg(owner);
            double mass = MassKg(station, owner);
            if (limit > 0 && mass > limit && mass > before[key] + 1e-9)
                return $"That would put {(owner.Faction ? "the faction" : "you")} at {Tonnes(mass)} at {station}, over the limit of {Tonnes(limit)}.";
        }
        return null;
    }

    public static string Tonnes(double kg) =>
        (kg / 1000).ToString("#,0.###", CultureInfo.InvariantCulture) + " t";

    private string Check(LedgerOp op)
    {
        if (!ValidName(op.Station) || !rules.KnownStation(op.Station))
            return $"Unknown station '{op.Station}'.";
        if (!op.Owner.IsSet || !rules.KnownOwner(op.Owner))
            return $"Unknown owner {op.Owner}.";
        bool withOther = op.Action is LedgerAction.Transfer or LedgerAction.Give;
        if (withOther && (!op.Other.IsSet || !rules.KnownOwner(op.Other)))
            return $"Unknown owner {op.Other}.";
        if (withOther && op.Other.Equals(op.Owner))
            return "The receiver is the owner.";
        bool reserved =
            op.Action
            is LedgerAction.Reserve
                or LedgerAction.Fill
                or LedgerAction.Release
                or LedgerAction.Give
                or LedgerAction.Take;
        if (reserved && !ValidName(op.Reservation))
            return "No valid reservation id.";
        bool all = op.Action is LedgerAction.Release or LedgerAction.Give or LedgerAction.Take;
        if (op.Items.Count == 0 && !all)
            return "No items.";
        foreach (var item in op.Items)
        {
            if (!ValidName(item.Key) || !rules.Item(item.Key, out _, out bool integral))
                return $"Unknown item {item.Key}.";
            if (item.Value <= 0)
                return $"The amount of {item.Key} must be positive.";
            if (integral && item.Value % Unit != 0)
                return $"{item.Key} comes in whole units only.";
        }
        return null;
    }

    // Station names, reservation ids and items go into tab-separated lines
    public static bool ValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['\t', '\n', '\r']) < 0;

    private string Apply(LedgerOp op, HashSet<(string, LedgerOwner)> raised)
    {
        if (!stations.TryGetValue(op.Station, out var station))
            stations[op.Station] = station = new Station();

        void Raise(LedgerOwner owner) => raised.Add((op.Station, owner));

        Reservation reservation = null;
        if (op.Reservation != null)
        {
            station.Reservations.TryGetValue(op.Reservation, out reservation);
            if (reservation != null && !reservation.Owner.Equals(op.Owner))
                return $"Reservation {op.Reservation} belongs to someone else.";
            bool creates = op.Action is LedgerAction.Reserve or LedgerAction.Fill;
            if (reservation == null && !creates)
                return $"No reservation {op.Reservation} at {op.Station}.";
        }

        switch (op.Action)
        {
            case LedgerAction.Deposit:
                Raise(op.Owner);
                Add(FreeItems(station, op.Owner), op.Items);
                return null;
            case LedgerAction.Withdraw:
                return Remove(station, op.Owner, op.Items);
            case LedgerAction.Transfer:
                Raise(op.Other);
                return Remove(station, op.Owner, op.Items)
                    ?? Add(FreeItems(station, op.Other), op.Items);
            case LedgerAction.Reserve:
            case LedgerAction.Fill:
                if (reservation == null)
                    station.Reservations[op.Reservation] = reservation = new Reservation
                    {
                        Owner = op.Owner,
                        Counted = op.Counted,
                    };
                if (op.Action == LedgerAction.Fill && reservation.Counted)
                    Raise(op.Owner);
                return (
                        op.Action == LedgerAction.Reserve
                            ? Remove(station, op.Owner, op.Items)
                            : null
                    ) ?? Add(reservation.Items, op.Items);
            default:
                var items = op.Items.Count > 0 ? op.Items : reservation.Items.ToList();
                string error = Remove(reservation.Items, items, $"reservation {op.Reservation}");
                if (error != null)
                    return error;
                if (reservation.Items.Count == 0)
                    station.Reservations.Remove(op.Reservation);
                if (op.Action == LedgerAction.Release)
                    return Add(FreeItems(station, op.Owner), items);
                if (op.Action == LedgerAction.Give)
                {
                    Raise(op.Other);
                    return Add(FreeItems(station, op.Other), items);
                }
                return null;
        }
    }

    private static Dictionary<string, long> FreeItems(Station station, LedgerOwner owner)
    {
        if (!station.Free.TryGetValue(owner, out var items))
            station.Free[owner] = items = new Dictionary<string, long>();
        return items;
    }

    private static string Add(Dictionary<string, long> to, List<KeyValuePair<string, long>> items)
    {
        foreach (var item in items)
            to[item.Key] = checked((to.TryGetValue(item.Key, out long had) ? had : 0) + item.Value);
        return null;
    }

    private static string Remove(
        Station station,
        LedgerOwner owner,
        List<KeyValuePair<string, long>> items
    )
    {
        string error = Remove(FreeItems(station, owner), items, "the ledger");
        if (station.Free[owner].Count == 0)
            station.Free.Remove(owner);
        return error;
    }

    private static string Remove(
        Dictionary<string, long> from,
        List<KeyValuePair<string, long>> items,
        string where
    )
    {
        foreach (var item in items)
        {
            long had = from.TryGetValue(item.Key, out long h) ? h : 0;
            if (had < item.Value)
                return $"Only {Units(had)} {item.Key} in {where}, not {Units(item.Value)}.";
            if (had == item.Value)
                from.Remove(item.Key);
            else
                from[item.Key] = had - item.Value;
        }
        return null;
    }

    public static string Units(long raw) =>
        ((decimal)raw / Unit).ToString("#,0.######", CultureInfo.InvariantCulture);

    /// <summary>A copy of one station's ledger, all of it or only one owner's part.</summary>
    public LedgerBook Extract(string station, LedgerOwner? owner)
    {
        var copy = new LedgerBook(rules);
        var s = Get(station)?.Clone();
        if (s == null)
            return copy;
        if (owner is { } only)
        {
            s.Free = s.Free.Where(f => f.Key.Equals(only)).ToDictionary(f => f.Key, f => f.Value);
            s.Reservations = s
                .Reservations.Where(r => r.Value.Owner.Equals(only))
                .ToDictionary(r => r.Key, r => r.Value);
        }
        if (!s.Empty)
            copy.stations[station] = s;
        return copy;
    }

    /// <summary>Every owner with something at the station.</summary>
    public IEnumerable<LedgerOwner> OwnersAt(string station)
    {
        var s = Get(station);
        if (s == null)
            return Enumerable.Empty<LedgerOwner>();
        return s.Free.Keys.Concat(s.Reservations.Values.Select(r => r.Owner)).Distinct();
    }

    // --- persistence: one line per station, free stock and reservation, tab-separated ---

    public string Serialize()
    {
        var text = new StringBuilder(Header).Append('\n');
        foreach (var station in stations.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            text.Append("S\t").Append(station.Key).Append('\n');
            foreach (var free in station.Value.Free.OrderBy(f => f.Key.ToString()))
            {
                text.Append("H\t").Append(free.Key);
                AppendItems(text, free.Value);
            }
            foreach (
                var r in station.Value.Reservations.OrderBy(r => r.Key, StringComparer.Ordinal)
            )
            {
                text.Append("R\t")
                    .Append(r.Key)
                    .Append('\t')
                    .Append(r.Value.Owner)
                    .Append('\t')
                    .Append(r.Value.Counted ? '1' : '0');
                AppendItems(text, r.Value.Items);
            }
        }
        return text.ToString();
    }

    private static void AppendItems(StringBuilder text, Dictionary<string, long> items)
    {
        foreach (var item in items.OrderBy(i => i.Key, StringComparer.Ordinal))
            text.Append('\t')
                .Append(item.Key)
                .Append('\t')
                .Append(item.Value.ToString(CultureInfo.InvariantCulture));
        text.Append('\n');
    }

    /// <summary>Reads what <see cref="Serialize"/> wrote. Items are taken as saved, even unknown ones.</summary>
    public static LedgerBook Deserialize(string text, ILedgerRules rules)
    {
        var book = new LedgerBook(rules);
        var lines = text.Split('\n');
        if (lines[0] != Header)
            throw new FormatException("Not a ledger: " + lines[0]);
        Station station = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
                continue;
            var f = line.Split('\t');
            switch (f[0])
            {
                case "S":
                    book.stations[f[1]] = station = new Station();
                    break;
                case "H" when station != null && LedgerOwner.TryParse(f[1], out var owner):
                    station.Free[owner] = ReadItems(f, 2);
                    break;
                case "R" when station != null && LedgerOwner.TryParse(f[2], out var owner):
                    station.Reservations[f[1]] = new Reservation
                    {
                        Owner = owner,
                        Counted = f[3] == "1",
                        Items = ReadItems(f, 4),
                    };
                    break;
                default:
                    throw new FormatException("Malformed ledger line: " + line);
            }
        }
        return book;
    }

    private static Dictionary<string, long> ReadItems(string[] f, int start)
    {
        var items = new Dictionary<string, long>();
        for (int i = start; i + 1 < f.Length; i += 2)
            items[f[i]] = long.Parse(f[i + 1], CultureInfo.InvariantCulture);
        return items;
    }
}
