using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks of the shipyard queues and build ledgers (ServerPlugin/Builds/BuildBook.cs) against a
// fake world with the real market book: the labor fee, quotes from the ledger, listings and OCT,
// paid orders all or none, cancel, start and finish with leftovers and hydrogen, free orders,
// the queue, the saved and wire formats, and that no money is made or lost. Prints one line per
// failed check and a summary; exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/builds/BuildTests

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
const string Plate = "Component/SteelPlate"; // 20 kg, whole, 50 SC: OCT sells at 70
const string Motor = "Component/Motor"; // 24 kg, whole, 100 SC: OCT sells at 140
const string Ice = "Ore/Ice"; // 1 kg, 1 SC: OCT sells at 1.4
const string Gold = "Ingot/Gold"; // not in OCT's catalog
var p1 = LedgerOwner.Player(1);
var p2 = LedgerOwner.Player(2);
var f10 = LedgerOwner.OfFaction(10);
var oct = FakeWorld.OctOwner;
var s = new BuildSettings { QueueMax = 3 };

long Free(FakeWorld w, LedgerOwner owner, string item, string station = A) =>
    w.Ledger.FreeOf(station, owner).TryGetValue(item, out long raw) ? raw : 0;

LedgerBook.Reservation Reserved(FakeWorld w, BuildOrder order) =>
    w.Ledger.Get(order.Station)?.Reservations.TryGetValue(order.Reservation, out var r) == true
        ? r
        : null;

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

(FakeWorld, MarketBook, FakeMarket, BuildBook) Setup()
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var ms = new MarketSettings { DriftKg = 0 };
    m.Restock(w, ms, [A, B], [new(Plate, 1000 * U), new(Motor, 100 * U), new(Ice, 100_000 * U)]);
    return (w, m, new FakeMarket(w, m, ms), new BuildBook());
}

Dictionary<string, long> Needs(params (string Item, long Raw)[] items) =>
    items.ToDictionary(i => i.Item, i => i.Raw);

var none = new Dictionary<string, long>();

BuildOrder Paid(LedgerOwner payer, long player = 1, string station = A, long fee = 100) =>
    new()
    {
        Station = station,
        Player = player,
        Payer = payer,
        Fee = fee,
        Pcu = 1000,
        Ship = "Test\tShip",
        Blueprint = "QUJD",
    };

string State(FakeWorld w, BuildBook b) => w.Snapshot() + b.Serialize();

void Refused(
    FakeWorld w,
    BuildBook b,
    MarketBook m,
    string what,
    Func<BuildResult> act,
    string message
)
{
    string before = State(w, b) + m.Serialize();
    var r = act();
    Check(!r.Ok && r.Message.Contains(message), $"{what}: refused ({r.Message})");
    Check(State(w, b) + m.Serialize() == before, what + ": nothing changed");
}

BuildResult Ok(FakeWorld w, string what, Func<BuildResult> act)
{
    var r = act();
    Check(r.Ok, $"{what}: {r.Message}");
    Check(w.Total() == w.Start, $"{what}: no money made or lost ({w.Total() - w.Start})");
    return r;
}

// The labor fee: 1 SC per 10 PCU, rounded up
{
    Check(BuildBook.Fee(60_000, 1) == 6_000, "fee: 60k PCU is 6,000 SC");
    Check(BuildBook.Fee(15, 1) == 2, "fee: rounded up");
    Check(BuildBook.Fee(0, 1) == 0 && BuildBook.Fee(500, 0) == 0, "fee: none");
}

// Quote: ledger first, then the cheapest listings, then OCT; what nobody has is missing
{
    var (w, m, market, b) = Setup();
    Deposit(w, p1, Plate, 30 * U);
    Deposit(w, p2, Plate, 50 * U);
    Check(
        m.List(
            w,
            new MarketSettings { DriftKg = 0 },
            A,
            p2,
            ListingKind.Offer,
            Plate,
            20 * U,
            60_000
        ).Ok,
        "p2 offers 20 plates at 60 SC"
    );
    var q = BuildBook.Quote(w, market, A, p1, Needs((Plate, 100 * U), (Gold, 5 * U)), 100);
    var plate = q.Lines.Single(l => l.Item == Plate);
    Check(plate.Ledger == 30 * U, "quote: 30 plates from the ledger");
    Check(
        plate.Listings == 20 * U && plate.ListingsCost == 1_200,
        "quote: 20 from p2's listing at 60"
    );
    Check(plate.Oct == 50 * U && plate.OctCost == 3_500, "quote: 50 from OCT at 70");
    var gold = q.Lines.Single(l => l.Item == Gold);
    Check(gold.Missing == 5 * U && !q.Complete, "quote: gold is missing");
    Check(q.Fee == 100 && q.Total == 4_800 && q.Balance == 25_000, $"quote: totals {q.Total}");
    var back = BuildQuote.Decode(q.Encode());
    Check(back.Encode() == q.Encode() && back.Total == q.Total, "quote: wire format");
    var noMarket = BuildBook.Quote(w, null, A, p1, Needs((Plate, 100 * U)), 0);
    Check(noMarket.Lines[0].Missing == 70 * U, "quote without the market: the ledger only");
    string text = BuildBook.QuoteText(q, Ice, 0, i => i, (i, raw) => LedgerBook.Units(raw));
    Check(
        text.Contains("Labor fee: 100 SC") && text.Contains("Total: 4,800 SC"),
        "quote text: " + text
    );
}

