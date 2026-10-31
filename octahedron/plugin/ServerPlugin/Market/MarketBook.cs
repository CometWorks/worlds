using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ServerPlugin;

/// <summary>The market's numbers, from the config.</summary>
public sealed class MarketSettings
{
    // On top of what OCT sells for, off what it pays
    public int TaxPercent = 2;
    public int OctBuyPercent = 100;
    public int OctSellPercent = 100;
    public int SpecialtyPercent = 120;
    public double SpecialtyDailyKg = 50_000;
    public double OctMaxStockKg = 200_000;

    // Net kg bought (or sold) that double (or halve) OCT's prices at a station, before decay
    public double DriftKg = 100_000;
    public double DriftHalfLifeHours = 24;
    public double DriftMaxFactor = 2;

    // Goods OCT sells at a fixed price (milli-SC per unit) by station and item, from a stock
    // kept topped up to FixedStock (raw): the zone chips
    public Dictionary<(string Station, string Item), long> FixedAsks = new();
    public long FixedStock;
}

/// <summary>What the market needs from the game: time, items, prices and accounts.</summary>
public interface IMarketWorld
{
    /// <summary>Unix seconds.</summary>
    long Now { get; }

    /// <summary>The account of OCT, the market maker.</summary>
    long Oct { get; }

    /// <summary>Mass of one unit in kg and whether amounts are whole; false for an unknown item.</summary>
    bool Item(string item, out double massKg, out bool integral);

    /// <summary>OCT's reference price of a catalog item at the station in milli-SC per unit; 0 when OCT doesn't trade it there.</summary>
    long Reference(string station, string item);

    /// <summary>The item's economy base price in milli-SC per unit, for items OCT has no price for; 0 if none.</summary>
    long BasePrice(string item);

    /// <summary>The station's catalog, the items OCT trades there.</summary>
    IEnumerable<string> Catalog(string station);

    /// <summary>Whether OCT buys the item at the station's specialty price.</summary>
    bool Specialty(string station, string item);

    /// <summary>The account's balance in SC, -1 without an account.</summary>
    long Balance(long account);

    bool ChangeBalance(long account, long amount);
}

public readonly struct MarketResult
{
    public readonly bool Ok;
    public readonly string Message;

    public MarketResult(bool ok, string message)
    {
        Ok = ok;
        Message = message;
    }

    public static MarketResult No(string message) => new(false, message);
}

/// <summary>What a purchase from OCT's stock would take: at most what it asked for.</summary>
public struct MarketFill
{
    public long Amount;

    // Milli-SC per unit
    public long Price;

    // SC
    public long Cost;
}

