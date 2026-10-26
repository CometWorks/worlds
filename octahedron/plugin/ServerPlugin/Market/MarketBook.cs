using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ServerPlugin;

public enum ListingKind
{
    /// <summary>Items for sale; they are reserved in the owner's ledger while listed.</summary>
    Offer,

    /// <summary>Items wanted; the money for them is taken out of the owner's account while listed.</summary>
    Order,
}

/// <summary>A player's or faction's offer or order at a station's market.</summary>
public sealed class Listing
{
    public string Id;
    public string Station;
    public LedgerOwner Owner;
    public ListingKind Kind;
    public string Item;

    // What is left, raw (millionths of a unit)
    public long Amount;

    // Milli-SC per unit
    public long Price;

    // An order's money for what is left, SC
    public long Escrow;

    // Unix seconds
    public long Created;

    /// <summary>The ledger reservation that holds an offer's items.</summary>
    public string Reservation => "market " + Id;
}

/// <summary>The market's numbers, from the config.</summary>
public sealed class MarketSettings
{
    public int FeePercent = 5;
    public int ListingDays = 7;
    public int MaxListings = 20;
    public int OctBuyPercent = 60;
    public int OctSellPercent = 140;
    public int SpecialtyPercent = 80;
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

/// <summary>What the market needs from the game: time, items, prices, accounts and the ledgers.</summary>
public interface IMarketWorld
{
    /// <summary>Unix seconds.</summary>
    long Now { get; }

    /// <summary>OCT, the market maker.</summary>
    LedgerOwner Oct { get; }

    LedgerBook Ledger { get; }

    /// <summary>Mass of one unit in kg and whether amounts are whole; false for an unknown item.</summary>
    bool Item(string item, out double massKg, out bool integral);

    /// <summary>OCT's reference price of a catalog item at the station in milli-SC per unit; 0 when OCT doesn't trade it there.</summary>
    long Reference(string station, string item);

    /// <summary>The station's catalog, the items OCT trades there.</summary>
    IEnumerable<string> Catalog(string station);

    /// <summary>Whether OCT buys the item at the station's specialty price.</summary>
    bool Specialty(string station, string item);

    /// <summary>How many more kg the owner may keep at the station; infinity without a limit.</summary>
    double RoomKg(string station, LedgerOwner owner);

    /// <summary>The account's balance in SC, -1 without an account.</summary>
    long Balance(LedgerOwner owner);

    bool ChangeBalance(LedgerOwner owner, long amount);

    /// <summary>Applies ledger ops, all or none; null when they applied, else why not.</summary>
    string ApplyLedger(IReadOnlyList<LedgerOp> ops);
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

/// <summary>A part of a purchase or a sale: a listing, or OCT when Listing is null.</summary>
public struct MarketFill
{
    public Listing Listing;
    public long Amount;

    // Milli-SC per unit
    public long Price;

    // SC: what the buyer pays, or what the seller gets before the fee
    public long Cost;
    public bool Specialty;

    public bool Oct => Listing == null;
}

/// <summary>
/// The station markets: the players' and factions' listings, and OCT as the market maker of
/// last resort. Items and money move all or none, through the ledgers and the accounts of
/// <see cref="IMarketWorld"/>. Pure bookkeeping without game types, so it is tested on its own
/// (tests/market). Runs on the authority's game thread, the World Authority's on a cluster.
/// </summary>
public sealed class MarketBook
{
    private const string Header = "octahedron-market 1";
    public const long MilliPerSc = 1000;

    private readonly Dictionary<string, Listing> listings = new();
    private long nextId;

    // UTC day number of the last restock; the specialty purchases are counted per day
    public long Day = -1;
    private readonly Dictionary<string, double> specialtyKg = new();

    // Net kg bought from the market (positive) or sold to it, per station and item, and when
    private readonly Dictionary<(string Station, string Item), (double Kg, long At)> drift = new();

    public IEnumerable<Listing> Listings => listings.Values;

