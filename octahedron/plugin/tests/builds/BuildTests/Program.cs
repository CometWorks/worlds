using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ServerPlugin;

// Checks of the shipyard queues (ServerPlugin/Builds/BuildBook.cs) against a fake world with the
// real market book: the labor fee, quotes from the Store, paid orders all or none, cancel, start
// and finish with leftovers and hydrogen, free orders, the queue, the saved and wire formats, and
// that no money is made or lost. Prints one line per failed check and a summary; exit code 0 =
// all pass.
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

const long U = Amounts.Unit;
const string A = "Alpha Station";
const string B = "Beta Station";
const string Plate = "Component/SteelPlate"; // 20 kg, whole, 50 SC: OCT sells at 51, buys at 49
const string Motor = "Component/Motor"; // 24 kg, whole, 100 SC: OCT sells at 102, buys at 98
const string Ice = "Ore/Ice"; // 1 kg, 1 SC: OCT sells at 1, sold back at 0.98
const string Gold = "Ingot/Gold"; // not in OCT's catalog
const long P1 = 1;
const long P2 = 2;
var s = new BuildSettings { QueueMax = 3 };

(FakeWorld, MarketBook, FakeMarket, BuildBook) Setup()
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var ms = new MarketSettings { DriftKg = 0 };
    m.Restock(
        w,
        ms,
        [A, B],
        _ => [new(Plate, 1000 * U), new(Motor, 100 * U), new(Ice, 100_000 * U)]
    );
    return (w, m, new FakeMarket(w, m, ms), new BuildBook());
}

Dictionary<string, long> Needs(params (string Item, long Raw)[] items) =>
    items.ToDictionary(i => i.Item, i => i.Raw);

var none = new Dictionary<string, long>();

BuildOrder Paid(long payer, long player = 1, string station = A, long fee = 100) =>
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

// The labor fee: SC per 100 PCU, rounded up
{
    Check(
        ShipyardPlacement.Offset(70, 282.5) == 282.5 / 6,
        "placement: short ship at the 1/3 point"
    );
    Check(ShipyardPlacement.Offset(282.5 / 4, 282.5) == 0, "placement: a quarter long is centered");
    Check(ShipyardPlacement.Offset(100, 282.5) == 0, "placement: long ship centered");
    double[] room = [90, 60, 280];
    Check(
        ShipyardPlacement.Align([80, 20, 30], room).SequenceEqual([0, 1, 2]),
        "align: a ship that fits as it is stays"
    );
    Check(
        ShipyardPlacement.Align([100, 20, 30], room).SequenceEqual([2, 1, 0]),
        "align: too wide, turned about its up"
    );
    Check(
        ShipyardPlacement.Align([20, 70, 100], room).SequenceEqual([1, 0, 2]),
        "align: too tall, rolled"
    );
    Check(
        ShipyardPlacement.Align([200, 70, 20], room).SequenceEqual([1, 2, 0]),
        "align: long sideways and too tall, the last axis along the shipyard's width"
    );
    Check(
        ShipyardPlacement.Align([300, 10, 10], room).SequenceEqual([2, 1, 0]),
        "align: too long for any turn, the one that misses the least"
    );
    string Frame(params (string Name, bool Flies, bool Main)[] controllers) =>
        ShipyardPlacement
            .FrameBlock(
                controllers.Select(c => Tuple.Create(c.Name, c.Flies, c.Main)),
                c => c.Item2,
                c => c.Item3
            )
            ?.Item1;
    Check(
        Frame(
            ("cryo", false, false),
            ("seat", false, false),
            ("bed", false, false),
            ("helm", true, false)
        ) == "helm",
        "frame: the cockpit behind a cryo chamber, a seat and a bed"
    );
    Check(
        Frame(("helm", true, false), ("bridge", true, true)) == "bridge",
        "frame: the main cockpit wins"
    );
    Check(
        Frame(("bed", false, true), ("helm", true, false)) == "helm",
        "frame: a bed flagged as main doesn't count"
    );
    Check(
        Frame(("helm", true, false), ("bridge", true, false)) == "helm",
        "frame: the first cockpit"
    );
    Check(
        Frame(("cryo", false, false), ("seat", false, false), ("bed", false, true)) == null,
        "frame: nothing that flies the ship, none"
    );
    Check(Frame() == null, "frame: no controllers, none");
    Check(BuildBook.Fee(60_000, 10) == 6_000, "fee: 60k PCU at 10 SC is 6,000 SC");
    Check(BuildBook.Fee(15, 10) == 2 && BuildBook.Fee(101, 1) == 2, "fee: rounded up");
    Check(BuildBook.Fee(0, 10) == 0 && BuildBook.Fee(500, 0) == 0, "fee: none");
}

