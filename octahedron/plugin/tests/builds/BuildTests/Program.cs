using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ServerPlugin;

// Checks of the shipyard queues and turns (ServerPlugin/Builds/BuildBook.cs) against a fake world
// with the real market book: the labor fee, quotes from the Store, paid orders and their turns, the
// purchase at the start all or none, hydrogen, the sale of what a turn leaves, towed grids and what
// doesn't fit, free orders, the queue, the saved and wire formats, and that no money is made or
// lost. Prints one line per failed check and a summary; exit code 0 = all pass.
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

// A bot's finished ship: the bot always hears where it is, unless the ship is gone
{
    var flags = new[] { false, true };
    foreach (var closed in flags)
    foreach (var storable in flags)
    foreach (var waiting in flags)
    {
        Check(
            ShipyardPlacement.BotShipEnd(closed, false, storable, waiting) == BotShip.Ready,
            $"bot ship: the yard is no longer simulated here, ready (closed={closed}, storable={storable}, waiting={waiting})"
        );
        Check(
            ShipyardPlacement.BotShipEnd(closed, true, storable, waiting)
                == (
                    closed ? BotShip.Gone
                    : storable && waiting ? BotShip.Hangar
                    : BotShip.Ready
                ),
            $"bot ship: yard here, gone, Hangar or ready (closed={closed}, storable={storable}, waiting={waiting})"
        );
    }
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

// A paid order costs nothing while it waits, and cancels free of charge
{
    var (w, m, market, b) = Setup();
    var needed = Needs((Plate, 100 * U), (Motor, 2 * U), (Ice, 500 * U));
    string before = w.Snapshot() + m.Serialize();
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1), needed)).Order;
    Check(order.Id == "b1" && b.Get("b1") == order, "order: id b1");
    Check(order.Ship == "Test Ship", "order: the ship's name has no tab");
    Check(order.Phase == BuildPhase.Waiting && !order.Active, "order: it waits");
    Check(w.Snapshot() + m.Serialize() == before, "order: nothing paid or bought");
    Check(order.Items[Plate] == 100 * U && order.Items.Count == 3, "order: the bill");
    Refused(
        w,
        b,
        m,
        "a second order at the same shipyard",
        () => b.Order(w, s, Paid(P1), needed),
        "one entry at a time"
    );
    Refused(w, b, m, "p2 has nothing to cancel", () => b.Cancel(A, P2), "no ship");
    Ok(w, "cancel", () => b.Cancel(A, P1));
    Check(b.Get("b1") == null && w.Snapshot() + m.Serialize() == before, "cancel: nothing moved");
    Check(Ok(w, "again", () => b.Order(w, s, Paid(P1), needed)).Order.Id == "b2", "ids not reused");
    Refused(
        w,
        b,
        m,
        "an unknown station",
        () => b.Order(w, s, Paid(P2, player: 2, station: ""), needed),
        "Unknown station"
    );
}

// The turn: the window to load, the press buys what is missing, the window to take what is left
{
    var (w, m, market, b) = Setup();
    var bill = Needs((Plate, 150 * U), (Motor, 2 * U), (Ice, 500 * U));
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1), bill)).Order;
    Check(b.Next(A) == order && b.Next(B) == null, "next: the order waits at Alpha");
    Refused(
        w,
        b,
        m,
        "start before the turn",
        () => b.Begin(w, market, s, order.Id, P1, none, none),
        "can't start"
    );
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    Check(order.Phase == BuildPhase.Loading && order.Active, "turn: loading");
    Check(order.TurnEnds == w.Now + 600, "turn: 10 minutes by default");
    Check(b.Next(A) == order, "next: still it, for its blueprint at the press");
    Refused(w, b, m, "turn twice", () => b.Turn(w, s, order.Id), "has its turn");
    Refused(w, b, m, "cancel in a turn", () => b.Cancel(A, P1), "can't be canceled");
    Refused(
        w,
        b,
        m,
        "another player presses start",
        () => b.Begin(w, market, s, order.Id, P2, none, none),
        "Only the player"
    );

    // The player loaded 50 plates: 100 are missing
    var lacking = Needs((Plate, 100 * U), (Motor, 2 * U), (Ice, 500 * U));
    var q = BuildBook.Quote(w, market, A, P1, lacking, 100);
    Check(q.Total == 5_100 + 204 + 500 + 100, $"start quote {q.Total}");
    var r = Ok(w, "start", () => b.Begin(w, market, s, order.Id, P1, lacking, none));
    Check(
        r.Message.EndsWith("starts: 5,804 SC for what was missing and 100 SC labor paid."),
        r.Message
    );
    Check(order.Started && order.Phase == BuildPhase.Building, "start: building");
    Check(b.Next(A) == null, "start: nothing next");
    Check(w.Balances[P1] == 25_000 - q.Total, "start: the payer paid " + w.Balances[P1]);
    Check(m.Stock(A, Plate) == 900 * U && m.Stock(B, Plate) == 1000 * U, "start: the stock");
    Check(order.Items[Plate] == 100 * U && order.Items[Ice] == 500 * U, "start: what came");
    Refused(
        w,
        b,
        m,
        "start twice",
        () => b.Begin(w, market, s, order.Id, P1, lacking, none),
        "can't start"
    );

    // The build ends: the ice not used is sold back, the player has a while to take the rest
    long paid = w.Balances[P1];
    var after = Ok(w, "after", () => b.After(w, market, s, order.Id, 300 * U, false, none));
    Check(after.Message == "The ice the ship didn't take was sold for 196 SC.", after.Message);
    Check(w.Balances[P1] == paid + 196, "after: at 0.98");
    Check(m.Stock(A, Ice) == 99_700 * U, "after: the ice is in stock: " + m.Stock(A, Ice));
    Check(order.Phase == BuildPhase.After, "after: its phase");
    Check(order.TurnEnds == w.Now + 600, "after: 10 minutes");
    Check(b.Queue(A)[0] == order, "after: it stays at the front");
    Check(b.Next(A) == null, "after: nothing next");

    // The turn ends: what is left is sold for the player
    paid = w.Balances[P1];
    var end = Ok(w, "end", () => b.EndTurn(w, market, order.Id, Needs((Plate, 10 * U))));
    Check(end.Message == "What was left in the shipyard was sold for 490 SC.", end.Message);
    Check(w.Balances[P1] == paid + 490, "end: at the bid, the tax in it");
    Check(m.Stock(A, Plate) == 910 * U, "end: into the stock");
    Check(b.Get(order.Id) == null && b.Next(A) == null, "end: it left the queue");
}