    public Listing Get(string id) => id != null && listings.TryGetValue(id, out var l) ? l : null;

    public IEnumerable<Listing> At(string station, string item = null, ListingKind? kind = null) =>
        listings.Values.Where(l =>
            l.Station == station
            && (item == null || l.Item == item)
            && (kind == null || l.Kind == kind)
        );

    /// <summary>The money held for the owner's open orders, SC.</summary>
    public long Escrow(LedgerOwner owner) =>
        listings.Values.Where(l => l.Owner.Equals(owner)).Sum(l => l.Escrow);

    public double SpecialtyBoughtKg(string station) =>
        specialtyKg.TryGetValue(station, out double kg) ? kg : 0;

    // --- prices ---

    /// <summary>SC for an amount at a price, rounded up (what a buyer pays) or down.</summary>
    public static long Cost(long raw, long price, bool up)
    {
        decimal sc = (decimal)raw * price / (LedgerBook.Unit * MilliPerSc);
        return checked((long)(up ? Math.Ceiling(sc) : Math.Floor(sc)));
    }

    public static long Fee(long cost, int percent) => cost * percent / 100;

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

    private long OctPrice(
        IMarketWorld w,
        MarketSettings s,
        string station,
        string item,
        int percent
    )
    {
        long reference = w.Reference(station, item);
        if (reference <= 0)
            return 0;
        double price = reference * percent / 100.0 * Factor(s, station, item, w.Now);
        return Math.Max(1, (long)Math.Round(price));
    }

    /// <summary>What OCT sells the item for at the station, milli-SC per unit; 0: it doesn't.</summary>
    public long OctAsk(IMarketWorld w, MarketSettings s, string station, string item) =>
        s.FixedAsks.TryGetValue((station, item), out long ask)
            ? ask
            : OctPrice(w, s, station, item, s.OctSellPercent);

    /// <summary>What OCT pays for the item at the station, milli-SC per unit; 0: it doesn't buy it.</summary>
    public long OctBid(
        IMarketWorld w,
        MarketSettings s,
        string station,
        string item,
        bool specialty = false
    ) =>
        OctPrice(
            w,
            s,
            station,
            item,
            specialty && w.Specialty(station, item) ? s.SpecialtyPercent : s.OctBuyPercent
        );

    private static long Free(IMarketWorld w, string station, LedgerOwner owner, string item) =>
        w.Ledger.FreeOf(station, owner).TryGetValue(item, out long raw) ? raw : 0;

    // A kg amount as raw units of the item, whole units for whole items
    private static long Raw(double kg, double massKg, bool integral)
    {
        if (double.IsPositiveInfinity(kg))
            return long.MaxValue;
        if (kg <= 0 || massKg <= 0)
            return kg <= 0 ? 0 : long.MaxValue;
        double units = Math.Floor(kg / massKg * LedgerBook.Unit + 1e-6);
        long raw = units >= long.MaxValue ? long.MaxValue : (long)units;
        return integral ? raw - raw % LedgerBook.Unit : raw;
    }

    private static string Check(IMarketWorld w, string item, long raw, out double massKg)
    {
        massKg = 0;
        if (!LedgerBook.ValidName(item) || !w.Item(item, out massKg, out bool integral))
            return $"Unknown item {item}.";
        if (raw <= 0)
            return "No amount given.";
        if (integral && raw % LedgerBook.Unit != 0)
            return "That item comes in whole units only.";
        return null;
    }

    // --- buying ---

    /// <summary>
    /// Where a purchase would come from: the cheapest offers at the station first, OCT's stock
    /// at its price among them, up to the amount. Less than the amount when there isn't that much.
    /// </summary>
    public List<MarketFill> PlanBuy(
        IMarketWorld w,
        MarketSettings s,
        string station,
        LedgerOwner buyer,
        string item,
        long raw
    )
    {
        var sources = At(station, item, ListingKind.Offer)
            .Where(l => !l.Owner.Equals(buyer) && w.Balance(l.Owner) >= 0)
            .Select(l => new MarketFill
            {
                Listing = l,
                Amount = l.Amount,
                Price = l.Price,
            })
            .ToList();
        long ask = OctAsk(w, s, station, item);
        long stock = Free(w, station, w.Oct, item);
        if (ask > 0 && stock > 0 && !buyer.Equals(w.Oct))
            sources.Add(new MarketFill { Amount = stock, Price = ask });
        return Take(sources.OrderBy(f => f.Price).ThenBy(Order), raw, up: true);
    }