/// <summary>An item as a station's Store block shows it. Prices in milli-SC per unit, whole SC; amounts raw.</summary>
public readonly record struct StoreRow(string Item, long Ask, long Stock, long Bid, long Wanted)
{
    public string Encode() =>
        string.Join(
            "\t",
            Item,
            Ask.ToString(CultureInfo.InvariantCulture),
            Stock.ToString(CultureInfo.InvariantCulture),
            Bid.ToString(CultureInfo.InvariantCulture),
            Wanted.ToString(CultureInfo.InvariantCulture)
        );

    public static List<StoreRow> DecodeAll(string text) =>
        (text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Select(f => new StoreRow(
                f[0],
                long.Parse(f[1], CultureInfo.InvariantCulture),
                long.Parse(f[2], CultureInfo.InvariantCulture),
                long.Parse(f[3], CultureInfo.InvariantCulture),
                long.Parse(f[4], CultureInfo.InvariantCulture)
            ))
            .ToList();
}

/// <summary>
/// The stations' markets: OCT's real stock of each item at each station, and its prices there.
/// Players trade through the game's Store blocks, which show <see cref="Rows"/> and report back
/// here (<see cref="Bought"/>, <see cref="Sold"/>); a paid build buys here directly. Pure
/// bookkeeping without game types, so it is tested on its own (tests/market). Runs on the
/// authority's game thread, the World Authority's on a cluster.
/// </summary>
public sealed class MarketBook
{
    private const string Header = "octahedron-market 2";
    public const long MilliPerSc = 1000;

    private readonly Dictionary<(string Station, string Item), long> stock = new();

    // UTC day number of the specialty purchases counted, UTC hour number of the last restock
    public long Day = -1;
    public long Hour = -1;
    private readonly Dictionary<string, double> specialtyKg = new();

    // Net kg bought from the market (positive) or sold to it, per station and item, and when
    private readonly Dictionary<(string Station, string Item), (double Kg, long At)> drift = new();

    public long Stock(string station, string item) =>
        stock.TryGetValue((station, item), out long raw) ? raw : 0;

    public void SetStock(string station, string item, long raw)
    {
        if (raw > 0)
            stock[(station, item)] = raw;
        else
            stock.Remove((station, item));
    }

    public double SpecialtyBoughtKg(string station) =>
        specialtyKg.TryGetValue(station, out double kg) ? kg : 0;

    // --- prices ---

    /// <summary>SC for an amount at a price, rounded up (what a buyer pays) or down.</summary>
    public static long Cost(long raw, long price, bool up)
    {
        decimal sc = (decimal)raw * price / (Amounts.Unit * MilliPerSc);
        return checked((long)(up ? Math.Ceiling(sc) : Math.Floor(sc)));
    }

    /// <summary>How far OCT's prices of the item at the station have drifted from the reference: 1 = none.</summary>
    public double Factor(MarketSettings s, string station, string item, long now)
    {
        if (s.DriftKg <= 0 || !drift.TryGetValue((station, item), out var d))
            return 1;
        double kg = Decayed(s, d.Kg, d.At, now);
        double factor = Math.Pow(2, kg / s.DriftKg);
        double max = Math.Max(1, s.DriftMaxFactor);
        return Math.Min(max, Math.Max(1 / max, factor));
    }

    private static double Decayed(MarketSettings s, double kg, long at, long now) =>
        s.DriftHalfLifeHours <= 0
            ? kg
            : kg * Math.Pow(0.5, Math.Max(0, now - at) / 3600.0 / s.DriftHalfLifeHours);

    private void Drift(MarketSettings s, string station, string item, double kg, long now)
    {
        var key = (station, item);
        double had = drift.TryGetValue(key, out var d) ? Decayed(s, d.Kg, d.At, now) : 0;
        drift[key] = (had + kg, now);
    }

    // A Store block's prices are whole SC per unit
    private long Price(
        IMarketWorld w,
        MarketSettings s,
        string station,
        string item,
        int percent,
        int tax
    )
    {
        double sc =
            w.Reference(station, item)
            / (double)MilliPerSc
            * percent
            / 100
            * (100 + tax)
            / 100
            * Factor(s, station, item, w.Now);
        return (long)Math.Round(sc, MidpointRounding.AwayFromZero) * MilliPerSc;
    }

    /// <summary>What OCT sells the item for at the station, milli-SC per unit; 0: it doesn't. A specialty sells at no less than the station pays for it, or the Store itself would be a way to make money.</summary>
    public long OctAsk(IMarketWorld w, MarketSettings s, string station, string item)
    {
        if (s.FixedAsks.TryGetValue((station, item), out long ask))
            return ask;
        if (w.Reference(station, item) <= 0)
            return 0;
        int percent = w.Specialty(station, item)
            ? Math.Max(s.OctSellPercent, s.SpecialtyPercent)
            : s.OctSellPercent;
        return Math.Max(MilliPerSc, Price(w, s, station, item, percent, s.TaxPercent));
    }

    /// <summary>What OCT pays for the item at the station, milli-SC per unit; 0: it doesn't buy it. At least 1 SC below what it sells for, whatever the rounding to whole SC does to the tax.</summary>
    public long OctBid(
        IMarketWorld w,
        MarketSettings s,
        string station,
        string item,
        bool specialty = false
    )
    {
        if (w.Reference(station, item) <= 0)
            return 0;
        int percent =
            specialty && w.Specialty(station, item) ? s.SpecialtyPercent : s.OctBuyPercent;
        return Math.Max(
            0,
            Math.Min(
                Price(w, s, station, item, percent, -s.TaxPercent),
                OctAsk(w, s, station, item) - MilliPerSc
            )
        );
    }

    /// <summary>What the station pays for a unit of what a shipyard leaves behind, milli-SC: OCT's price, else the item's base price less the tax.</summary>
    public long SalePrice(IMarketWorld w, MarketSettings s, string station, string item)
    {
        long bid = OctBid(w, s, station, item);
        return bid > 0 ? bid : w.BasePrice(item) * (100 - s.TaxPercent) / 100;
    }

    // A kg amount as raw units of the item, whole units for whole items
    private static long Raw(double kg, double massKg, bool integral)
    {
        if (double.IsPositiveInfinity(kg))
            return long.MaxValue;
        if (kg <= 0 || massKg <= 0)
            return kg <= 0 ? 0 : long.MaxValue;
        double units = Math.Floor(kg / massKg * Amounts.Unit + 1e-6);
        long raw = units >= long.MaxValue ? long.MaxValue : (long)units;
        return integral ? raw - raw % Amounts.Unit : raw;
    }

    // --- the Store blocks ---

    /// <summary>The station's Store: its catalog and fixed-price goods, with OCT's stock and how much it still buys.</summary>
    public List<StoreRow> Rows(IMarketWorld w, MarketSettings s, string station)
    {
        var rows = new List<StoreRow>();
        var items = w.Catalog(station)
            .Concat(s.FixedAsks.Keys.Where(k => k.Station == station).Select(k => k.Item))
            .Distinct();
        foreach (var item in items.OrderBy(i => i, StringComparer.Ordinal))
        {
            if (!w.Item(item, out double massKg, out bool integral))
                continue;
            long have = Stock(station, item);
            long room = Raw(s.OctMaxStockKg - massKg * have / Amounts.Unit, massKg, integral);
            bool special =
                w.Specialty(station, item) && SpecialtyBoughtKg(station) < s.SpecialtyDailyKg;
            long bid = OctBid(w, s, station, item, special);
            if (special)
                room = Math.Min(
                    room,
                    Raw(s.SpecialtyDailyKg - SpecialtyBoughtKg(station), massKg, integral)
                );
            rows.Add(
                new StoreRow(item, OctAsk(w, s, station, item), have, bid, bid > 0 ? room : 0)
            );
        }
        return rows;
    }

    /// <summary>A player bought this from the station's Store; the game moved the items and the money.</summary>
    public void Bought(IMarketWorld w, MarketSettings s, string station, string item, long raw)
    {
        if (!w.Item(item, out double massKg, out _))
            return;
        // A build may have bought it first, on a cluster
        SetStock(station, item, Stock(station, item) - raw);
        Drift(s, station, item, massKg * raw / Amounts.Unit, w.Now);
    }

    /// <summary>A player sold this to the station's Store; the game moved the items and the money.</summary>
    public void Sold(IMarketWorld w, MarketSettings s, string station, string item, long raw)
    {
        if (!w.Item(item, out double massKg, out _))
            return;
        SetStock(station, item, Stock(station, item) + raw);
        if (w.Specialty(station, item) && SpecialtyBoughtKg(station) < s.SpecialtyDailyKg)
            specialtyKg[station] = SpecialtyBoughtKg(station) + massKg * raw / Amounts.Unit;
        Drift(s, station, item, -massKg * raw / Amounts.Unit, w.Now);
    }

    // --- what a build buys and leaves behind ---

    /// <summary>What a purchase would take from OCT's stock: less than the amount when there isn't that much.</summary>
    public MarketFill PlanBuy(
        IMarketWorld w,
        MarketSettings s,
        string station,
        string item,
        long raw
    )
    {
        long ask = OctAsk(w, s, station, item);
        long take = ask > 0 ? Math.Min(raw, Stock(station, item)) : 0;
        return take <= 0
            ? default
            : new MarketFill
            {
                Amount = take,
                Price = ask,
                Cost = Cost(take, ask, up: true),
            };
    }

    /// <summary>Buys the item from OCT's stock for the buyer's account, all of it or nothing.</summary>
    public MarketResult Buy(
        IMarketWorld w,
        MarketSettings s,
        string station,
        long buyer,
        string item,
        long raw
    )
    {
        if (!Amounts.ValidName(item) || !w.Item(item, out double massKg, out bool integral))
            return MarketResult.No($"Unknown item {item}.");
        if (raw <= 0)
            return MarketResult.No("No amount given.");
        if (integral && raw % Amounts.Unit != 0)
            return MarketResult.No("That item comes in whole units only.");
        var fill = PlanBuy(w, s, station, item, raw);
        if (fill.Amount < raw)
            return MarketResult.No(
                fill.Amount == 0
                    ? "Nobody sells that here."
                    : $"Only {Amounts.Units(fill.Amount)} for sale here."
            );
        long balance = w.Balance(buyer);
        if (balance < 0)
            return MarketResult.No("No bank account.");
        if (balance < fill.Cost)
            return MarketResult.No($"That costs {Sc(fill.Cost)}, the account has {Sc(balance)}.");
        if (fill.Cost > 0 && !w.ChangeBalance(buyer, -fill.Cost))
            return MarketResult.No("The payment failed.");
        if (fill.Cost > 0)
            w.ChangeBalance(w.Oct, fill.Cost);
        SetStock(station, item, Stock(station, item) - raw);
        Drift(s, station, item, massKg * raw / Amounts.Unit, w.Now);
        return new MarketResult(true, $"Bought {Amounts.Units(raw)} for {Sc(fill.Cost)}.");
    }

    /// <summary>Items a canceled purchase gives back to the stock; its price effect goes too. The caller pays the money back.</summary>
    public void Return(IMarketWorld w, MarketSettings s, string station, string item, long raw)
    {
        SetStock(station, item, Stock(station, item) + raw);
        if (w.Item(item, out double massKg, out _))
            Drift(s, station, item, -massKg * raw / Amounts.Unit, w.Now);
    }

    /// <summary>
    /// Sells what a shipyard leaves behind for the seller: OCT pays <see cref="SalePrice"/>, whatever
    /// its stock limit, and it goes into its stock at the station. Items that can't be stocked, like
    /// gas bottles, are paid for and gone. Returns the SC the seller got.
    /// </summary>
    public long SellOff(
        IMarketWorld w,
        MarketSettings s,
        string station,
        long seller,
        IReadOnlyDictionary<string, long> items
    )
    {
        long net = 0;
        foreach (var (item, raw) in items.Where(i => i.Value > 0).Select(i => (i.Key, i.Value)))
        {
            net += Cost(raw, SalePrice(w, s, station, item), up: false);
            if (!w.Item(item, out double massKg, out _))
                continue;
            SetStock(station, item, Stock(station, item) + raw);
            Drift(s, station, item, -massKg * raw / Amounts.Unit, w.Now);
        }
        // OCT keeps a floor, so it can pay; should it not, the seller is paid all the same
        w.ChangeBalance(w.Oct, -net);
        w.ChangeBalance(seller, net);
        return net;
    }

    // --- stock ---

    /// <summary>
    /// Once a UTC hour: every item of a station's profile that is completely out comes back to its
    /// minimum stock, up to OCT's stock limit; a new UTC day starts the specialty purchases again.
    /// Null when it is not a new hour or nothing was out, else what was added. Force does it now and
    /// also starts a new day.
    /// </summary>
    public string Restock(
        IMarketWorld w,
        MarketSettings s,
        IEnumerable<string> stations,
        Func<string, IReadOnlyList<KeyValuePair<string, long>>> profile,
        bool force = false
    )
    {
        long hour = w.Now / 3600;
        if (force || hour / 24 > Day)
        {
            Day = hour / 24;
            specialtyKg.Clear();
        }
        if (!force && hour <= Hour)
            return null;
        Hour = hour;
        int added = 0;
        var names = stations.ToList();
        foreach (var station in names)
        foreach (var (item, raw) in profile(station))
        {
            if (Stock(station, item) > 0 || !w.Item(item, out double massKg, out bool integral))
                continue;
            long add = Math.Min(raw, Raw(s.OctMaxStockKg, massKg, integral));
            if (add <= 0)
                continue;
            SetStock(station, item, add);
            added++;
        }
        return added == 0 ? null : $"OCT restocked {added} items at {names.Count} stations.";
    }

    /// <summary>
    /// Tops OCT's fixed-price goods up to their stock at the given stations. Null when none was
    /// missing, else what was added.
    /// </summary>
    public string TopUp(MarketSettings s, IEnumerable<string> stations)
    {
        var known = stations.ToHashSet();
        var added = new List<string>();
        foreach (var (station, item) in s.FixedAsks.Keys.Where(k => known.Contains(k.Station)))
        {
            long add = s.FixedStock - Stock(station, item);
            if (add <= 0)
                continue;
            SetStock(station, item, s.FixedStock);
            added.Add($"{item} {Amounts.Units(add)} at {station}");
        }
        return added.Count == 0 ? null : "OCT topped up " + string.Join(", ", added) + ".";
    }

    /// <summary>
    /// The items OCT holds less of at the station than its minimum stock, with the amount missing
    /// (raw), for OCT's acquisition contracts.
    /// </summary>
    public List<KeyValuePair<string, long>> Missing(
        string station,
        IReadOnlyList<KeyValuePair<string, long>> profile
    ) =>
        profile
            .Select(d => new KeyValuePair<string, long>(d.Key, d.Value - Stock(station, d.Key)))
            .Where(d => d.Value > 0)
            .ToList();

    // --- formats ---

    public static string Sc(long sc) => sc.ToString("N0", CultureInfo.InvariantCulture) + " SC";

    /// <summary>A price of milli-SC per unit as "12.5 SC".</summary>
    public static string Price(long milli) =>
        ((decimal)milli / MilliPerSc).ToString("#,0.###", CultureInfo.InvariantCulture) + " SC";

    /// <summary>"Ingot/Iron=2000,Component/SteelPlate=300": items and amounts in units, as raw amounts.</summary>
    public static List<KeyValuePair<string, long>> ParseAmounts(string text)
    {
        var amounts = new List<KeyValuePair<string, long>>();
        foreach (var entry in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=');
            if (
                pair.Length != 2
                || pair[0].Trim().Split('/').Length != 2
                || !decimal.TryParse(
                    pair[1].Trim(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out decimal units
                )
                || units <= 0
            )
                throw new FormatException($"Bad entry \"{entry.Trim()}\"");
            amounts.Add(new(pair[0].Trim(), checked((long)(units * Amounts.Unit))));
        }
        return amounts;
    }

    /// <summary>"Alpha Station=Ore/Platinum,Ingot/Platinum; Gamma Station=Ore/Iron": the items of each station.</summary>
    public static Dictionary<string, HashSet<string>> ParseStations(string text)
    {
        var stations = new Dictionary<string, HashSet<string>>();
        foreach (var entry in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Trim().Length == 0)
                continue;
            var pair = entry.Split('=');
            if (pair.Length != 2 || pair[0].Trim().Length == 0)
                throw new FormatException($"Bad entry \"{entry.Trim()}\"");
            stations[pair[0].Trim()] = pair[1]
                .Split(',')
                .Select(i => i.Trim())
                .Where(i => i.Length > 0)
                .ToHashSet();
        }
        return stations;
    }

    /// <summary>"Moon Station=Ore/Uranium=100,Ingot/Uranium=50; Beta Station=Ore/Gold=100": the items and amounts of each station, as raw amounts.</summary>
    public static Dictionary<string, List<KeyValuePair<string, long>>> ParseProfiles(string text)
    {
        var profiles = new Dictionary<string, List<KeyValuePair<string, long>>>();
        foreach (var entry in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Trim().Length == 0)
                continue;
            int split = entry.IndexOf('=');
            if (split <= 0 || entry.Substring(0, split).Trim().Length == 0)
                throw new FormatException($"Bad entry \"{entry.Trim()}\"");
            profiles[entry.Substring(0, split).Trim()] = ParseAmounts(entry.Substring(split + 1));
        }
        return profiles;
    }

    /// <summary>"Ore/*, Ingot/*": items as Type/Subtype, * matching any text; null for none.</summary>
    public static Regex Glob(string patterns)
    {
        var globs = (patterns ?? "")
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => Regex.Escape(p).Replace(@"\*", ".*"))
            .ToList();
        return globs.Count == 0
            ? null
            : new Regex(
                $"^(?:{string.Join("|", globs)})$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            );
    }

    // --- persistence: a line per stock, specialty count and drift, tab-separated ---

    public string Serialize()
    {
        string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var text = new StringBuilder(Header).Append('\n');
        text.Append("N\t").Append(N(Day)).Append('\t').Append(N(Hour)).Append('\n');
        foreach (
            var p in stock.OrderBy(p => p.Key.Station + "\t" + p.Key.Item, StringComparer.Ordinal)
        )
            text.Append($"S\t{p.Key.Station}\t{p.Key.Item}\t{N(p.Value)}").Append('\n');
        foreach (var p in specialtyKg.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append("B\t").Append(p.Key).Append('\t').Append(D(p.Value)).Append('\n');
        foreach (
            var p in drift.OrderBy(p => p.Key.Station + "\t" + p.Key.Item, StringComparer.Ordinal)
        )
            text.Append($"D\t{p.Key.Station}\t{p.Key.Item}\t{D(p.Value.Kg)}\t{N(p.Value.At)}")
                .Append('\n');
        return text.ToString();
    }

    public static MarketBook Deserialize(string text)
    {
        long L(string v) => long.Parse(v, CultureInfo.InvariantCulture);
        double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);
        var book = new MarketBook();
        var lines = text.Split('\n');
        if (lines[0] != Header)
            throw new FormatException("Not a market: " + lines[0]);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
                continue;
            var f = line.Split('\t');
            switch (f[0])
            {
                case "N" when f.Length >= 3:
                    book.Day = L(f[1]);
                    book.Hour = L(f[2]);
                    break;
                case "S" when f.Length >= 4:
                    book.stock[(f[1], f[2])] = L(f[3]);
                    break;
                case "B" when f.Length >= 3:
                    book.specialtyKg[f[1]] = D(f[2]);
                    break;
                case "D" when f.Length >= 5:
                    book.drift[(f[1], f[2])] = (D(f[3]), L(f[4]));
                    break;
                default:
                    throw new FormatException("Malformed market line: " + line);
            }
        }
        return book;
    }
}