// Quote: at the Store's price, what it doesn't have is missing
{
    var (w, m, market, b) = Setup();
    var quote = BuildBook.Quote(
        w,
        market,
        A,
        P1,
        Needs((Plate, 10 * U), (Motor, 150 * U), (Gold, 2 * U)),
        100
    );
    var plate = quote.Lines.First(l => l.Item == Plate);
    var motor = quote.Lines.First(l => l.Item == Motor);
    var gold = quote.Lines.First(l => l.Item == Gold);
    Check(plate.Bought == 10 * U && plate.Cost == 510 && plate.Missing == 0, "quote: plates");
    Check(
        motor.Bought == 100 * U && motor.Cost == 10_200 && motor.Missing == 50 * U,
        "quote: motors"
    );
    Check(gold.Bought == 0 && gold.Missing == 2 * U, "quote: gold isn't for sale");
    Check(quote.Cost == 10_710 && quote.Total == 10_810 && !quote.Complete, "quote: totals");
    Check(quote.Short(Ice) != null, "quote: short of something");
    Check(quote.Balance == 25_000, "quote: the balance");
    Check(BuildQuote.Decode(quote.Encode()).Encode() == quote.Encode(), "quote: wire format");
    Check(
        BuildBook.Quote(w, null, A, P1, Needs((Plate, U)), 0).Lines[0].Missing == U,
        "quote: market closed, all missing"
    );
    string Text(BuildQuote q) =>
        BuildBook.QuoteText(q, Ice, 0, i => i, (i, raw) => Amounts.Units(raw));
    Check(Text(quote).Contains("Labor fee: 100 SC"), "quote text with a fee");
    Check(Text(quote).Contains("Total: 10,810 SC"), "quote text: the total");
    Check(
        !Text(BuildBook.Quote(w, market, A, P1, Needs((Plate, U)), 0)).Contains("Labor"),
        "no fee"
    );
}

// A paid order: pays the quote and the fee, stock goes down, the order holds every item
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 10 * U), (Motor, 2 * U));
    var r = Ok(w, "paid order", () => b.Order(w, market, s, Paid(P1), needed, none, 814));
    Check(r.Message.EndsWith("714 SC for the components and 100 SC labor paid."), r.Message);
    Check(w.Balances[P1] == 25_000 - 814, "paid: " + w.Balances[P1]);
    Check(w.Balances[FakeWorld.OctAccount] == 20_000_814, "OCT got it");
    Check(m.Stock(A, Plate) == 990 * U && m.Stock(A, Motor) == 98 * U, "stock went down");
    Check(m.Stock(B, Plate) == 1000 * U, "another station's stays");
    Check(r.Order.Items[Plate] == 10 * U && r.Order.Items[Motor] == 2 * U, "the order holds them");
    Check(r.Order.Paid == 714 && r.Order.Fee == 100 && r.Order.Payer == P1, "what it paid");
    Check(r.Order.Ship == "Test Ship", "the name is clean");
    Check(b.Queue(A).Single().Id == r.Order.Id, "queued");

    var message = Ok(w, "cancel", () => b.Cancel(w, market, s, A, P1)).Message;
    Check(message.Contains("814 SC is paid back"), "cancel: " + message);
    Check(w.Balances[P1] == 25_000, "all money back");
    Check(m.Stock(A, Plate) == 1000 * U && m.Stock(A, Motor) == 100 * U, "all stock back");
    Check(b.Queue(A).Count == 0, "out of the queue");
}