    // Listings before OCT, the older listing first
    private static long Order(MarketFill f) => f.Oct ? long.MaxValue : f.Listing.Created;

    private static List<MarketFill> Take(IEnumerable<MarketFill> sources, long raw, bool up)
    {
        var fills = new List<MarketFill>();
        long left = raw;
        foreach (var source in sources)
        {
            if (left <= 0)
                break;
            long take = Math.Min(left, source.Amount);
            if (take <= 0)
                continue;
            fills.Add(source with { Amount = take, Cost = Cost(take, source.Price, up) });
            left -= take;
        }
        return fills;
    }

    /// <summary>
    /// Buys the item for the buyer: from the plan of <see cref="PlanBuy"/>, all of it or nothing.
    /// The items go into the buyer's free stock, or with <paramref name="reservation"/> into that
    /// uncounted reservation of the buyer (a build's).
    /// </summary>
    public MarketResult Buy(
        IMarketWorld w,
        MarketSettings s,
        string station,
        LedgerOwner buyer,
        string item,
        long raw,
        string reservation = null
    )
    {
        string error = Check(w, item, raw, out double massKg);
        if (error != null)
            return MarketResult.No(error);
        var fills = PlanBuy(w, s, station, buyer, item, raw);
        long amount = fills.Sum(f => f.Amount);
        if (amount < raw)
            return MarketResult.No(
                amount == 0
                    ? "Nobody sells that here."
                    : $"Only {LedgerBook.Units(amount)} for sale here."
            );
        long total = fills.Sum(f => f.Cost);
        long balance = w.Balance(buyer);
        if (balance < 0)
            return MarketResult.No("No bank account.");
        if (balance < total)
            return MarketResult.No($"That costs {Sc(total)}, the account has {Sc(balance)}.");

        var ops = new List<LedgerOp>();
        foreach (var f in fills)
            ops.Add(
                f.Oct
                    ? new LedgerOp
                    {
                        Action = LedgerAction.Transfer,
                        Station = station,
                        Owner = w.Oct,
                        Other = buyer,
                    }.Add(item, f.Amount)
                    : new LedgerOp
                    {
                        Action = LedgerAction.Give,
                        Station = station,
                        Owner = f.Listing.Owner,
                        Other = buyer,
                        Reservation = f.Listing.Reservation,
                    }.Add(item, f.Amount)
            );
        if (reservation != null)
            ops.Add(
                new LedgerOp
                {
                    Action = LedgerAction.Reserve,
                    Station = station,
                    Owner = buyer,
                    Reservation = reservation,
                }.Add(item, raw)
            );

        if (total > 0 && !w.ChangeBalance(buyer, -total))
            return MarketResult.No("The payment failed.");
        error = w.ApplyLedger(ops);
        if (error != null)
        {
            w.ChangeBalance(buyer, total);
            return MarketResult.No(error);
        }

        long octIncome = 0;
        foreach (var f in fills)
        {
            if (f.Oct)
            {
                octIncome += f.Cost;
                continue;
            }
            long fee = Fee(f.Cost, s.FeePercent);
            octIncome += fee;
            w.ChangeBalance(f.Listing.Owner, f.Cost - fee);
            f.Listing.Amount -= f.Amount;
            if (f.Listing.Amount <= 0)
                listings.Remove(f.Listing.Id);
        }
        if (octIncome > 0)
            w.ChangeBalance(w.Oct, octIncome);
        Drift(s, station, item, massKg * raw / LedgerBook.Unit, w.Now);
        return new MarketResult(true, $"Bought {LedgerBook.Units(raw)} for {Sc(total)}.");
    }

