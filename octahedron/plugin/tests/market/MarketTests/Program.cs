using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks of the station market (ServerPlugin/Market/MarketBook.cs) against a fake world: OCT's
// prices, stock and specialty, buying and selling through listings and OCT all or none, the
// fee, listing limits, cancel and expiry, price drift, the saved and wire formats, the kiosk's
// market requests, and that no money is made or lost. Prints one line per failed check and a
// summary; exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/market/MarketTests

int checks = 0;
int failures = 0;

void Check(bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

const long U = LedgerBook.Unit;
const string A = "Alpha Station";
const string B = "Beta Station";
const string Iron = "Ingot/Iron"; // 1 kg, 10 SC
const string Plate = "Component/SteelPlate"; // 20 kg, whole, 50 SC
const string Platinum = "Ingot/Platinum"; // 1 kg, 35 SC, Alpha's specialty
const string Gold = "Ingot/Gold"; // 1 kg, not in OCT's catalog
var p1 = LedgerOwner.Player(1);
var p2 = LedgerOwner.Player(2);
var p3 = LedgerOwner.Player(3);
var f10 = LedgerOwner.OfFaction(10);
var oct = FakeWorld.OctOwner;
var daily = new List<KeyValuePair<string, long>> { new(Iron, 2000 * U), new(Plate, 300 * U) };

MarketSettings Exact() => new() { DriftKg = 0 };

long Free(FakeWorld w, LedgerOwner owner, string item, string station = A) =>
    w.Ledger.FreeOf(station, owner).TryGetValue(item, out long raw) ? raw : 0;

void Deposit(FakeWorld w, LedgerOwner owner, string item, long raw, string station = A) =>
    Check(
        w.Ledger.Apply([
            new LedgerOp
            {
                Action = LedgerAction.Deposit,
                Station = station,
                Owner = owner,
            }.Add(item, raw),
        ]) == null,
        $"deposit {item} for {owner}"
    );

// Nothing changes when a request is refused
void Refused(FakeWorld w, MarketBook m, string what, Func<MarketResult> act, string message = "")
{
    string before = w.Snapshot() + m.Serialize();
    var r = act();
    Check(!r.Ok && r.Message.Contains(message), $"{what}: refused ({r.Message})");
    Check(w.Snapshot() + m.Serialize() == before, what + ": nothing changed");
}

MarketResult Ok(FakeWorld w, MarketBook m, string what, Func<MarketResult> act)
{
    var r = act();
    Check(r.Ok, $"{what}: {r.Message}");
    // Money only moves: the accounts and the orders' escrow add up to the same
    Check(w.Total() + m.Listings.Sum(l => l.Escrow) == w.Start, $"{what}: no money made or lost");
    return r;
}

// OCT's prices and daily stock
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    Check(m.OctAsk(w, s, A, Iron) == 14_000, "OCT sells iron at 140 %: " + m.OctAsk(w, s, A, Iron));
    Check(m.OctBid(w, s, A, Iron) == 6_000, "OCT buys iron at 60 %");
    Check(m.OctBid(w, s, A, Platinum, true) == 28_000, "OCT buys Alpha's specialty at 80 %");
    Check(m.OctBid(w, s, B, Platinum, true) == 21_000, "no specialty at Beta: 60 %");
    Check(m.OctAsk(w, s, A, Gold) == 0 && m.OctBid(w, s, A, Gold) == 0, "gold: not in the catalog");

    Check(m.Restock(w, s, [A, B], daily) != null, "first restock");
    Check(
        Free(w, oct, Iron) == 2000 * U && Free(w, oct, Plate, B) == 300 * U,
        "restock: both stations"
    );
    Check(m.Restock(w, s, [A, B], daily) == null, "restock: once a day");
    w.Now += 86_400;
    m.Restock(w, s, [A, B], daily);
    Check(Free(w, oct, Iron) == 4000 * U, "restock: the next day adds again");
    var lots = new List<KeyValuePair<string, long>> { new(Iron, 500_000 * U) };
    m.Restock(w, s, [A], lots, force: true);
    Check(Free(w, oct, Iron) == 200_000 * U, "restock: up to OCT's 200 t: " + Free(w, oct, Iron));
    Check(m.Restock(w, s, [A], lots, force: true).Contains("full"), "restock: full");

    var missing = MarketBook.Missing(w, B, [new(Iron, 5000 * U), new(Plate, 100 * U)]);
    Check(
        missing.Count == 1 && missing[0].Key == Iron && missing[0].Value == 1000 * U,
        "missing: what OCT lacks for its daily stock"
    );
}