// A dropped order: what is left is sold, it never paid anything else
{
    var (w, m, market, b) = Setup();
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1), Needs((Plate, U)))).Order;
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    var end = Ok(w, "dropped", () => b.EndTurn(w, market, order.Id, Needs((Plate, U))));
    Check(w.Balances[P1] == 25_000 + 49, "dropped: the sale only: " + end.Message);
    Check(
        Ok(w, "nothing left", () => b.Order(w, s, Paid(P1), none)).Order != null,
        "another order"
    );
    var nothing = Ok(w, "ends empty", () => b.EndTurn(w, market, b.Queue(A)[0].Id, none));
    Check(nothing.Message == "", "nothing left: no message: " + nothing.Message);
}

// The start can't be paid for or isn't for sale: nothing moves, the player can load it instead
{
    var (w, m, market, b) = Setup();
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1), none)).Order;
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    Refused(
        w,
        b,
        m,
        "more than the Store has",
        () => b.Begin(w, market, s, order.Id, P1, Needs((Motor, 101 * U)), none),
        "load them into the containers"
    );
    w.Balances[P1] = 100;
    w.Start = w.Total();
    Refused(
        w,
        b,
        m,
        "too poor",
        () => b.Begin(w, market, s, order.Id, P1, Needs((Plate, 10 * U)), none),
        "load more into the containers"
    );
    w.Balances[P1] = 25_000;
    w.Start = w.Total();
    market.FailOn = Plate;
    var failed = b.Begin(w, market, s, order.Id, P1, Needs((Motor, U), (Plate, U)), none);
    market.FailOn = null;
    Check(!failed.Ok && failed.Message.Contains("Buying"), "half way: " + failed.Message);
    Check(w.Total() == w.Start && w.Balances[P1] == 25_000, "the part bought is paid back");
    Check(m.Stock(A, Motor) == 100 * U, "the part bought is back in stock");
    Check(order.Phase == BuildPhase.Loading, "still loading");

    var (w2, m2, market2, b2) = Setup();
    var poor = Ok(w2, "no account", () => b2.Order(w2, s, Paid(99, 99), none)).Order;
    Ok(w2, "turn", () => b2.Turn(w2, s, poor.Id));
    Refused(
        w2,
        b2,
        m2,
        "no account",
        () => b2.Begin(w2, market2, s, poor.Id, 99, Needs((Plate, U)), none),
        "No bank account"
    );
    Check(!b.Start("nope").Ok, "start nothing");
    Check(!b.Finish("nope").Ok, "finish nothing");
    Check(!b.After(w, market, s, "nope", 0, false, none).Ok, "after nothing");
    Check(!b.EndTurn(w, market, "nope", none).Ok, "end nothing");
}

// A build that never got going gives the fee back
{
    var (w, m, market, b) = Setup();
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1), none)).Order;
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    Ok(w, "start", () => b.Begin(w, market, s, order.Id, P1, none, none));
    Check(w.Balances[P1] == 25_000 - 100, "the fee is paid");
    var r = Ok(w, "refund", () => b.After(w, market, s, order.Id, 0, true, none));
    Check(w.Balances[P1] == 25_000, "the fee is back");
    Check(r.Message == "The labor fee of 100 SC is paid back.", r.Message);
}