    // --- selling ---

    /// <summary>
    /// Where a sale would go: the best orders at the station first, as far as their owners have
    /// room, then OCT at its specialty and its normal price up to its stock limit.
    /// </summary>
    public List<MarketFill> PlanSell(
        IMarketWorld w,
        MarketSettings s,
        string station,
        LedgerOwner seller,
        string item,
        long raw
    )
    {
        if (!w.Item(item, out double massKg, out bool integral))
            return [];
        var sources = new List<MarketFill>();
        foreach (var order in At(station, item, ListingKind.Order))
        {
            if (order.Owner.Equals(seller))
                continue;
            long room = Raw(w.RoomKg(station, order.Owner), massKg, integral);
            sources.Add(
                new MarketFill
                {
                    Listing = order,
                    Amount = Math.Min(order.Amount, room),
                    Price = order.Price,
                }
            );
        }
        long bid = OctBid(w, s, station, item);
        if (bid > 0 && !seller.Equals(w.Oct))
        {
            double stockKg = massKg * Free(w, station, w.Oct, item) / LedgerBook.Unit;
            long capacity = Raw(s.OctMaxStockKg - stockKg, massKg, integral);
            long special = 0;
            if (w.Specialty(station, item))
            {
                special = Math.Min(
                    capacity,
                    Raw(s.SpecialtyDailyKg - SpecialtyBoughtKg(station), massKg, integral)
                );
                sources.Add(
                    new MarketFill
                    {
                        Amount = special,
                        Price = OctBid(w, s, station, item, specialty: true),
                        Specialty = true,
                    }
                );
            }
            sources.Add(new MarketFill { Amount = capacity - special, Price = bid });
        }
        return Take(sources.OrderByDescending(f => f.Price).ThenBy(Order), raw, up: false);
    }

    /// <summary>Sells the seller's free items: the plan of <see cref="PlanSell"/>, all of it or nothing. Amount 0: all they have.</summary>
    public MarketResult Sell(
        IMarketWorld w,
        MarketSettings s,
        string station,
        LedgerOwner seller,
        string item,
        long raw
    )
    {
        long have = Free(w, station, seller, item);
        if (raw == 0)
            raw = have;
        string error = Check(w, item, raw, out double massKg);
        if (error != null)
            return MarketResult.No(have == 0 ? "There is none of that in the ledger." : error);
        if (have < raw)
            return MarketResult.No($"Only {LedgerBook.Units(have)} in the ledger.");
        if (w.Balance(seller) < 0)
            return MarketResult.No("No bank account.");
        var fills = PlanSell(w, s, station, seller, item, raw);
        long amount = fills.Sum(f => f.Amount);
        if (amount < raw)
            return MarketResult.No(
                amount == 0
                    ? "Nobody buys that here."
                    : $"Only {LedgerBook.Units(amount)} wanted here."
            );

        var ops = fills
            .Select(f =>
                new LedgerOp
                {
                    Action = LedgerAction.Transfer,
                    Station = station,
                    Owner = seller,
                    Other = f.Oct ? w.Oct : f.Listing.Owner,
                }.Add(item, f.Amount)
            )
            .ToList();
        long octPays = fills.Where(f => f.Oct).Sum(f => f.Cost);
        if (octPays > 0 && !w.ChangeBalance(w.Oct, -octPays))
            return MarketResult.No("OCT can't pay for that now.");
        error = w.ApplyLedger(ops);
        if (error != null)
        {
            if (octPays > 0)
                w.ChangeBalance(w.Oct, octPays);
            return MarketResult.No(error);
        }

        long gross = octPays,
            fees = 0;
        foreach (var f in fills)
        {
            if (f.Specialty)
                specialtyKg[station] =
                    SpecialtyBoughtKg(station) + massKg * f.Amount / LedgerBook.Unit;
            if (f.Oct)
                continue;
            var order = f.Listing;
            order.Amount -= f.Amount;
            // What the rest of the order needs stays held, so the parts add up to the escrow
            long pay = order.Escrow - Cost(order.Amount, order.Price, up: true);
            pay = Math.Max(0, Math.Min(order.Escrow, pay));
            order.Escrow -= pay;
            long fee = Fee(pay, s.FeePercent);
            fees += fee;
            gross += pay - fee;
            if (order.Amount <= 0)
            {
                // Nothing should be left; whatever is goes back
                if (order.Escrow > 0)
                    w.ChangeBalance(order.Owner, order.Escrow);
                listings.Remove(order.Id);
            }
        }
        if (gross > 0)
            w.ChangeBalance(seller, gross);
        if (fees > 0)
            w.ChangeBalance(w.Oct, fees);
        Drift(s, station, item, -massKg * raw / LedgerBook.Unit, w.Now);
        return new MarketResult(true, $"Sold {LedgerBook.Units(raw)} for {Sc(gross)}.");
    }