// Buying from OCT and from offers
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    m.Restock(w, s, [A], daily);
    Ok(w, m, "buy 100 iron from OCT", () => m.Buy(w, s, A, p1, Iron, 100 * U));
    Check(w.Balances[p1] == 25_000 - 1_400, "OCT's price paid: " + w.Balances[p1]);
    Check(Free(w, p1, Iron) == 100 * U && Free(w, oct, Iron) == 1900 * U, "iron moved");

    Deposit(w, p2, Iron, 500 * U);
    Ok(
        w,
        m,
        "p2 offers 200 iron at 12 SC",
        () => m.List(w, s, A, p2, ListingKind.Offer, Iron, 200 * U, 12_000)
    );
    var offer = m.At(A, Iron, ListingKind.Offer).Single();
    Check(Free(w, p2, Iron) == 300 * U, "offer: the items are reserved");
    Check(
        Math.Abs(w.Ledger.MassKg(A, p2) - 500) < 1e-9,
        "offer: they still count toward the limit"
    );
    Refused(
        w,
        m,
        "p2 can't buy from its own offer only",
        () => m.Buy(w, s, A, p2, Iron, 2000 * U),
        "Only"
    );

    var plan = m.PlanBuy(w, s, A, p1, Iron, 250 * U);
    Check(
        plan.Count == 2 && plan[0].Listing == offer && plan[0].Amount == 200 * U && plan[1].Oct,
        "plan: the cheaper offer first, then OCT"
    );
    long p2Before = w.Balances[p2],
        octBefore = w.Balances[oct];
    Ok(w, m, "buy 250 iron", () => m.Buy(w, s, A, p1, Iron, 250 * U));
    Check(w.Balances[p1] == 25_000 - 1_400 - 2_400 - 700, "paid 200 at 12 and 50 at 14");
    Check(w.Balances[p2] == p2Before + 2_400 - 120, "the seller gets the price less the 5 % fee");
    Check(w.Balances[oct] == octBefore + 120 + 700, "OCT gets the fee and its sale");
    Check(!m.Listings.Any() && !w.Ledger.ReservationsOf(A, p2).Any(), "the sold-out offer is gone");
    Check(Free(w, p1, Iron) == 350 * U, "the buyer has 350");

    Refused(w, m, "more than is for sale", () => m.Buy(w, s, A, p1, Iron, 5000 * U), "Only");
    Refused(w, m, "unknown item", () => m.Buy(w, s, A, p1, "Ingot/Unobtainium", U), "Unknown");
    Refused(w, m, "half a plate", () => m.Buy(w, s, A, p1, Plate, U / 2), "whole");
    Refused(w, m, "nothing for sale", () => m.Buy(w, s, A, p1, Gold, U), "Nobody");
    w.Balances[p3] = 100;
    Refused(w, m, "too little money", () => m.Buy(w, s, A, p3, Plate, 10 * U), "costs");
    w.Balances[p3] = 1_000_000;
    w.Start = w.Total();
    Deposit(w, p3, Iron, 99_990 * U);
    Refused(
        w,
        m,
        "over the buyer's ledger limit, the money stays",
        () => m.Buy(w, s, A, p3, Plate, 10 * U),
        "limit"
    );

    Ok(
        w,
        m,
        "a build buys into its reservation",
        () => m.Buy(w, s, A, p1, Plate, 10 * U, "build 1")
    );
    var build = w.Ledger.ReservationsOf(A, p1).Single();
    Check(
        build.Key == "build 1" && !build.Value.Counted && build.Value.Items[Plate] == 10 * U,
        "build: in an uncounted reservation"
    );
    Check(Free(w, p1, Plate) == 0, "build: nothing free");
}