// A paid order: the build ledger gets every item, the payer pays the quote and the fee
{
    var (w, m, market, b) = Setup();
    Deposit(w, p1, Plate, 30 * U);
    Deposit(w, p2, Plate, 50 * U);
    m.List(w, new MarketSettings { DriftKg = 0 }, A, p2, ListingKind.Offer, Plate, 20 * U, 60_000);
    var needed = Needs((Plate, 100 * U), (Motor, 2 * U), (Ice, 500 * U));
    var q = BuildBook.Quote(w, market, A, p1, needed, 100);
    Check(q.Total == 1_200 + 3_500 + 280 + 700 + 100, $"order quote {q.Total}");

    Refused(
        w,
        b,
        m,
        "the price went up",
        () => b.Order(w, market, s, Paid(p1), needed, none, q.Total - 1),
        "press again"
    );
    var r = Ok(w, "order", () => b.Order(w, market, s, Paid(p1), needed, none, q.Total));
    var order = r.Order;
    Check(order.Id == "b1" && b.Get("b1") == order, "order: id b1");
    Check(order.Ship == "Test Ship", "order: the ship's name has no tab");
    Check(w.Balances[p1] == 25_000 - q.Total, "order: the payer paid the total " + w.Balances[p1]);
    Check(w.Balances[p2] == 25_000 + 1_200 - 60, "order: p2 got its sale minus the 5 % fee");
    Check(Free(w, p1, Plate) == 0, "order: the ledger's plates went into the build");
    var reserved = Reserved(w, order);
    Check(
        reserved != null
            && !reserved.Counted
            && reserved.Items[Plate] == 100 * U
            && reserved.Items[Motor] == 2 * U
            && reserved.Items[Ice] == 500 * U,
        "order: the build ledger holds every item, uncounted"
    );
    Check(w.Ledger.MassKg(A, p1) == 0, "order: the build ledger is not in the payer's limit");
    Check(order.Items.Count == 3 && order.Items[Plate] == 100 * U, "order: its items");

    Refused(
        w,
        b,
        m,
        "a second order at the same shipyard",
        () => b.Order(w, market, s, Paid(p1), needed, none, long.MaxValue),
        "one ship at a time"
    );

    // Cancel: the build ledger and the fee come back
    long octBefore = w.Balances[oct];
    Refused(w, b, m, "p2 has nothing to cancel", () => b.Cancel(w, A, 2), "no ship");
    Ok(w, "cancel", () => b.Cancel(w, A, 1));
    Check(Reserved(w, order) == null && b.Get("b1") == null, "cancel: build ledger and order gone");
    Check(
        Free(w, p1, Plate) == 100 * U && Free(w, p1, Motor) == 2 * U && Free(w, p1, Ice) == 500 * U,
        "cancel: the items are in the payer's ledger"
    );
    Check(w.Balances[oct] == octBefore - 100, "cancel: OCT paid the fee back");
    Check(w.Balances[p1] == 25_000 - q.Total + 100, "cancel: the payer got the fee back");

    // Ordered again from the ledger alone: only the fee is paid
    var again = Ok(
        w,
        "order from the ledger",
        () => b.Order(w, market, s, Paid(p1), needed, none, 100)
    );
    Check(again.Order.Id == "b2", "ids are not reused");
    Check(w.Balances[p1] == 25_000 - q.Total, "order from the ledger: only the fee");
}