    // --- listings ---

    /// <summary>Lists an offer (reserving the items) or an order (holding the money). Amount 0 offers all the owner has.</summary>
    public MarketResult List(
        IMarketWorld w,
        MarketSettings s,
        string station,
        LedgerOwner owner,
        ListingKind kind,
        string item,
        long raw,
        long price
    )
    {
        if (kind == ListingKind.Offer && raw == 0)
            raw = Free(w, station, owner, item);
        string error = Check(w, item, raw, out _);
        if (error != null)
            return MarketResult.No(error);
        if (price <= 0)
            return MarketResult.No("No price given.");
        if (At(station).Count(l => l.Owner.Equals(owner)) >= s.MaxListings)
            return MarketResult.No($"{s.MaxListings} listings at this station is the most.");
        if (w.Balance(owner) < 0)
            return MarketResult.No("No bank account.");

        var listing = new Listing
        {
            Id = "M" + (nextId + 1).ToString(CultureInfo.InvariantCulture),
            Station = station,
            Owner = owner,
            Kind = kind,
            Item = item,
            Amount = raw,
            Price = price,
            Created = w.Now,
        };
        if (kind == ListingKind.Offer)
        {
            error = w.ApplyLedger([
                new LedgerOp
                {
                    Action = LedgerAction.Reserve,
                    Station = station,
                    Owner = owner,
                    Reservation = listing.Reservation,
                    Counted = true,
                }.Add(item, raw),
            ]);
            if (error != null)
                return MarketResult.No(error);
        }
        else
        {
            long escrow = Cost(raw, price, up: true);
            long balance = w.Balance(owner);
            if (balance < escrow)
                return MarketResult.No(
                    $"That order holds {Sc(escrow)}, the account has {Sc(balance)}."
                );
            if (escrow > 0 && !w.ChangeBalance(owner, -escrow))
                return MarketResult.No("The payment failed.");
            listing.Escrow = escrow;
        }
        nextId++;
        listings[listing.Id] = listing;
        return new MarketResult(
            true,
            $"Listed {(kind == ListingKind.Offer ? "an offer" : "an order")} {listing.Id}: {LedgerBook.Units(raw)} at {Price(price)}."
        );
    }

    /// <summary>Takes the owner's listing off the market: an offer's items back to the ledger, an order's money back to the account.</summary>
    public MarketResult Cancel(IMarketWorld w, LedgerOwner owner, string id)
    {
        var listing = Get(id);
        if (listing == null || !listing.Owner.Equals(owner))
            return MarketResult.No($"No listing {id} of yours.");
        return Close(w, listing) ?? new MarketResult(true, $"Listing {id} canceled.");
    }