// Orders and selling
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    Ok(
        w,
        m,
        "p1 orders 100 plates at 60 SC",
        () => m.List(w, s, A, p1, ListingKind.Order, Plate, 100 * U, 60_000)
    );
    Check(w.Balances[p1] == 25_000 - 6_000, "order: the money is held");
    Check(m.Escrow(p1) == 6_000, "order: escrow");
    Refused(
        w,
        m,
        "an order without the money",
        () => m.List(w, s, A, p3, ListingKind.Order, Plate, 1000 * U, 60_000),
        "holds"
    );
    Refused(w, m, "sell what isn't there", () => m.Sell(w, s, A, p2, Plate, 30 * U), "Only 0");

    Deposit(w, p2, Plate, 200 * U);
    long octBefore = w.Balances[oct];
    Ok(w, m, "p2 sells 30 plates", () => m.Sell(w, s, A, p2, Plate, 30 * U));
    Check(w.Balances[p2] == 25_000 + 1_800 - 90, "sold to the order, less the fee");
    Check(w.Balances[oct] == octBefore + 90, "OCT gets the fee");
    Check(Free(w, p1, Plate) == 30 * U, "the order's owner has the plates");
    var order = m.At(A, Plate, ListingKind.Order).Single();
    Check(order.Amount == 70 * U && order.Escrow == 4_200, "the order goes on: 70 for 4,200 SC");

    octBefore = w.Balances[oct];
    long p2Before = w.Balances[p2];
    Ok(w, m, "p2 sells 100 plates", () => m.Sell(w, s, A, p2, Plate, 100 * U));
    Check(w.Balances[p2] == p2Before + 4_200 - 210 + 900, "70 to the order, 30 to OCT at 30 SC");
    Check(w.Balances[oct] == octBefore + 210 - 900, "OCT pays its part and gets the fee");
    Check(!m.Listings.Any(), "the filled order is gone");
    Check(Free(w, oct, Plate) == 30 * U && Free(w, p1, Plate) == 100 * U, "plates moved");

    // An order whose owner has no room is passed over
    Deposit(w, p3, Iron, 99_990 * U);
    w.Balances[p3] = 1_000_000;
    w.Start = w.Total();
    Ok(
        w,
        m,
        "p3 orders plates",
        () => m.List(w, s, A, p3, ListingKind.Order, Plate, 10 * U, 100_000)
    );
    var fills = m.PlanSell(w, s, A, p2, Plate, 10 * U);
    Check(fills.Count == 1 && fills[0].Oct, "an order over its owner's limit is skipped");

    // OCT's stock limit
    Deposit(w, oct, Iron, 199_999 * U);
    Ok(w, m, "OCT buys up to 200 t", () => m.Sell(w, s, A, p3, Iron, U));
    Refused(w, m, "OCT is full", () => m.Sell(w, s, A, p3, Iron, U), "Nobody buys");
    Refused(w, m, "OCT trades only its catalog", () => m.Sell(w, s, A, p3, Gold, U), "");

    // OCT can't pay
    var poor = new FakeWorld();
    poor.Balances[oct] = 10;
    poor.Start = poor.Total();
    Deposit(poor, p1, Plate, 10 * U);
    var none = new MarketBook();
    Refused(
        poor,
        none,
        "OCT without money",
        () => none.Sell(poor, s, A, p1, Plate, 10 * U),
        "can't pay"
    );
}

// The specialty: 80 % for 50 t a day, then 60 %
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    Deposit(w, p2, Platinum, 80_000 * U);
    w.Limit = false;
    Ok(w, m, "sell 60 t of platinum at Alpha", () => m.Sell(w, s, A, p2, Platinum, 60_000 * U));
    Check(
        w.Balances[p2] == 25_000 + 50_000 * 28 + 10_000 * 21,
        "50 t at 28 SC and 10 t at 21 SC: " + w.Balances[p2]
    );
    Check(Math.Abs(m.SpecialtyBoughtKg(A) - 50_000) < 1e-6, "specialty: 50 t counted");
    Check(m.OctBid(w, s, A, Platinum) == 21_000, "after it, the normal price");
    long before = w.Balances[p2];
    Ok(w, m, "the same day all at 60 %", () => m.Sell(w, s, A, p2, Platinum, 1000 * U));
    Check(w.Balances[p2] == before + 21_000, "1 t at 21 SC");
    w.Now += 86_400;
    m.Restock(w, s, [A], []);
    Check(m.SpecialtyBoughtKg(A) == 0, "a new day: the specialty again");
}