// Refusals leave everything as it was
{
    var (w, m, market, b) = Setup();
    Refused(
        w,
        b,
        m,
        "more than the Store has",
        () => b.Order(w, market, s, Paid(P1), Needs((Motor, 101 * U)), none, long.MaxValue),
        "more than this station has"
    );
    Refused(
        w,
        b,
        m,
        "the price went up",
        () => b.Order(w, market, s, Paid(P1), Needs((Plate, 10 * U)), none, 609),
        "see the new quote"
    );
    w.Balances[P1] = 100;
    w.Start = w.Total();
    Refused(
        w,
        b,
        m,
        "too poor",
        () => b.Order(w, market, s, Paid(P1), Needs((Plate, 10 * U)), none, long.MaxValue),
        "the account has"
    );
    Refused(
        w,
        b,
        m,
        "no account",
        () => b.Order(w, market, s, Paid(99), Needs((Plate, U)), none, long.MaxValue),
        "No bank account"
    );
    w.Balances[P1] = 25_000;
    w.Start = w.Total();
    market.FailOn = Plate;
    string before = w.Snapshot() + m.Serialize();
    var failed = b.Order(
        w,
        market,
        s,
        Paid(P1),
        Needs((Motor, U), (Plate, U)),
        none,
        long.MaxValue
    );
    market.FailOn = null;
    Check(
        !failed.Ok && failed.Message.Contains("Buying"),
        "a purchase fails half way: " + failed.Message
    );
    Check(w.Total() == w.Start && w.Balances[P1] == 25_000, "the part bought is paid back");
    Check(m.Stock(A, Motor) == 100 * U, "the part bought is back in stock");
    Check(b.Queue(A).Count == 0, "no order");

    Ok(w, "one", () => b.Order(w, market, s, Paid(P1), Needs((Plate, U)), none, long.MaxValue));
    Refused(
        w,
        b,
        m,
        "a second order at this shipyard",
        () => b.Order(w, market, s, Paid(P1), Needs((Plate, U)), none, long.MaxValue),
        "one entry at a time"
    );
    Refused(w, b, m, "nobody's cancel", () => b.Cancel(w, market, s, A, P2), "no ship");
    string id = b.Queue(A)[0].Id;
    b.Start(id);
    Refused(w, b, m, "cancel once running", () => b.Cancel(w, market, s, A, P1), "being built");
    Check(!b.Start(id).Ok, "start twice");
    Check(!b.Start("nope").Ok, "start nothing");
    Check(!b.Finish(w, market, s, "nope", none, 0, false).Ok, "finish nothing");
}

// Finish: leftovers and unused ice are sold back on Store terms for the player, the fee stays
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 10 * U), (Ice, 100 * U));
    var o = Ok(
        w,
        "order",
        () => b.Order(w, market, s, Paid(P1), needed, none, long.MaxValue)
    ).Order;
    Check(o.Paid == 510 + 100, "components and ice: " + o.Paid);
    Ok(w, "start", () => b.Start(o.Id));
    long paid = w.Balances[P1];
    var r = Ok(
        w,
        "finish",
        () => b.Finish(w, market, s, o.Id, Needs((Plate, 2 * U)), 60 * U, false)
    );
    Check(r.Message == "What was left over is sold for 137 SC.", r.Message);
    Check(w.Balances[P1] == paid + 2 * 49 + 39, "sold at 49 and 0.98: " + (w.Balances[P1] - paid));
    Check(m.Stock(A, Plate) == 992 * U, "the plates are back in stock");
    Check(m.Stock(A, Ice) == 99_940 * U, "the ice not used is back in stock: " + m.Stock(A, Ice));
    Check(b.Get(o.Id) == null, "gone");

    var clean = Ok(
        w,
        "another",
        () => b.Order(w, market, s, Paid(P1, fee: 0), Needs((Plate, U)), none, long.MaxValue)
    ).Order;
    b.Start(clean.Id);
    var nothing = Ok(w, "nothing left", () => b.Finish(w, market, s, clean.Id, none, 0, false));
    Check(nothing.Message == "", "nothing left: no message: " + nothing.Message);
}