// Refusals leave everything as it was
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 100 * U));
    Refused(
        w,
        b,
        m,
        "more than the station has",
        () => b.Order(w, market, s, Paid(p1), Needs((Plate, 5000 * U)), none, long.MaxValue),
        "more than this station has"
    );
    Refused(
        w,
        b,
        m,
        "the account can't pay",
        () => b.Order(w, market, s, Paid(p1, fee: 20_000), needed, none, long.MaxValue),
        "the account has"
    );
    Refused(
        w,
        b,
        m,
        "an unknown station",
        () => b.Order(w, market, s, Paid(p1, station: ""), needed, none, long.MaxValue),
        "Unknown station"
    );
    // A purchase failing half way: what was bought stays the payer's, nothing is queued
    market.FailOn = Plate;
    var r = b.Order(
        w,
        market,
        s,
        Paid(p1),
        Needs((Plate, 10 * U), (Motor, 1 * U)),
        none,
        long.MaxValue
    );
    Check(!r.Ok && r.Message.Contains("in the ledger"), "a failed purchase: " + r.Message);
    Check(
        b.Orders.Count() == 0 && w.Ledger.Get(A).Reservations.Count == 0,
        "a failed purchase: no order"
    );
    Check(
        Free(w, p1, Motor) == 1 * U && w.Balances[p1] == 25_000 - 140,
        "a failed purchase: the motor bought is p1's"
    );
    Check(w.Total() == w.Start, "a failed purchase: no money made or lost");
}

// Start and finish: components out at the start, leftovers back and hydrogen used at the end
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 100 * U), (Motor, 2 * U), (Ice, 500 * U));
    var order = Ok(
        w,
        "order",
        () => b.Order(w, market, s, Paid(p1), needed, none, long.MaxValue)
    ).Order;
    Check(b.Next(A) == order && b.Next(B) == null, "next: the order waits at Alpha");
    var start = Ok(w, "start", () => b.Start(w, s, order.Id));
    Check(order.Started && b.Next(A) == null, "start: running, nothing next");
    var reserved = Reserved(w, order);
    Check(
        reserved != null && reserved.Items.Count == 1 && reserved.Items[Ice] == 500 * U,
        "start: only the ice stays in the build ledger"
    );
    Refused(w, b, m, "start twice", () => b.Start(w, s, order.Id), "being built");
    Refused(w, b, m, "cancel a running build", () => b.Cancel(w, A, 1), "can't be canceled");

    Deposit(w, p1, Plate, 4_999 * U); // the payer at their limit
    Ok(
        w,
        "finish",
        () => b.Finish(w, s, order.Id, Needs((Plate, 7 * U), (Ice, 3 * U)), 300 * U, refund: false)
    );
    Check(
        b.Get(order.Id) == null && Reserved(w, order) == null,
        "finish: order and build ledger gone"
    );
    Check(
        Free(w, p1, Plate) == 4_999 * U + 7 * U,
        "finish: the leftovers are back, past the payer's limit"
    );
    Check(
        Free(w, p1, Ice) == 200 * U,
        "finish: the ice not used is back: " + LedgerBook.Units(Free(w, p1, Ice))
    );
}

// Finish with a refund: the build couldn't start
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 10 * U), (Ice, 50 * U));
    var order = Ok(
        w,
        "order",
        () => b.Order(w, market, s, Paid(p1), needed, none, long.MaxValue)
    ).Order;
    long paid = 25_000 - w.Balances[p1];
    b.Start(w, s, order.Id);
    Ok(
        w,
        "finish with refund",
        () => b.Finish(w, s, order.Id, Needs((Plate, 10 * U)), 0, refund: true)
    );
    Check(w.Balances[p1] == 25_000 - paid + 100, "refund: the fee is back");
    Check(Free(w, p1, Plate) == 10 * U && Free(w, p1, Ice) == 50 * U, "refund: everything is back");
}

// Hydrogen from a ship at the station: put into the build ledger, not bought
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 10 * U), (Ice, 500 * U));
    var provided = Needs((Ice, 300 * U));
    var order = Ok(
        w,
        "order with ship hydrogen",
        () => b.Order(w, market, s, Paid(p1), needed, provided, 700 + 280 + 100)
    ).Order;
    Check(
        w.Balances[p1] == 25_000 - 700 - 280 - 100,
        "ship hydrogen: only 200 kg of ice bought " + w.Balances[p1]
    );
    Check(
        Reserved(w, order).Items[Ice] == 500 * U,
        "ship hydrogen: the build ledger has all the ice"
    );
    // Refused: what came from the ship stays with the caller
    Refused(
        w,
        b,
        m,
        "ship hydrogen, refused",
        () =>
            b.Order(
                w,
                market,
                s,
                Paid(p2, player: 2, fee: 30_000),
                needed,
                provided,
                long.MaxValue
            ),
        "the account has"
    );
}