// A ship left in the build space: what it cost comes back once, unless it left
{
    var (w, m, market, b) = Setup();
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1), none)).Order;
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    var plates = Needs((Plate, 10 * U));
    Ok(w, "start", () => b.Begin(w, market, s, order.Id, P1, plates, none));
    Check(order.Paid == 510 + 100, "the order knows what it took: " + order.Paid);
    Ok(w, "after", () => b.After(w, market, s, order.Id, 0, false, none, ship: 7));
    var ship = b.Ships.Single();
    Check(ship.Refund == 610 && ship.Player == P1 && ship.Station == A, "the ship is known");
    var again = BuildBook.Deserialize(b.Serialize());
    Check(again.Ships.Single().Refund == 610, "it is saved");
    Check(!again.ShipOut(w, 8, true).Ok, "no other ship is paid for");
    var paid = Ok(w, "refund", () => again.ShipOut(w, 7, true));
    Check(paid.Message == "The 610 SC you paid is paid back.", paid.Message);
    Check(w.Balances[P1] == 25_000, "all of it is back");
    Check(!again.ShipOut(w, 7, true).Ok && w.Balances[P1] == 25_000, "only once");
    Check(!again.Ships.Any(), "and it is forgotten");

    Ok(w, "left", () => b.ShipOut(w, 7, false));
    Check(!b.Ships.Any() && w.Balances[P1] == 25_000, "a ship that left costs nothing");
    Check(!b.ShipOut(w, 7, true).Ok, "and nothing comes back for it later");

    var queue = new BuildRequest { Verb = BuildVerb.Queue, Station = A };
    b.Finish(order.Id, 9);
    var lines = queue.Run(b, w, market, s).Split('\n');
    Check(BuiltShip.Decode(lines.Single()).Refund == 0, "a free one has nothing to refund");
    Ok(w, "removed", () => b.ShipOut(w, 9, true));
    Check(w.Balances[P1] == 25_000, "and gets nothing");
}

// An unfinished ship is worth its share; the ice sold back and a removed ship don't count
{
    var (w, m, market, b) = Setup();
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1, fee: 0), none)).Order;
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    var needed = Needs((Plate, 10 * U));
    Ok(w, "start", () => b.Begin(w, market, s, order.Id, P1, needed, none));
    Ok(w, "after", () => b.After(w, market, s, order.Id, 0, false, none, ship: 7, percent: 40));
    Check(b.Ships.Single().Refund == 510 * 40 / 100, "a share: " + b.Ships.Single().Refund);

    var (w2, m2, market2, b2) = Setup();
    var iced = Ok(w2, "order", () => b2.Order(w2, s, Paid(P1, fee: 0), none)).Order;
    Ok(w2, "turn", () => b2.Turn(w2, s, iced.Id));
    Ok(w2, "start", () => b2.Begin(w2, market2, s, iced.Id, P1, Needs((Ice, 100 * U)), none));
    var before = w2.Balances[P1];
    Ok(w2, "after", () => b2.After(w2, market2, s, iced.Id, 0, false, none, ship: 7));
    var sold = w2.Balances[P1] - before;
    Check(sold > 0 && b2.Ships.Single().Refund == iced.Paid - sold, "less the ice sold back");

    var (w3, m3, market3, b3) = Setup();
    var gone = Ok(w3, "order", () => b3.Order(w3, s, Paid(P1), none)).Order;
    Ok(w3, "turn", () => b3.Turn(w3, s, gone.Id));
    Ok(w3, "start", () => b3.Begin(w3, market3, s, gone.Id, P1, none, none));
    Ok(w3, "after", () => b3.After(w3, market3, s, gone.Id, 0, true, none));
    Check(!b3.Ships.Any(), "no ship, no entry");
}

// A saved book of before Paid and built ships still loads
{
    var old =
        "octahedron-builds 3\nN\t5\n"
        + "O\tb5\tAlpha Station\t1\t1\t10\t100\tOld\t1000\t0\t\t1\t2000\t0\tComponent/SteelPlate\t5\n";
    var order = BuildBook.Deserialize(old).Get("b5");
    Check(order.Paid == 0 && order.Items["Component/SteelPlate"] == 5, "an old line");
    var round = BuildBook.DecodeOrder(BuildBook.EncodeOrder(order, false));
    Check(round.Items["Component/SteelPlate"] == 5, "with Paid and an item");
    var request = BuildRequest.Decode(
        new BuildRequest
        {
            Verb = BuildVerb.After,
            Grid = 7,
            Percent = 40,
        }.Encode()
    );
    Check(request.Grid == 7 && request.Percent == 40, "the wire format has the ship");
}