    private MarketResult? Close(IMarketWorld w, Listing listing)
    {
        if (listing.Kind == ListingKind.Offer)
        {
            string error = w.ApplyLedger([
                new LedgerOp
                {
                    Action = LedgerAction.Release,
                    Station = listing.Station,
                    Owner = listing.Owner,
                    Reservation = listing.Reservation,
                },
            ]);
            if (error != null)
                return MarketResult.No(error);
        }
        else if (listing.Escrow > 0 && !w.ChangeBalance(listing.Owner, listing.Escrow))
            return MarketResult.No("The refund failed.");
        listings.Remove(listing.Id);
        return null;
    }

    /// <summary>Closes the listings older than the settings allow; returns what happened, a line each.</summary>
    public List<string> Expire(IMarketWorld w, MarketSettings s)
    {
        var lines = new List<string>();
        long oldest = w.Now - s.ListingDays * 86_400L;
        foreach (var listing in listings.Values.Where(l => l.Created <= oldest).ToList())
        {
            var failure = Close(w, listing);
            lines.Add(
                $"Listing {listing.Id} of {listing.Owner} at {listing.Station} expired"
                    + (failure is { } f ? $" but could not close: {f.Message}" : "")
            );
        }
        return lines;
    }

    /// <summary>
    /// Once a UTC day: OCT's daily stock goes into its ledger at every station, as far as its
    /// stock limit allows, and the specialty purchases start again. Null when it is not a new
    /// day, else what was added, or why it failed.
    /// </summary>
    public string Restock(
        IMarketWorld w,
        MarketSettings s,
        IEnumerable<string> stations,
        IReadOnlyList<KeyValuePair<string, long>> daily,
        bool force = false
    )
    {
        long today = w.Now / 86_400;
        if (!force && today <= Day)
            return null;
        Day = today;
        specialtyKg.Clear();
        var ops = new List<LedgerOp>();
        foreach (var station in stations)
        {
            var op = new LedgerOp
            {
                Action = LedgerAction.Deposit,
                Station = station,
                Owner = w.Oct,
            };
            foreach (var (item, raw) in daily)
            {
                if (!w.Item(item, out double massKg, out bool integral))
                    continue;
                double stockKg = massKg * Free(w, station, w.Oct, item) / LedgerBook.Unit;
                long add = Math.Min(raw, Raw(s.OctMaxStockKg - stockKg, massKg, integral));
                if (add > 0)
                    op.Add(item, add);
            }
            if (op.Items.Count > 0)
                ops.Add(op);
        }
        if (ops.Count == 0)
            return "OCT's stock is full everywhere.";
        return w.ApplyLedger(ops) is { } error
            ? "OCT's restock failed: " + error
            : $"OCT restocked {ops.Count} stations.";
    }

    /// <summary>
    /// Tops OCT's fixed-price goods up to their stock at the given stations. Null when none was
    /// missing, else what was added, or why it failed.
    /// </summary>
    public string TopUp(IMarketWorld w, MarketSettings s, IEnumerable<string> stations)
    {
        var known = stations.ToHashSet();
        var ops = new List<LedgerOp>();
        foreach (var (station, item) in s.FixedAsks.Keys.Where(k => known.Contains(k.Station)))
        {
            long add = s.FixedStock - Free(w, station, w.Oct, item);
            if (add > 0)
                ops.Add(
                    new LedgerOp
                    {
                        Action = LedgerAction.Deposit,
                        Station = station,
                        Owner = w.Oct,
                    }.Add(item, add)
                );
        }
        if (ops.Count == 0)
            return null;
        return w.ApplyLedger(ops) is { } error
            ? "OCT's top-up failed: " + error
            : "OCT topped up "
                + string.Join(
                    ", ",
                    ops.Select(o =>
                        $"{o.Items[0].Key} {LedgerBook.Units(o.Items[0].Value)} at {o.Station}"
                    )
                )
                + ".";
    }

    /// <summary>
    /// The catalog items OCT holds less of at the station than its daily stock, with the amount
    /// missing (raw), for OCT's acquisition contracts.
    /// </summary>
    public static List<KeyValuePair<string, long>> Missing(
        IMarketWorld w,
        string station,
        IReadOnlyList<KeyValuePair<string, long>> daily
    ) =>
        daily
            .Select(d => new KeyValuePair<string, long>(
                d.Key,
                d.Value - Free(w, station, w.Oct, d.Key)
            ))
            .Where(d => d.Value > 0)
            .ToList();