// Too little ice for sale: the order goes ahead with the ice there is
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 1 * U), (Ice, 150_000 * U));
    var q = BuildBook.Quote(w, market, A, f10, needed, 0);
    Check(q.Short(Ice) == null && !q.Complete, "short of ice: not short of anything else");
    var order = Ok(
        w,
        "order short of ice",
        () => b.Order(w, market, s, Paid(f10, fee: 0), needed, none, long.MaxValue)
    ).Order;
    Check(
        order.Items[Ice] == 100_000 * U && Reserved(w, order).Items[Ice] == 100_000 * U,
        "short of ice: what OCT had"
    );
    Refused(
        w,
        b,
        m,
        "short of plates",
        () =>
            b.Order(
                w,
                market,
                s,
                Paid(p2, player: 2),
                Needs((Plate, 2000 * U)),
                none,
                long.MaxValue
            ),
        "more than this station has"
    );
}

// The faction pays from its ledger and account
{
    var (w, m, market, b) = Setup();
    Deposit(w, f10, Motor, 5 * U);
    var order = Ok(
        w,
        "faction order",
        () =>
            b.Order(
                w,
                market,
                s,
                Paid(f10),
                Needs((Motor, 5 * U), (Plate, 1 * U)),
                none,
                long.MaxValue
            )
    ).Order;
    Check(
        w.Balances[f10] == 1_000_000 - 70 - 100 && w.Balances[p1] == 25_000,
        "faction order: the faction paid"
    );
    Check(
        Reserved(w, order).Owner.Equals(f10) && Free(w, f10, Motor) == 0,
        "faction order: its build ledger"
    );
    Ok(w, "faction cancel", () => b.Cancel(w, A, 1));
    Check(
        Free(w, f10, Motor) == 5 * U && Free(w, f10, Plate) == 1 * U,
        "faction cancel: back to the faction"
    );
}

// Free orders: no ledger, one at a time anywhere, the queue in order
{
    var (w, m, market, b) = Setup();
    string before = w.Snapshot();
    var free = new BuildOrder
    {
        Station = A,
        Player = 1,
        Ship = "Free",
        Blueprint = "QQ==",
    };
    var r = Ok(w, "free order", () => b.Order(w, market, s, free, none, none, 0));
    Check(r.Order.Free && w.Snapshot() == before, "free order: nothing paid or reserved");
    Refused(
        w,
        b,
        m,
        "a second free order at another shipyard",
        () => b.Order(w, market, s, new BuildOrder { Station = B, Player = 1 }, none, none, 0),
        "one free ship"
    );
    w.Now += 10;
    Ok(
        w,
        "p2 queues",
        () => b.Order(w, market, s, Paid(p2, player: 2), Needs((Plate, 1 * U)), none, long.MaxValue)
    );
    w.Now += 10;
    Ok(
        w,
        "p3 queues",
        () => b.Order(w, market, s, new BuildOrder { Station = A, Player = 3 }, none, none, 0)
    );
    Refused(
        w,
        b,
        m,
        "the queue is full",
        () =>
            b.Order(w, market, s, Paid(f10, player: 4), Needs((Plate, 1 * U)), none, long.MaxValue),
        "queue is full"
    );
    var queue = b.Queue(A);
    Check(queue.Select(o => o.Player).SequenceEqual(new long[] { 1, 2, 3 }), "queue: in order");
    Check(b.Next(A).Player == 1, "next: the first");
    b.Start(w, s, b.Next(A).Id);
    Check(
        b.Next(A) == null && b.Queue(A)[0].Started,
        "queue: the running build first, nothing next"
    );
    Ok(w, "free finish", () => b.Finish(w, s, queue[0].Id, none, 0, false));
    Check(b.Next(A).Player == 2, "next: the second after the first ended");
    Ok(w, "free cancel", () => b.Cancel(w, A, 3));
    Check(b.Queue(A).Count == 1, "free cancel: gone");

    // The saved format keeps everything, blueprints included
    string saved = b.Serialize();
    var loaded = BuildBook.Deserialize(saved);
    Check(loaded.Serialize() == saved, "saved format: round trip");
    var o2 = loaded.Queue(A)[0];
    Check(
        o2.Payer.Equals(p2) && o2.Fee == 100 && o2.Items[Plate] == U && o2.Blueprint == "QUJD",
        "saved format: an order"
    );
    var wire = BuildBook.DecodeOrder(BuildBook.EncodeOrder(o2, blueprint: false));
    Check(
        wire.Blueprint == "" && wire.Id == o2.Id && wire.Ship == o2.Ship,
        "wire format: without the blueprint"
    );
    var next = Ok(
        w,
        "order after the load",
        () => loaded.Order(w, market, s, new BuildOrder { Station = B, Player = 3 }, none, none, 0)
    );
    Check(next.Order.Id == "b4", "saved format: the next id " + next.Order.Id);
}