// Finish with a refund: all back, started or not, whatever the welders used
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 10 * U));
    string id = Ok(
        w,
        "order",
        () => b.Order(w, market, s, Paid(P1), needed, none, long.MaxValue)
    ).Order.Id;
    var r = Ok(w, "refund", () => b.Finish(w, market, s, id, none, 0, refund: true));
    Check(r.Message == "610 SC is paid back.", r.Message);
    Check(w.Balances[P1] == 25_000 && m.Stock(A, Plate) == 1000 * U, "not started: all back");

    id = Ok(
        w,
        "order",
        () => b.Order(w, market, s, Paid(P1), needed, none, long.MaxValue)
    ).Order.Id;
    b.Start(id);
    Ok(w, "refund", () => b.Finish(w, market, s, id, Needs((Plate, U)), 0, refund: true));
    Check(w.Balances[P1] == 25_000 && m.Stock(A, Plate) == 1000 * U, "started: all back");
}

// Hydrogen from a ship at the station: not bought, sold for the player when it can't be used
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, U), (Ice, 100 * U));
    var o = Ok(
        w,
        "order with a ship's hydrogen",
        () => b.Order(w, market, s, Paid(P1, fee: 0), needed, Needs((Ice, 60 * U)), long.MaxValue)
    ).Order;
    Check(o.Paid == 51 + 40, "only the rest of the ice is bought: " + o.Paid);
    Check(o.Items[Ice] == 100 * U && o.Provided == 60 * U, "the order holds all of it");
    Check(m.Stock(A, Ice) == 99_960 * U, "stock only for what was bought");
    var r = Ok(w, "cancel", () => b.Cancel(w, market, s, A, P1));
    Check(
        w.Balances[P1] == 25_000 + 58,
        "paid back and the ship's sold at 0.98: " + w.Balances[P1]
    );
    Check(m.Stock(A, Ice) == 100_060 * U, "the ice is in stock: " + m.Stock(A, Ice));
    Check(r.Message.Contains("149 SC"), r.Message);

    var more = Ok(
        w,
        "a ship has more than needed",
        () =>
            b.Order(
                w,
                market,
                s,
                Paid(P1, fee: 0),
                Needs((Ice, 10 * U)),
                Needs((Ice, 50 * U)),
                long.MaxValue
            )
    ).Order;
    Check(
        more.Paid == 0 && more.Provided == 10 * U && more.Items[Ice] == 10 * U,
        "only what it needs"
    );
}

// Too little ice for sale: the order goes ahead with the ice there is
{
    var (w, m, market, b) = Setup();
    m.SetStock(A, Ice, 30 * U);
    var o = Ok(
        w,
        "short of ice",
        () =>
            b.Order(
                w,
                market,
                s,
                Paid(P1, fee: 0),
                Needs((Plate, U), (Ice, 100 * U)),
                none,
                long.MaxValue
            )
    ).Order;
    Check(o.Items[Ice] == 30 * U && m.Stock(A, Ice) == 0, "the ice there is");
    m.SetStock(B, Ice, 0);
    var o2 = Ok(
        w,
        "no ice",
        () =>
            b.Order(
                w,
                market,
                s,
                Paid(P2, 2, B, 0),
                Needs((Plate, U), (Ice, 100 * U)),
                none,
                long.MaxValue
            )
    ).Order;
    Check(!o2.Items.ContainsKey(Ice), "no ice at all: none held");
}