// Hydrogen from outside: not bought; what the ship doesn't take is sold for the player
{
    var (w, m, market, b) = Setup();
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1, fee: 0), none)).Order;
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    var needed = Needs((Plate, U), (Ice, 100 * U));
    var begun = Ok(
        w,
        "a ship's hydrogen",
        () => b.Begin(w, market, s, order.Id, P1, needed, Needs((Ice, 60 * U)))
    );
    Check(begun.Message.Contains("91 SC"), "only the rest of the ice is bought: " + begun.Message);
    Check(order.Items[Ice] == 100 * U, "the order holds all of it");
    Check(m.Stock(A, Ice) == 99_960 * U, "stock only for what was bought");
    long paid = w.Balances[P1];
    Ok(w, "after", () => b.After(w, market, s, order.Id, 70 * U, false, none));
    Check(
        w.Balances[P1] == paid + 29,
        "the 30 not taken are sold at 0.98: " + (w.Balances[P1] - paid)
    );
}

// Too little ice for sale: the build goes ahead with the ice there is
{
    var (w, m, market, b) = Setup();
    m.SetStock(A, Ice, 30 * U);
    var order = Ok(w, "order", () => b.Order(w, s, Paid(P1, fee: 0), none)).Order;
    Ok(w, "turn", () => b.Turn(w, s, order.Id));
    Ok(
        w,
        "short of ice",
        () => b.Begin(w, market, s, order.Id, P1, Needs((Plate, U), (Ice, 100 * U)), none)
    );
    Check(order.Items[Ice] == 30 * U && m.Stock(A, Ice) == 0, "the ice there is");
    m.SetStock(B, Ice, 0);
    var other = Ok(w, "order", () => b.Order(w, s, Paid(P2, 2, B, 0), none)).Order;
    Ok(w, "turn", () => b.Turn(w, s, other.Id));
    Ok(
        w,
        "no ice",
        () => b.Begin(w, market, s, other.Id, P2, Needs((Plate, U), (Ice, 100 * U)), none)
    );
    Check(!other.Items.ContainsKey(Ice), "no ice at all: none held");
}

// After a build the player's next paid order goes on with the turn, keeping the containers
{
    var (w, m, market, b) = Setup();
    var first = Ok(w, "first", () => b.Order(w, s, Paid(P1), none)).Order;
    b.Turn(w, s, first.Id);
    b.Begin(w, market, s, first.Id, P1, none, none);
    w.Now += 10;
    Ok(w, "p2 waits", () => b.Order(w, s, Paid(P2, player: 2), none));
    b.After(w, market, s, first.Id, 0, false, none);
    w.Now += 100;
    Refused(
        w,
        b,
        m,
        "a free order in the turn",
        () => b.Order(w, s, new BuildOrder { Station = A, Player = 1 }, none),
        "end it first"
    );
    var next = Ok(w, "next", () => b.Order(w, s, Paid(P1), Needs((Motor, 1 * U)))).Order;
    Check(next == first && next.Phase == BuildPhase.Loading, "next: the same turn, loading");
    Check(next.TurnEnds == w.Now + 600 && next.Items[Motor] == U, "next: a new window, a new bill");
    Check(b.Queue(A)[0] == first, "next: still at the front");
    var other = Ok(w, "in the window", () => b.Order(w, s, Paid(P1), Needs((Plate, 1 * U)))).Order;
    Check(other == first && other.Items.ContainsKey(Plate), "next: the loading window too");
}

// A towed grid: it takes the free shipyard at once, or there is no order and it is sold
{
    var (w, m, market, b) = Setup();
    BuildOrder Tow(long grid, long player = 1) =>
        new()
        {
            Station = A,
            Player = player,
            Grid = grid,
            Ship = "Wreck",
            Pcu = 70_000,
        };
    var order = Ok(w, "p2 orders", () => b.Order(w, s, Paid(P2, player: 2), none)).Order;
    string before = w.Snapshot();
    Check(Ok(w, "busy", () => b.Tow(w, Tow(776))).Order == null, "tow: busy, no order");
    Check(b.Queue(A).Count == 1, "tow: a busy shipyard's queue stays");
    Check(w.Snapshot() == before, "tow: nothing moved");
    long had = w.Balances[P1];
    var sale = Ok(w, "sold at once", () => BuildBook.Sale(market, A, 1, Needs((Plate, 100 * U))));
    Check(w.Balances[P1] > had, "tow: the sale pays the player " + sale.Message);
    b.Cancel(A, 2);

    var tow = Ok(w, "tow into a free shipyard", () => b.Tow(w, Tow(777))).Order;
    Check(!tow.Free && tow.Grid == 777 && tow.Payer == 0, "tow: not a free build, nobody pays");
    Check(tow.Started && b.Next(A) == null, "tow: running at once");
    Check(Ok(w, "tow twice", () => b.Tow(w, Tow(777))).Order == tow, "tow: once in the queue");
    Check(b.Queue(A).Count(o => o.Grid == 777) == 1, "tow: one entry");
    Refused(w, b, m, "a tow can't be canceled", () => b.Cancel(A, 1), "can't be canceled");
    Check(BuildBook.DecodeOrder(BuildBook.EncodeOrder(tow, false)).Grid == 777, "tow: on the wire");
    long bal = w.Balances[P1];
    var after = Ok(
        w,
        "breakdown done",
        () => b.After(w, market, s, tow.Id, 0, false, Needs((Plate, 100 * U)))
    );
    Check(after.Message.Contains("did not fit"), "breakdown: tells " + after.Message);
    Check(tow.Phase == BuildPhase.After && tow.TurnEnds == w.Now + 600, "breakdown: 10 minutes");
    Check(w.Balances[P1] == bal + 4_900, "breakdown: the overflow is sold at once");
    Ok(w, "end", () => b.EndTurn(w, market, tow.Id, none));
    Check(b.Queue(A).Count == 0, "breakdown: the queue is free");
}