// The requests a node sends, and the book answering them
{
    var (w, m, market, b) = Setup();
    var request = new BuildRequest
    {
        Verb = BuildVerb.Order,
        Station = A,
        Player = 1,
        Payer = p1,
        Fee = 100,
        Pcu = 1000,
        Ship = "A\tShip",
        Blueprint = "QUJD",
        MaxTotal = 99_999,
        Needed = Needs((Plate, 3 * U), (Ice, 10 * U)),
        Provided = Needs((Ice, 4 * U)),
    };
    var back = BuildRequest.Decode(request.Encode());
    Check(
        back.Encode() == request.Encode() && back.Ship == "A Ship" && back.Provided[Ice] == 4 * U,
        "request: wire format"
    );
    string answer = back.Run(b, w, market, s);
    Check(
        answer.StartsWith("1\t") && answer.Contains("\nO\tb1\t"),
        "request: order answered with the order: " + answer
    );
    string queue = new BuildRequest { Verb = BuildVerb.Queue, Station = A }.Run(b, w, market, s);
    Check(BuildBook.DecodeOrder(queue).Blueprint == "", "request: the queue without blueprints");
    string next = new BuildRequest { Verb = BuildVerb.Next, Station = A }.Run(b, w, market, s);
    Check(BuildBook.DecodeOrder(next).Blueprint == "QUJD", "request: next with its blueprint");
    string quote = new BuildRequest
    {
        Verb = BuildVerb.Quote,
        Station = A,
        Payer = p1,
        Needed = Needs((Plate, 3 * U)),
    }.Run(b, w, market, s);
    Check(BuildQuote.Decode(quote).Lines[0].OctCost == 210, "request: a quote");
    string cancel = new BuildRequest
    {
        Verb = BuildVerb.Cancel,
        Station = A,
        Player = 2,
    }.Run(b, w, market, s);
    Check(cancel.StartsWith("0\t"), "request: a refusal answers 0");
    Check(
        new BuildRequest { Verb = BuildVerb.Start, Id = "b1" }
            .Run(b, w, market, s)
            .StartsWith("1"),
        "request: start"
    );
    Check(
        new BuildRequest
        {
            Verb = BuildVerb.Finish,
            Id = "b1",
            HydrogenUsed = 10 * U,
        }
            .Run(b, w, market, s)
            .StartsWith("1"),
        "request: finish"
    );
    Check(
        b.Orders.Count() == 0 && w.Total() == w.Start,
        "request: all done, no money made or lost"
    );
}

Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;

sealed class FakeMarket(FakeWorld w, MarketBook m, MarketSettings s) : IBuildMarket
{
    public string FailOn;

    public List<MarketFill> PlanBuy(string station, LedgerOwner buyer, string item, long raw) =>
        m.PlanBuy(w, s, station, buyer, item, raw);

    public MarketResult Buy(
        string station,
        LedgerOwner buyer,
        string item,
        long raw,
        string reservation
    ) =>
        item == FailOn
            ? MarketResult.No("Sold out.")
            : m.Buy(w, s, station, buyer, item, raw, reservation);
}

sealed class FakeWorld : IMarketWorld
{
    public static readonly LedgerOwner OctOwner = LedgerOwner.OfFaction(1000);
    private static readonly Dictionary<string, (double Kg, bool Integral, long Price)> Items = new()
    {
        ["Component/SteelPlate"] = (20, true, 50_000),
        ["Component/Motor"] = (24, true, 100_000),
        ["Ore/Ice"] = (1, false, 1_000),
        ["Ingot/Gold"] = (1, false, 0),
    };

    public long Now { get; set; } = 1_800_000_000;
    public LedgerOwner Oct => OctOwner;
    public LedgerBook Ledger { get; }
    public readonly Dictionary<LedgerOwner, long> Balances = new();
    public long Start;

    public FakeWorld()
    {
        Ledger = new LedgerBook(new Rules(this));
        foreach (var id in new long[] { 1, 2, 3, 4 })
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

    public bool Specialty(string station, string item) => false;

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

        // 100 t a player, 1,000 t a faction, none for OCT
        public double LimitKg(LedgerOwner owner) =>
            owner.Equals(OctOwner) ? 0
            : owner.Faction ? 1_000_000
            : 100_000;

        public bool KnownStation(string station) => station is "Alpha Station" or "Beta Station";

        public bool KnownOwner(LedgerOwner owner) =>
            owner.Faction ? owner.Id is 10 or 1000 : owner.Id is >= 1 and <= 4;
    }
}