// Listings: limits, cancel, expiry
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    Deposit(w, p1, Iron, 100 * U);
    for (int i = 0; i < 20; i++)
        Ok(w, m, $"offer {i + 1}", () => m.List(w, s, A, p1, ListingKind.Offer, Iron, U, 20_000));
    Refused(
        w,
        m,
        "the 21st listing",
        () => m.List(w, s, A, p1, ListingKind.Order, Iron, U, 1_000),
        "20 listings"
    );
    Ok(
        w,
        m,
        "another station has its own 20",
        () => m.List(w, s, B, p1, ListingKind.Order, Iron, U, 1_000)
    );
    Refused(
        w,
        m,
        "offer more than there is",
        () => m.List(w, s, B, p1, ListingKind.Offer, Iron, U, 1_000),
        "Only"
    );
    Refused(w, m, "no price", () => m.List(w, s, A, p2, ListingKind.Order, Iron, U, 0), "price");

    var first = m.At(A).OrderBy(l => l.Created).First();
    Refused(w, m, "cancel another's listing", () => m.Cancel(w, p2, first.Id), "No listing");
    Ok(w, m, "cancel an offer", () => m.Cancel(w, p1, first.Id));
    Check(Free(w, p1, Iron) == 81 * U, "the offer's items are back");
    var order = m.At(B).Single();
    Ok(w, m, "cancel an order", () => m.Cancel(w, p1, order.Id));
    Check(w.Balances[p1] == 25_000, "the order's money is back");

    Ok(
        w,
        m,
        "an order to expire",
        () => m.List(w, s, A, f10, ListingKind.Order, Plate, 2 * U, 10_000)
    );
    w.Now += 7 * 86_400 - 1;
    Check(m.Expire(w, s).Count == 0, "nothing expires before 7 days");
    w.Now += 1;
    var expired = m.Expire(w, s);
    Check(expired.Count == 20 && !m.Listings.Any(), "after 7 days all expire: " + expired.Count);
    Check(
        Free(w, p1, Iron) == 100 * U && w.Balances[f10] == 1_000_000,
        "expired: items and money back"
    );
    Check(w.Total() == w.Start, "expired: no money made or lost");
}

// Prices drift with the volume and recover
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = new MarketSettings();
    w.Limit = false;
    Deposit(w, oct, Iron, 150_000 * U);
    long ask = m.OctAsk(w, s, A, Iron);
    w.Balances[p1] = 10_000_000;
    w.Start = w.Total();
    Ok(w, m, "buy 100 t of iron", () => m.Buy(w, s, A, p1, Iron, 100_000 * U));
    Check(
        m.OctAsk(w, s, A, Iron) == 2 * ask,
        "100 t bought: twice the price " + m.OctAsk(w, s, A, Iron)
    );
    Check(m.OctBid(w, s, A, Iron) == 12_000, "OCT also pays more");
    Check(m.OctAsk(w, s, B, Iron) == ask, "other stations keep theirs");
    w.Now += 24 * 3600;
    Check(
        Math.Abs(m.OctAsk(w, s, A, Iron) - ask * Math.Sqrt(2)) < 2,
        "a day later half of it is gone"
    );
    Ok(w, m, "sell it back", () => m.Sell(w, s, A, p1, Iron, 100_000 * U));
    Check(m.OctAsk(w, s, A, Iron) < ask, "sold: below the reference");
    w.Now += 365 * 86_400;
    Check(m.OctAsk(w, s, A, Iron) == ask, "back to the reference");
    var saved = m.Serialize();
    Check(MarketBook.Deserialize(saved).Serialize() == saved, "drift: saved");
}