    // --- requests ---

    /// <summary>Does a kiosk's market request: buy, sell, offer, order or cancel.</summary>
    public MarketResult Run(IMarketWorld w, MarketSettings s, MarketRequest r)
    {
        switch (r.Verb)
        {
            case KioskVerb.Buy:
                return Buy(w, s, r.Station, r.Owner, r.Item, r.Amount, r.Reservation);
            case KioskVerb.Sell:
                return Sell(w, s, r.Station, r.Owner, r.Item, r.Amount);
            case KioskVerb.Offer:
            case KioskVerb.Order:
                long price = r.Price;
                if (price <= 0 && r.PricePercent > 0)
                {
                    long reference = w.Reference(r.Station, r.Item);
                    if (reference <= 0)
                        return MarketResult.No("OCT doesn't trade that here: give a price in SC.");
                    price = Math.Max(
                        1,
                        (long)
                            Math.Round(
                                reference
                                    * Factor(s, r.Station, r.Item, w.Now)
                                    * r.PricePercent
                                    / 100
                            )
                    );
                }
                return List(
                    w,
                    s,
                    r.Station,
                    r.Owner,
                    r.Verb == KioskVerb.Offer ? ListingKind.Offer : ListingKind.Order,
                    r.Item,
                    r.Amount,
                    price
                );
            case KioskVerb.Cancel:
                return Cancel(w, r.Owner, r.Listing);
            default:
                return MarketResult.No($"{r.Verb} is no market request.");
        }
    }

    // --- what the kiosk shows ---

    /// <summary>
    /// The station's market as kiosk records (Kiosks/KioskProtocol.cs), one per line:
    ///   market    item, display name, OCT buys at (specialty price while it lasts), OCT sells at,
    ///             OCT's stock, cheapest offer and the amount at that price, best order and its
    ///             amount, specialty (1/0); prices milli-SC per unit, amounts raw
    ///   listing   id, offer/order, item, display name, amount left, price, mine/faction, expires (unix s)
    ///   balance   the operator's SC, the faction's (-1 without a faction)
    /// Catalog items, OCT's fixed-price goods and items with listings, by name.
    /// </summary>
    public string View(
        IMarketWorld w,
        MarketSettings s,
        string station,
        LedgerOwner me,
        LedgerOwner? faction,
        Func<string, string> name
    )
    {
        var text = new StringBuilder();
        void Line(params object[] fields) =>
            text.Append(string.Join("\t", fields.Select(KioskReply.Field))).Append('\n');
        var items = w.Catalog(station)
            .Concat(s.FixedAsks.Keys.Where(k => k.Station == station).Select(k => k.Item))
            .Concat(At(station).Select(l => l.Item))
            .Distinct();
        foreach (var item in items.OrderBy(name, StringComparer.OrdinalIgnoreCase))
        {
            if (!w.Item(item, out double massKg, out _))
                continue;
            var offers = At(station, item, ListingKind.Offer).ToList();
            var orders = At(station, item, ListingKind.Order).ToList();
            long offer = offers.Count > 0 ? offers.Min(l => l.Price) : 0;
            long order = orders.Count > 0 ? orders.Max(l => l.Price) : 0;
            bool special =
                w.Specialty(station, item) && SpecialtyBoughtKg(station) < s.SpecialtyDailyKg;
            Line(
                "market",
                item,
                name(item),
                OctBid(w, s, station, item, special),
                OctAsk(w, s, station, item),
                Free(w, station, w.Oct, item),
                offer,
                offers.Where(l => l.Price == offer).Sum(l => l.Amount),
                order,
                orders.Where(l => l.Price == order).Sum(l => l.Amount),
                w.Specialty(station, item)
            );
        }
        foreach (
            var l in At(station)
                .Where(l => l.Owner.Equals(me) || (faction is { } f && l.Owner.Equals(f)))
                .OrderBy(l => l.Created)
        )
            Line(
                "listing",
                l.Id,
                l.Kind == ListingKind.Offer ? "offer" : "order",
                l.Item,
                name(l.Item),
                l.Amount,
                l.Price,
                l.Owner.Equals(me) ? "mine" : "faction",
                l.Created + s.ListingDays * 86_400L
            );
        Line("balance", w.Balance(me), faction is { } fa ? w.Balance(fa) : -1);
        return text.ToString();
    }

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
            amounts.Add(new(pair[0].Trim(), checked((long)(units * LedgerBook.Unit))));
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