// Free orders: nothing paid, one at a time anywhere, the queue in order
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
    Check(r.Order.Free && w.Snapshot() == before, "free order: nothing paid");
    Refused(
        w,
        b,
        m,
        "a second free order at another shipyard",
        () => b.Order(w, market, s, new BuildOrder { Station = B, Player = 1 }, none, none, 0),
        "one entry at a time"
    );
    w.Now += 10;
    Ok(
        w,
        "p2 queues",
        () => b.Order(w, market, s, Paid(P2, player: 2), Needs((Plate, 1 * U)), none, long.MaxValue)
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
        () => b.Order(w, market, s, Paid(4, player: 4), Needs((Plate, 1 * U)), none, long.MaxValue),
        "queue is full"
    );
    var queue = b.Queue(A);
    Check(queue.Select(o => o.Player).SequenceEqual(new long[] { 1, 2, 3 }), "queue: in order");
    Check(b.Next(A).Player == 1, "next: the first");
    b.Start(b.Next(A).Id);
    Check(
        b.Next(A) == null && b.Queue(A)[0].Started,
        "queue: the running build first, nothing next"
    );
    Ok(w, "free finish", () => b.Finish(w, market, s, queue[0].Id, none, 0, false));
    Check(b.Next(A).Player == 2, "next: the second after the first ended");
    Ok(w, "free cancel", () => b.Cancel(w, market, s, A, 3));
    Check(b.Queue(A).Count == 1, "free cancel: gone");

    // The saved format keeps everything, blueprints included
    string saved = b.Serialize();
    var loaded = BuildBook.Deserialize(saved);
    Check(loaded.Serialize() == saved, "saved format: round trip");
    var o2 = loaded.Queue(A)[0];
    Check(
        o2.Payer == P2
            && o2.Fee == 100
            && o2.Paid == 51
            && o2.Items[Plate] == U
            && o2.Blueprint == "QUJD",
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
    bool old = false;
    try
    {
        BuildBook.Deserialize("octahedron-builds 1\nN\t0\n");
    }
    catch (FormatException)
    {
        old = true;
    }
    Check(old, "saved format: the ledgers' format is refused");
}

// A blueprint over a plugin message's 64 KiB goes up and down in parts, as Builds does (SE-805)
{
    const int MaxPayload = 64 * 1024;
    var (w, m, market, b) = Setup();
    // The size of the ship "Polaris Mk2 RC8 Recolored", and one ending on a part's boundary
    foreach (int size in new[] { 212_920, 2 * BuildBook.BlueprintPartSize })
    {
        var blueprint = new string('Q', size - 1) + "z";
        var parts = BuildBook.BlueprintParts(blueprint);
        Check(string.Concat(parts) == blueprint, $"parts of {size}: all of it");
        string id = "up" + size;
        bool fit = true;
        for (int i = 0; i < parts.Count; i++)
        {
            var upload = new BuildRequest
            {
                Verb = BuildVerb.Upload,
                Id = id,
                Part = i,
                Blueprint = parts[i],
            };
            fit &= Encoding.UTF8.GetByteCount(upload.Encode()) <= MaxPayload;
            string uploaded = BuildRequest.Decode(upload.Encode()).Run(b, w, market, s);
            Check(uploaded == "1", $"parts of {size}: part {i} kept");
        }
        var order = new BuildRequest
        {
            Verb = BuildVerb.Order,
            Station = A,
            Player = size,
            Ship = "Big",
            Id = id,
            Part = parts.Count,
        };
        fit &= Encoding.UTF8.GetByteCount(order.Encode()) <= MaxPayload;
        string answer = BuildRequest.Decode(order.Encode()).Run(b, w, market, s);
        Check(answer.StartsWith("1\t"), $"parts of {size}: ordered: " + answer);
        string orderId = b.Orders.First(o => o.Player == size).Id;
        var got = new StringBuilder();
        for (int i = 0; ; i++)
        {
            string down = new BuildRequest
            {
                Verb = BuildVerb.Download,
                Id = orderId,
                Part = i,
            }.Run(b, w, market, s);
            fit &= Encoding.UTF8.GetByteCount(down) <= MaxPayload;
            Check(down.StartsWith("1\t"), $"parts of {size}: part {i} read");
            got.Append(down, 2, down.Length - 2);
            if (down.Length - 2 < BuildBook.BlueprintPartSize)
                break;
        }
        Check(got.ToString() == blueprint, $"parts of {size}: the order has the blueprint");
        Check(fit, $"parts of {size}: every message fits");
        Check(
            BuildBook.Deserialize(b.Serialize()).Get(orderId).Blueprint == blueprint,
            $"parts of {size}: the blueprint is saved"
        );
    }
    Check(BuildBook.BlueprintParts("").Single() == "", "parts: an empty blueprint is one part");
    Check(
        new BuildRequest { Verb = BuildVerb.Download, Id = "nothing" }.Run(b, w, market, s) == "0",
        "parts: no blueprint without the order"
    );
    Check(!b.Upload(w.Now, "gap", 1, "x"), "parts: a part after a gap refused");
    Check(b.Upload(w.Now, "short", 0, "x"), "parts: a first part kept");
    string missing = new BuildRequest
    {
        Verb = BuildVerb.Order,
        Station = B,
        Player = 9,
        Id = "short",
        Part = 2,
    }.Run(b, w, market, s);
    Check(
        missing.StartsWith("0\t") && !b.Orders.Any(o => o.Player == 9),
        "parts: no order from an unfinished upload: " + missing
    );
    b.Upload(w.Now, "again", 0, "x");
    b.Upload(w.Now, "again", 0, "y");
    b.Upload(w.Now, "again", 1, "z");
    Check(b.TakeUpload("again", 2) == "yz", "parts: a repeated part replaces itself");
    Check(b.TakeUpload("again", 2) == null, "parts: an upload is taken once");
    b.Upload(w.Now, "old", 0, "x");
    b.Upload(w.Now + 601, "new", 0, "x");
    Check(b.TakeUpload("old", 1) == null, "parts: an abandoned upload is dropped");
}