// Saved and wire formats, kiosk requests
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    m.Restock(w, s, [A], daily);
    Deposit(w, p1, Iron, 10 * U);
    m.List(w, s, A, p1, ListingKind.Offer, Iron, 5_500_000, 12_345);
    m.List(w, s, A, f10, ListingKind.Order, Plate, 3 * U, 70_000);
    Deposit(w, p2, Platinum, 10 * U);
    m.Sell(w, s, A, p2, Platinum, 10 * U);
    string saved = m.Serialize();
    var loaded = MarketBook.Deserialize(saved);
    Check(loaded.Serialize() == saved, "round trip");
    Check(
        loaded.Get("M1").Amount == 5_500_000 && loaded.Get("M2").Escrow == 210,
        "round trip: listings"
    );
    Check(
        Math.Abs(loaded.SpecialtyBoughtKg(A) - 10) < 1e-9 && loaded.Day == m.Day,
        "round trip: day"
    );
    try
    {
        MarketBook.Deserialize("octahedron-ledgers 1\n");
        Check(false, "a foreign text is refused");
    }
    catch (FormatException) { }

    var request = KioskRequest.Parse("offer\t7\t99\t1\t\tIngot/Iron\t2.5\t80%");
    Check(
        request.Verb == KioskVerb.Offer
            && request.PricePercent == 80
            && request.Amount == 2_500_000,
        "kiosk: offer at 80 %"
    );
    request = KioskRequest.Parse("order\t8\t99\t0\t\tIngot/Iron\t1\t12.3456");
    Check(request.Price == 12_346, "kiosk: price in SC, milli-SC rounded up: " + request.Price);
    request = KioskRequest.Parse("cancel\t9\t99\t0\t\t\t\t\tM7");
    Check(request.Listing == "M7", "kiosk: listing id");
    foreach (
        var bad in new[]
        {
            "offer\t1\t2\t0\t\tIngot/Iron\t1\t-5",
            "offer\t1\t2\t0\t\tIngot/Iron\t1\tx%",
        }
    )
        try
        {
            KioskRequest.Parse(bad);
            Check(false, "kiosk: bad price refused: " + bad);
        }
        catch (FormatException) { }

    KioskRequest Req(
        KioskVerb verb,
        bool faction = false,
        string item = Iron,
        long amount = U,
        long price = 1,
        string listing = ""
    ) =>
        new()
        {
            Verb = verb,
            Faction = faction,
            Item = item,
            Amount = amount,
            Price = price,
            Listing = listing,
        };
    Check(
        KioskRules.Refusal(Req(KioskVerb.Market, item: ""), false, false, false) == null,
        "rules: anyone sees the market"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Buy), false, false, false) == null,
        "rules: anyone buys"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Buy, true), true, false, false) != null,
        "rules: buying for the faction needs the rank"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Sell, true), true, true, false) == null,
        "rules: a trader sells for the faction"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Offer, price: 0), false, false, false)
            == "No price given.",
        "rules: an offer needs a price"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Buy, amount: 0), false, false, false) != null,
        "rules: buy needs an amount"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Sell, amount: 0), false, false, false) == null,
        "rules: sell all"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Cancel, item: ""), false, false, false) != null,
        "rules: cancel needs a listing"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Cancel, item: "", listing: "M1"), false, false, false)
            == null,
        "rules: cancel"
    );

    var wire = new MarketRequest
    {
        Verb = KioskVerb.Order,
        Station = A,
        Owner = f10,
        Item = Plate,
        Amount = 2 * U,
        PricePercent = 50,
    };
    var decoded = MarketRequest.Decode(wire.Encode());
    Check(
        decoded.Encode() == wire.Encode() && decoded.Reservation == null,
        "market request: wire round trip"
    );
    var r = m.Run(w, s, decoded);
    Check(
        r.Ok && m.Listings.Last().Price == 25_000,
        "market request: an order at 50 % of the reference: " + r.Message
    );
    Check(
        !m.Run(
            w,
            s,
            new MarketRequest
            {
                Verb = KioskVerb.Offer,
                Station = A,
                Owner = p1,
                Item = Gold,
                Amount = U,
                PricePercent = 50,
            }
        ).Ok,
        "market request: no percent without a reference"
    );
    Check(
        m.Run(
            w,
            s,
            new MarketRequest
            {
                Verb = KioskVerb.Cancel,
                Owner = f10,
                Listing = "M2",
            }
        ).Ok,
        "market request: cancel"
    );

    string view = m.View(w, s, A, p1, f10, item => item.Split('/')[1]);
    var lines = view.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Check(
        lines.Contains($"market\t{Iron}\tIron\t6000\t14000\t{2000 * U}\t12345\t5500000\t0\t0\t0"),
        "view: iron with OCT's prices and stock and the offer: " + view
    );
    Check(
        lines.Any(l => l.StartsWith($"market\t{Platinum}\tPlatinum\t28000\t49000\t{10 * U}\t"))
            && lines.Any(l => l.StartsWith($"market\t{Platinum}") && l.EndsWith("\t1")),
        "view: platinum at the specialty price"
    );
    Check(
        lines.Any(l =>
            l.StartsWith("listing\tM1\toffer\tIngot/Iron\tIron\t5500000\t12345\tmine\t")
        ),
        "view: my listing"
    );
    Check(
        lines.Any(l => l.StartsWith("listing\tM3\torder\t") && l.Contains("\tfaction\t")),
        "view: the faction's listing"
    );
    Check(lines.Last() == $"balance\t{w.Balances[p1]}\t{w.Balances[f10]}", "view: balances");
    Check(!lines.Any(l => l.Contains(Gold)), "view: only the catalog and listed items");
}