    // --- persistence: a line per listing, specialty count and drift, tab-separated ---

    public string Serialize()
    {
        string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var text = new StringBuilder(Header).Append('\n');
        text.Append("N\t").Append(N(nextId)).Append('\t').Append(N(Day)).Append('\n');
        foreach (var l in listings.Values.OrderBy(l => l.Created).ThenBy(l => l.Id))
            text.Append(
                    string.Join(
                        "\t",
                        "L",
                        l.Id,
                        l.Station,
                        l.Owner.ToString(),
                        l.Kind.ToString(),
                        l.Item,
                        N(l.Amount),
                        N(l.Price),
                        N(l.Escrow),
                        N(l.Created)
                    )
                )
                .Append('\n');
        foreach (var p in specialtyKg.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append("B\t").Append(p.Key).Append('\t').Append(D(p.Value)).Append('\n');
        foreach (var p in drift.OrderBy(p => p.Key.Station + "\t" + p.Key.Item))
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
                    book.nextId = L(f[1]);
                    book.Day = L(f[2]);
                    break;
                case "L" when f.Length >= 10 && LedgerOwner.TryParse(f[3], out var owner):
                    book.listings[f[1]] = new Listing
                    {
                        Id = f[1],
                        Station = f[2],
                        Owner = owner,
                        Kind = (ListingKind)Enum.Parse(typeof(ListingKind), f[4]),
                        Item = f[5],
                        Amount = L(f[6]),
                        Price = L(f[7]),
                        Escrow = L(f[8]),
                        Created = L(f[9]),
                    };
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

/// <summary>
/// A market request a node sends the authority, a line of tab-separated fields: verb, station,
/// owner, item, raw amount, price (milli-SC per unit), price as a percent of the reference,
/// listing id, reservation of a build to buy into.
/// </summary>
public sealed class MarketRequest
{
    public KioskVerb Verb;
    public string Station = "";
    public LedgerOwner Owner;
    public string Item = "";
    public long Amount;
    public long Price;
    public int PricePercent;
    public string Listing = "";
    public string Reservation;

    public string Encode() =>
        string.Join(
            "\t",
            Verb.ToString(),
            Station,
            Owner.ToString(),
            Item ?? "",
            Amount.ToString(CultureInfo.InvariantCulture),
            Price.ToString(CultureInfo.InvariantCulture),
            PricePercent.ToString(CultureInfo.InvariantCulture),
            Listing ?? "",
            Reservation ?? ""
        );

    public static MarketRequest Decode(string text)
    {
        var f = text.Split('\t');
        if (f.Length != 9 || !LedgerOwner.TryParse(f[2], out var owner))
            throw new FormatException("Malformed market request");
        return new MarketRequest
        {
            Verb = (KioskVerb)Enum.Parse(typeof(KioskVerb), f[0]),
            Station = f[1],
            Owner = owner,
            Item = f[3],
            Amount = long.Parse(f[4], CultureInfo.InvariantCulture),
            Price = long.Parse(f[5], CultureInfo.InvariantCulture),
            PricePercent = int.Parse(f[6], CultureInfo.InvariantCulture),
            Listing = f[7],
            Reservation = f[8].Length == 0 ? null : f[8],
        };
    }
}