// The requests a node sends, and the book answering them
{
    var (w, m, market, b) = Setup();
    var request = new BuildRequest
    {
        Verb = BuildVerb.Order,
        Station = A,
        Player = 1,
        Payer = P1,
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
    Check(BuildBook.DecodeOrder(next).Blueprint == "", "request: next without its blueprint");
    Check(
        new BuildRequest { Verb = BuildVerb.Download, Id = BuildBook.DecodeOrder(next).Id }.Run(
            b,
            w,
            market,
            s
        ) == "1\tQUJD",
        "request: the blueprint of next"
    );
    var exempt = new BuildRequest
    {
        Verb = BuildVerb.Order,
        Station = B,
        Player = 2,
        Ship = "Big",
        Exempt = true,
    };
    var exemptBack = BuildRequest.Decode(exempt.Encode());
    Check(exemptBack.Exempt && !back.Exempt, "request: exempt on the wire");
    // On a book of its own, the checks below count this one's money
    var (w2, _, market2, b2) = Setup();
    exemptBack.Run(b2, w2, market2, s);
    var exemptOrder = BuildBook.DecodeOrder(
        new BuildRequest { Verb = BuildVerb.Next, Station = B }.Run(b2, w2, market2, s)
    );
    Check(exemptOrder.Exempt && exemptOrder.Free, "request: the exempt order keeps it");
    var started = BuildBook.DecodeOrder(
        BuildBook.EncodeOrder(
            new BuildOrder
            {
                Id = "x",
                Started = true,
                Exempt = true,
            },
            blueprint: false
        )
    );
    Check(started.Started && started.Exempt, "wire format: started and exempt");
    var repairSlot = BuildBook.DecodeOrder(
        BuildBook.EncodeOrder(
            new BuildOrder
            {
                Id = "b1",
                Station = A,
                Player = 1,
                Ship = "Repair",
                Started = true,
                Repair = true,
            },
            blueprint: false
        )
    );
    Check(repairSlot.Repair && repairSlot.Started, "wire format: a started repair slot");
    Check(
        BuildRequest
            .Decode(new BuildRequest { Verb = BuildVerb.Order, Repair = true }.Encode())
            .Repair,
        "request: repair on the wire"
    );
    var slots = new BuildBook();
    var slotWorld = new FakeWorld();
    BuildResult Slot(long player, string station = A) =>
        slots.Order(
            slotWorld,
            null,
            s,
            new BuildOrder
            {
                Station = station,
                Player = player,
                Ship = "Repair",
                Repair = true,
            },
            new Dictionary<string, long>(),
            new Dictionary<string, long>(),
            0
        );
    Check(Slot(1).Ok, "repair: a slot is booked");
    Check(!Slot(1).Ok, "repair: one entry a player at a station");
    Check(!Slot(1, B).Ok, "repair: nor at another station");
    Check(
        !slots
            .Order(
                slotWorld,
                null,
                s,
                new BuildOrder
                {
                    Station = B,
                    Player = 1,
                    Ship = "Free",
                },
                new Dictionary<string, long>(),
                new Dictionary<string, long>(),
                0
            )
            .Ok,
        "repair: nor a ship next to the slot"
    );
    Check(Slot(2).Ok && Slot(3).Ok && !Slot(4).Ok, "repair: slots count in the queue's limit");
    var booked = slots.Queue(A)[0];
    Check(slots.Start(booked.Id).Ok, "repair: it starts");
    Check(slots.Next(A) == null, "repair: nothing next while it runs");
    Check(
        !slots.Cancel(slotWorld, null, s, A, 1).Ok,
        "repair: the book doesn't cancel a running one"
    );
    Check(
        slots.Finish(slotWorld, null, s, booked.Id, new Dictionary<string, long>(), 0, false).Ok
            && slots.Queue(A).Count == 2,
        "repair: it finishes"
    );
    Check(slots.Cancel(slotWorld, null, s, A, 2).Ok, "repair: a waiting one is canceled");
    Check(
        BuildBook.Deserialize(slots.Serialize()).Queue(A).Single().Repair,
        "repair: saved with the book"
    );
    Check(
        !BuildBook
            .DecodeOrder(BuildBook.EncodeOrder(new BuildOrder { Started = true }, false))
            .Exempt,
        "wire format: started, not exempt"
    );
    string quote = new BuildRequest
    {
        Verb = BuildVerb.Quote,
        Station = A,
        Payer = P1,
        Needed = Needs((Plate, 3 * U)),
    }.Run(b, w, market, s);
    Check(BuildQuote.Decode(quote).Lines[0].Cost == 153, "request: a quote");
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

    public MarketFill PlanBuy(string station, string item, long raw) =>
        m.PlanBuy(w, s, station, item, raw);

    public MarketResult Buy(string station, long buyer, string item, long raw) =>
        item == FailOn ? MarketResult.No("Sold out.") : m.Buy(w, s, station, buyer, item, raw);

    public void Return(string station, string item, long raw) => m.Return(w, s, station, item, raw);

    public long SellOff(string station, long seller, IReadOnlyDictionary<string, long> items) =>
        m.SellOff(w, s, station, seller, items);
}

sealed class FakeWorld : IMarketWorld
{
    public const long OctAccount = 1000;
    private static readonly Dictionary<string, (double Kg, bool Integral, long Price)> Items = new()
    {
        ["Component/SteelPlate"] = (20, true, 50_000),
        ["Component/Motor"] = (24, true, 100_000),
        ["Ore/Ice"] = (1, false, 1_000),
        ["Ingot/Gold"] = (1, false, 0),
    };

    public long Now { get; set; } = 1_800_000_000;
    public long Oct => OctAccount;
    public readonly Dictionary<long, long> Balances = new();
    public long Start;

    public FakeWorld()
    {
        foreach (var id in new long[] { 1, 2, 3, 4 })
            Balances[id] = 25_000;
        Balances[OctAccount] = 20_000_000;
        Start = Total();
    }

    public long Total() => Balances.Values.Sum();

    public string Snapshot() =>
        string.Join(",", Balances.OrderBy(b => b.Key).Select(b => $"{b.Key}={b.Value}"));

    public bool Item(string item, out double massKg, out bool integral)
    {
        bool known = Items.TryGetValue(item, out var info);
        massKg = info.Kg;
        integral = info.Integral;
        return known;
    }

    public long Reference(string station, string item) =>
        Items.TryGetValue(item, out var info) ? info.Price : 0;

    public long BasePrice(string item) => Reference("", item);

    public IEnumerable<string> Catalog(string station) =>
        Items.Where(i => i.Value.Price > 0).Select(i => i.Key);

    public bool Specialty(string station, string item) => false;

    public long Balance(long account) => Balances.TryGetValue(account, out long b) ? b : -1;

    public bool ChangeBalance(long account, long amount)
    {
        if (!Balances.TryGetValue(account, out long b) || b + amount < 0)
            return false;
        Balances[account] = b + amount;
        return true;
    }
}