// What doesn't fit: the cheapest units are sold first, whole units of whole items
{
    BreakdownItem Item(string name, long units, double volume, long price, bool whole = true) =>
        new(name, units * U, units * volume, price, whole);
    var items = new[]
    {
        Item("Gold", 10, 1, 500),
        Item("Plate", 100, 1, 50),
        Item("Motor", 10, 2, 100),
        Item("Ice", 1000, 0.001, 1, whole: false),
    };
    Check(BuildBook.Overflow(items, 1000).Count == 0, "overflow: all fits");
    var sold = BuildBook.Overflow(items, 131);
    Check(sold.Count == 0, "overflow: exactly fits " + string.Join(",", sold));
    sold = BuildBook.Overflow(items, 130.5);
    Check(
        sold.Count == 1 && sold["Ice"] == 500 * U,
        "overflow: the cheapest first, a part of it: " + string.Join(",", sold)
    );
    sold = BuildBook.Overflow(items, 120);
    Check(
        sold["Ice"] == 1000 * U && sold["Plate"] == 10 * U && !sold.ContainsKey("Motor"),
        "overflow: then the next: " + string.Join(",", sold)
    );
    sold = BuildBook.Overflow(items, 119.5);
    Check(sold["Plate"] == 11 * U, "overflow: whole units, rounded up");
    sold = BuildBook.Overflow(items, 11);
    Check(
        sold["Plate"] == 100 * U && sold["Motor"] == 10 * U && !sold.ContainsKey("Gold"),
        "overflow: the dearest stays: " + string.Join(",", sold)
    );
    sold = BuildBook.Overflow(items, 5);
    Check(sold["Gold"] == 5 * U, "overflow: the dearest last: " + string.Join(",", sold));
    sold = BuildBook.Overflow(items, 0);
    Check(sold.Values.Sum() == (1000 + 100 + 10 + 10) * U, "overflow: no room, all sold");
    Check(BuildBook.Overflow([], 0).Count == 0, "overflow: nothing to sell");
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
    var r = Ok(w, "free order", () => b.Order(w, s, free, none));
    Check(r.Order.Free && w.Snapshot() == before, "free order: nothing paid");
    Refused(
        w,
        b,
        m,
        "a second free order at another shipyard",
        () => b.Order(w, s, new BuildOrder { Station = B, Player = 1 }, none),
        "one entry at a time"
    );
    w.Now += 10;
    Ok(w, "p2 queues", () => b.Order(w, s, Paid(P2, player: 2), Needs((Plate, 1 * U))));
    w.Now += 10;
    Ok(w, "p3 queues", () => b.Order(w, s, new BuildOrder { Station = A, Player = 3 }, none));
    Refused(
        w,
        b,
        m,
        "the queue is full",
        () => b.Order(w, s, Paid(4, player: 4), Needs((Plate, 1 * U))),
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
    Ok(w, "free finish", () => b.Finish(queue[0].Id));
    Check(b.Next(A).Player == 2, "next: the second after the first ended");
    Ok(w, "free cancel", () => b.Cancel(A, 3));
    Check(b.Queue(A).Count == 1, "free cancel: gone");

    // The saved format keeps everything, blueprints included
    string saved = b.Serialize();
    var loaded = BuildBook.Deserialize(saved);
    Check(loaded.Serialize() == saved, "saved format: round trip");
    var o2 = loaded.Queue(A)[0];
    Check(
        o2.Payer == P2 && o2.Fee == 100 && o2.Items[Plate] == U && o2.Blueprint == "QUJD",
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
        () => loaded.Order(w, s, new BuildOrder { Station = B, Player = 3 }, none)
    );
    Check(next.Order.Id == "b4", "saved format: the next id " + next.Order.Id);
    bool old = false;
    try
    {
        BuildBook.Deserialize("octahedron-builds 2\nN\t0\n");
    }
    catch (FormatException)
    {
        old = true;
    }
    Check(old, "saved format: the format before the turns is refused");
}

// Human players' orders before the bots' (SE-1207): ahead of the bots' waiting orders; a bot's
// build or turn, and the last bot order of a full queue, bumped; a queue of humans full
{
    var (w, m, market, b) = Setup();
    BuildOrder Free(long player, bool bot = false) =>
        new()
        {
            Station = A,
            Player = player,
            Ship = (bot ? "Bot" : "Human") + player,
            Blueprint = "QQ==",
            Bot = bot,
        };
    long[] Players() => b.Queue(A).Select(o => o.Player).ToArray();
    long[] BumpedOnes() => b.Queue(A).Where(o => o.Bumped).Select(o => o.Player).ToArray();

    var bot11 = Ok(w, "bot 11 orders", () => b.Order(w, s, Free(11, true), none)).Order;
    w.Now += 10;
    Ok(w, "human 1 orders", () => b.Order(w, s, Free(1), none));
    Check(Players().SequenceEqual(new long[] { 1, 11 }), "priority: the human first");
    Check(b.Next(A).Player == 1 && BumpedOnes().Length == 0, "priority: next, nothing bumped");
    Ok(w, "human 1 cancels", () => b.Cancel(A, 1));

    // A bot's build in progress is bumped by a human's order, also with room in the queue
    b.Start(bot11.Id);
    w.Now += 10;
    Ok(w, "bot 12 orders", () => b.Order(w, s, Free(12, true), none));
    Check(BumpedOnes().Length == 0, "a bot's order bumps nothing");
    w.Now += 10;
    Ok(w, "human 2 orders", () => b.Order(w, s, Free(2), none));
    Check(BumpedOnes().SequenceEqual(new long[] { 11 }), "the bot's build is bumped");
    Check(Players().SequenceEqual(new long[] { 11, 2, 12 }), "the bumped build stays first");
    Check(b.Next(A) == null, "nothing next while the bumped build ends");
    Ok(w, "the bumped build ends", () => b.Finish(bot11.Id));
    Check(b.Next(A).Player == 2, "then the human's turn");

    // A full queue (3): the last bot order waiting makes way for a human's
    var human2 = b.Next(A);
    b.Start(human2.Id);
    w.Now += 10;
    Ok(w, "bot 13 orders", () => b.Order(w, s, Free(13, true), none));
    Check(Players().SequenceEqual(new long[] { 2, 12, 13 }), "full: human building, two bots");
    Refused(
        w,
        b,
        m,
        "a bot's order in a full queue",
        () => b.Order(w, s, Free(14, true), none),
        "queue is full"
    );
    w.Now += 10;
    Ok(w, "human 3 orders into the full queue", () => b.Order(w, s, Free(3), none));
    Check(BumpedOnes().SequenceEqual(new long[] { 13 }), "full: the last bot order is bumped");
    Check(Players().SequenceEqual(new long[] { 2, 3, 12, 13 }), "full: the new human 2nd");
    w.Now += 10;
    Ok(w, "human 4 orders into the full queue", () => b.Order(w, s, Free(4), none));
    Check(BumpedOnes().SequenceEqual(new long[] { 12, 13 }), "full: the next bot is bumped");
    Refused(w, b, m, "a full queue of humans", () => b.Order(w, s, Free(5), none), "queue is full");
    Refused(
        w,
        b,
        m,
        "the bumped bot orders again before its order left",
        () => b.Order(w, s, Free(12, true), none),
        "for you already"
    );

    // A bot's paid turn before the start is bumped; its build's After isn't
    var (w2, m2, market2, b2) = Setup();
    var paidBot = Paid(3, player: 3);
    paidBot.Bot = true;
    Ok(w2, "a bot's paid order", () => b2.Order(w2, s, paidBot, Needs((Plate, 1 * U))));
    b2.Turn(w2, s, paidBot.Id);
    Ok(w2, "a human's order", () => b2.Order(w2, s, Paid(P1), Needs((Plate, 1 * U))));
    Check(paidBot.Bumped, "a bot's turn before the start is bumped");
    paidBot.Bumped = false;
    paidBot.Phase = BuildPhase.After;
    Ok(w2, "human 1 cancels", () => b2.Cancel(A, 1));
    Ok(w2, "a human's order again", () => b2.Order(w2, s, Paid(P1), Needs((Plate, 1 * U))));
    Check(!paidBot.Bumped, "a bot's turn after its build stays");

    // Saved and on the wire; a book and a request from before the bots load as human players'
    string saved = b.Serialize();
    var loaded = BuildBook.Deserialize(saved);
    Check(loaded.Serialize() == saved, "bots: saved format round trip");
    var bumped = loaded.Queue(A).Last();
    Check(bumped.Bot && bumped.Bumped && bumped.Player == 13, "bots: the flags saved");
    var wire = BuildBook.DecodeOrder(BuildBook.EncodeOrder(bumped, blueprint: false));
    Check(wire.Bot && wire.Bumped, "bots: the flags on the wire");
    var old = BuildBook
        .Deserialize(
            "octahedron-builds 3\nN\t5\n"
                + "O\tb5\tAlpha Station\t1\t0\t0\t100\tOld\t1000\t0\t\t0\t0\t0\t0\n"
        )
        .Get("b5");
    Check(!old.Bot && !old.Bumped, "bots: an order from before is a human player's");
    var request = new BuildRequest
    {
        Verb = BuildVerb.Order,
        Station = A,
        Player = 21,
        Blueprint = "QQ==",
        Bot = true,
    };
    Check(BuildRequest.Decode(request.Encode()).Bot, "bots: the request carries the flag");
    var lines = request.Encode().Split('\n');
    var before = string.Join("\t", lines[0].Split('\t').Take(16)) + "\n\n";
    Check(!BuildRequest.Decode(before).Bot, "bots: a request from before is a human player's");
    var (w3, _, market3, b3) = Setup();
    Check(request.Run(b3, w3, market3, s).StartsWith("1"), "bots: ordered by request");
    Check(b3.Queue(A).Single().Bot, "bots: the order is a bot's");
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
                Phase = BuildPhase.Building,
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
                Phase = BuildPhase.Building,
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
    // A free repair slot of an admin who ignores PCU limits keeps Exempt on the wire and in the
    // queue other nodes read (SE-1467)
    var (w3, _, market3, b3) = Setup();
    BuildRequest
        .Decode(
            new BuildRequest
            {
                Verb = BuildVerb.Order,
                Station = A,
                Player = 3,
                Ship = "Repair",
                Repair = true,
                Slot = SlotKind.Repair,
                Exempt = true,
            }.Encode()
        )
        .Run(b3, w3, market3, s);
    var freeRepair = BuildBook.DecodeOrder(
        new BuildRequest { Verb = BuildVerb.Next, Station = A }.Run(b3, w3, market3, s)
    );
    Check(
        freeRepair.Repair && freeRepair.Slot == SlotKind.Repair && freeRepair.Exempt,
        "request: a free repair slot keeps Exempt in the queue"
    );
    var slots = new BuildBook();
    var slotWorld = new FakeWorld();
    BuildResult Slot(long player, string station = A) =>
        slots.Order(
            slotWorld,
            s,
            new BuildOrder
            {
                Station = station,
                Player = player,
                Ship = "Repair",
                Repair = true,
            },
            new Dictionary<string, long>()
        );
    Check(Slot(1).Ok, "repair: a slot is booked");
    Check(!Slot(1).Ok, "repair: one entry a player at a station");
    Check(!Slot(1, B).Ok, "repair: nor at another station");
    Check(
        !slots
            .Order(
                slotWorld,
                s,
                new BuildOrder
                {
                    Station = B,
                    Player = 1,
                    Ship = "Free",
                },
                new Dictionary<string, long>()
            )
            .Ok,
        "repair: nor a ship next to the slot"
    );
    Check(Slot(2).Ok && Slot(3).Ok && !Slot(4).Ok, "repair: slots count in the queue's limit");
    var booked = slots.Queue(A)[0];
    Check(slots.Start(booked.Id).Ok, "repair: it starts");
    Check(slots.Next(A) == null, "repair: nothing next while it runs");
    Check(!slots.Cancel(A, 1).Ok, "repair: the book doesn't cancel a running one");
    Check(slots.Finish(booked.Id).Ok && slots.Queue(A).Count == 2, "repair: it finishes");
    Check(slots.Cancel(A, 2).Ok, "repair: a waiting one is canceled");
    Check(
        BuildBook.Deserialize(slots.Serialize()).Queue(A).Single().Repair,
        "repair: saved with the book"
    );
    Check(
        !BuildBook
            .DecodeOrder(
                BuildBook.EncodeOrder(new BuildOrder { Phase = BuildPhase.Building }, false)
            )
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
    string Run(BuildVerb verb) =>
        new BuildRequest
        {
            Verb = verb,
            Id = "b1",
            Station = A,
            Player = 1,
            HydrogenUsed = 6 * U,
            Needed = verb == BuildVerb.Begin ? Needs((Plate, 3 * U), (Ice, 10 * U)) : none,
            Provided = verb == BuildVerb.Begin ? Needs((Ice, 4 * U)) : none,
        }.Run(b, w, market, s);
    Check(Run(BuildVerb.Turn).StartsWith("1"), "request: turn");
    Check(Run(BuildVerb.Begin).StartsWith("1"), "request: begin");
    Check(Run(BuildVerb.After).Contains("\nO\tb1\t"), "request: after with the order");
    Check(Run(BuildVerb.EndTurn).StartsWith("1"), "request: end of the turn");
    Check(BuildRequest.Decode(new BuildRequest { Grid = 7 }.Encode()).Grid == 7, "request: a grid");
    var prices = new BuildRequest
    {
        Verb = BuildVerb.Prices,
        Station = A,
        Needed = Needs((Plate, U)),
    }.Run(b, w, market, s);
    Check(prices == Plate + "\t49000", "request: what the station pays: " + prices);
    Check(
        b.Orders.Count() == 0 && w.Total() == w.Start,
        "request: all done, no money made or lost"
    );
}

// Slot kinds (SE-1386): saved and sent with the queue, and booked like a repair slot
{
    var (w, m, market, b) = Setup();
    foreach (var kind in new[] { SlotKind.Repair, SlotKind.Grind, SlotKind.Hack })
    {
        var wire = BuildBook.DecodeOrder(
            BuildBook.EncodeOrder(
                new BuildOrder
                {
                    Id = "b1",
                    Station = A,
                    Player = 1,
                    Ship = kind.ToString(),
                    Repair = true,
                    Slot = kind,
                },
                blueprint: false
            )
        );
        Check(wire.Repair && wire.Slot == kind, $"slots: {kind} on the wire");
        var request = BuildRequest.Decode(
            new BuildRequest
            {
                Verb = BuildVerb.Order,
                Repair = true,
                Slot = kind,
            }.Encode()
        );
        Check(request.Repair && request.Slot == kind, $"slots: {kind} request on the wire");
    }
    Check(
        BuildBook
            .DecodeOrder(BuildBook.EncodeOrder(new BuildOrder { Slot = SlotKind.Hack }, false))
            .Slot == SlotKind.Repair,
        "slots: a kind without a slot is no slot"
    );
    var old = new BuildRequest { Verb = BuildVerb.Order, Repair = true }.Encode();
    var fields = old.Split('\n');
    fields[0] = string.Join("\t", fields[0].Split('\t').Take(17));
    Check(
        BuildRequest.Decode(string.Join("\n", fields)).Slot == SlotKind.Repair,
        "slots: an older node's request is a repair"
    );
    var answer = new BuildRequest
    {
        Verb = BuildVerb.Order,
        Station = A,
        Player = 1,
        Ship = "Grind",
        Repair = true,
        Slot = SlotKind.Grind,
    }.Run(b, w, market, s);
    Check(
        answer.StartsWith("1") && b.Queue(A).Single().Slot == SlotKind.Grind,
        "slots: a grind slot is booked: " + answer
    );
    Check(
        BuildBook.Deserialize(b.Serialize()).Queue(A).Single().Slot == SlotKind.Grind,
        "slots: saved with the book"
    );
}

// A repair slot buys what its welders lack: as far as the stock and the account go
{
    var (w, m, market, b) = Setup();
    var start = w.Total();
    var stock = m.Stock(A, Motor);
    var answer = new BuildRequest
    {
        Verb = BuildVerb.Buy,
        Station = A,
        Player = P1,
        Needed = Needs((Plate, 10 * U), (Motor, 150 * U)),
    }.Run(b, w, market, s);
    var bought = BuildBook.DecodeOrder(answer.Split('\n')[1]);
    // 25,000 SC: 10 plates for 510, then 240 motors would cost more than the 24,490 left
    Check(bought.Items[Plate] == 10 * U, "buy: the plates");
    Check(
        bought.Items[Motor] > 0 && bought.Items[Motor] <= 100 * U && bought.Items[Motor] % U == 0,
        "buy: whole motors, at most the stock: " + bought.Items[Motor]
    );
    Check(w.Balance(P1) >= 0 && w.Balance(P1) < 25_000, "buy: paid: " + w.Balance(P1));
    Check(m.Stock(A, Motor) == stock - bought.Items[Motor], "buy: out of the stock");
    Check(w.Total() == start, "buy: no money made or lost");
    Check(b.Orders.Count() == 0, "buy: nothing queued");
    w.Balances[P2] = 0;
    var broke = new BuildRequest
    {
        Verb = BuildVerb.Buy,
        Station = A,
        Player = P2,
        Needed = Needs((Plate, U)),
    }.Run(b, w, market, s);
    Check(
        broke.StartsWith("1") && BuildBook.DecodeOrder(broke.Split('\n')[1]).Items.Count == 0,
        "buy: no money, nothing bought"
    );
}

// The build volume: a cylinder around the free space's longest side
{
    var x = new double[] { 1, 0, 0 };
    var y = new double[] { 0, 1, 0 };
    var z = new double[] { 0, 0, 1 };
    var v = BuildVolume.OfBox([0, 0, 0], [x, y, z], [10, 30, 20]);
    Check(
        v.HalfLength == 30 && v.Radius == 10 && v.Axis == y,
        "volume: along the longest side, as wide as the narrowest"
    );
    Check(
        v.Contains(0, 29, 0) && v.Contains(9.9, 0, 0) && v.Contains(0, -30, 9.9),
        "volume: inside"
    );
    Check(
        !v.Contains(0, 31, 0) && !v.Contains(0, 0, 10.1) && !v.Contains(8, 0, 8),
        "volume: outside, the box's corners too"
    );
    var tilted = BuildVolume.OfBox(
        [100, 0, 0],
        [
            [0.6, 0.8, 0],
            [-0.8, 0.6, 0],
            z,
        ],
        [50, 5, 5]
    );
    Check(
        tilted.Contains(100 + 0.6 * 49, 0.8 * 49, 0)
            && !tilted.Contains(100 + 0.6 * 51, 0.8 * 51, 0),
        "volume: a turned box"
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

    public long SalePrice(string station, string item) => m.SalePrice(w, s, station, item);

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

    public long Target(string station, string item) => 0;

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