Console.WriteLine($"{checks - failures}/{checks} market checks passed");
return failures == 0 ? 0 : 1;

sealed class FakeWorld : IMarketWorld
{
    public static readonly LedgerOwner OctOwner = LedgerOwner.OfFaction(1000);
    private static readonly Dictionary<string, (double Kg, bool Integral, long Price)> Items = new()
    {
        ["Ingot/Iron"] = (1, false, 10_000),
        ["Component/SteelPlate"] = (20, true, 50_000),
        ["Ingot/Platinum"] = (1, false, 35_000),
        ["Ingot/Gold"] = (1, false, 0),
    };

    public long Now { get; set; } = 1_800_000_000;
    public LedgerOwner Oct => OctOwner;
    public LedgerBook Ledger { get; }
    public bool Limit = true;
    public readonly Dictionary<LedgerOwner, long> Balances = new();
    public long Start;

    public FakeWorld()
    {
        Ledger = new LedgerBook(new Rules(this));
        foreach (var id in new long[] { 1, 2, 3 })
            Balances[LedgerOwner.Player(id)] = 25_000;
        Balances[LedgerOwner.OfFaction(10)] = 1_000_000;
        Balances[OctOwner] = 20_000_000;
        Start = Total();
    }

    public long Total() => Balances.Values.Sum();

    public string Snapshot() =>
        Ledger.Serialize()
        + string.Join(
            ",",
            Balances.OrderBy(b => b.Key.ToString()).Select(b => $"{b.Key}={b.Value}")
        );

    public bool Item(string item, out double massKg, out bool integral)
    {
        bool known = Items.TryGetValue(item, out var info);
        massKg = info.Kg;
        integral = info.Integral;
        return known;
    }

    public long Reference(string station, string item) =>
        Items.TryGetValue(item, out var info) ? info.Price : 0;

    public IEnumerable<string> Catalog(string station) =>
        Items.Where(i => i.Value.Price > 0).Select(i => i.Key);

    public bool Specialty(string station, string item) =>
        station == "Alpha Station" && item == "Ingot/Platinum";

    public double RoomKg(string station, LedgerOwner owner)
    {
        double limit = new Rules(this).LimitKg(owner);
        return limit <= 0 ? double.PositiveInfinity : limit - Ledger.MassKg(station, owner);
    }

    public long Balance(LedgerOwner owner) => Balances.TryGetValue(owner, out long b) ? b : -1;

    public bool ChangeBalance(LedgerOwner owner, long amount)
    {
        if (!Balances.TryGetValue(owner, out long b) || b + amount < 0)
            return false;
        Balances[owner] = b + amount;
        return true;
    }

    public string ApplyLedger(IReadOnlyList<LedgerOp> ops) => Ledger.Apply(ops);

    private sealed class Rules(FakeWorld world) : ILedgerRules
    {
        public bool Item(string item, out double massKg, out bool integral) =>
            world.Item(item, out massKg, out integral);

        // 100 t a player, none for OCT
        public double LimitKg(LedgerOwner owner) =>
            !world.Limit || owner.Equals(OctOwner) ? 0
            : owner.Faction ? 1_000_000
            : 100_000;

        public bool KnownStation(string station) => station is "Alpha Station" or "Beta Station";

        public bool KnownOwner(LedgerOwner owner) =>
            owner.Faction ? owner.Id is 10 or 1000 : owner.Id is 1 or 2 or 3;
    }
}
