using System;
using System.Linq;
using ServerPlugin;

// Checks the bookkeeping of offline characters (ServerPlugin/ParkingBook.cs). Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/offline-characters/ParkingTests

int checks = 0,
    failures = 0;

void Check(bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

const long Limit = 900;
const long Saved = long.MaxValue;
const long Me = 10;
const long Body = 100;
const long Next = 101;

ParkingVerdict Offline(ParkingBook b, long now, long durable = Saved, long body = Body) =>
    b.Status(Me, body, false, now, Limit, durable);

// A body nobody has seen its player for starts its time at the first look, not long ago
var book = new ParkingBook();
Check(Offline(book, 1000) == ParkingVerdict.Wait, "first look waits");
Check(Offline(book, 1000 + Limit - 1) == ParkingVerdict.Wait, "a second short");
Check(Offline(book, 1000 + Limit) == ParkingVerdict.Park, "15 minutes after the first look");

// Back before the time is up: the time starts over when they leave again
book = new ParkingBook();
book.Observe([(Me, true)], 1000);
Check(Offline(book, 1000 + Limit) == ParkingVerdict.Wait, "online never parks");
Check(
    book.Park(Me, Body, 5, "x", 1000 + Limit, Limit, 1) == ParkOutcome.Refused,
    "online is refused, the node's look may be old"
);
book.Observe([(Me, true)], 2000);
book.Observe([], 2010);
Check(Offline(book, 2000 + Limit - 1) == ParkingVerdict.Wait, "left: not yet");
Check(book.Park(Me, Body, 5, "x", 2000 + Limit - 1, Limit, 1) == ParkOutcome.Refused, "too early");
Check(Offline(book, 2000 + Limit) == ParkingVerdict.Park, "left: time up");

// Parking: no removal until a save holds it, and a second send for the body changes nothing
var now = 2000 + Limit;
Check(book.Park(Me, Body, 5, "items", now, Limit, 7) == ParkOutcome.Added, "kept");
Check(Offline(book, now, durable: 6) == ParkingVerdict.Hold, "not saved: the body stays");
Check(Offline(book, now, durable: 7) == ParkingVerdict.Remove, "saved: the body goes");
Check(
    book.Park(Me, Body, 5, "items", now, Limit, 8) == ParkOutcome.Known,
    "the body again after a node crash: no second deposit"
);
Check(book.Parked.Count == 1, "kept once");
Check(Offline(book, now, durable: 7) == ParkingVerdict.Remove, "that body is removed again");

// Loaded from a save: it is saved, whatever the order was
var loaded = ParkingBook.Deserialize(book.Serialize());
Check(loaded.Parked.Count == 1 && loaded.Parked[0].Order == 0, "a load has no order");
Check(Offline(loaded, now, durable: 0) == ParkingVerdict.Remove, "a save holds what it loaded");
Check(loaded.Parked[0].Items == "items" && loaded.Parked[0].Credits == 5, "the row comes back");
Check(loaded.Players[Me].NearStation, "the seen row comes back");

// Taking: the new body waits for a save knowing it is on its way, and gets it once
Check(book.Status(Me, Next, true, now, Limit, Saved) == ParkingVerdict.Take, "a new body takes");
var take = book.Take(Me, Next, 9, 8);
Check(take.Wait && take.Parked.Count == 0, "the take isn't saved");
take = book.Take(Me, Next, 10, 8);
Check(take.Wait, "asking again doesn't move the wait");
take = book.Take(Me, Next, 11, 9);
Check(!take.Wait && take.Parked.Single().Items == "items", "saved: handed over");
Check(book.Consume(Me, Next) == 1, "consumed");
Check(book.Consume(Me, Next) == 0, "once");
Check(book.Status(Me, Next, true, now, Limit, Saved) == ParkingVerdict.None, "nothing left");

// A node forgot it was handed them: the same body asking again has them
book = new ParkingBook();
book.Parked.Add(
    new()
    {
        Identity = Me,
        Body = Body,
        Items = "a",
    }
);
Check(book.Take(Me, Next, 1, Saved).Parked.Count == 1, "handed over");
take = book.Take(Me, Next, 2, Saved);
Check(!take.Wait && take.Parked.Count == 0 && book.Parked.Count == 0, "not a second time");

// The body handed them died before it got them, or a node lost it: the next body takes them
book = new ParkingBook();
book.Parked.Add(
    new()
    {
        Identity = Me,
        Body = Body,
        Items = "a",
    }
);
Check(book.Take(Me, Next, 1, Saved).Parked.Count == 1, "handed to a body");
Check(book.Consume(Me, Next + 1) == 0, "another body consumes nothing");
take = book.Take(Me, Next + 1, 2, Saved);
Check(take.Parked.Single().Items == "a" && !take.Wait, "the next body gets them");
Check(book.Parked.Single().Taker == Next + 1, "and they are its to consume");

// The player came back into the removed body before its removal: it loses what it holds
book = new ParkingBook();
book.Parked.Add(
    new()
    {
        Identity = Me,
        Body = Body,
        Items = "a",
    }
);
Check(
    book.Status(Me, Body, true, now, Limit, Saved) == ParkingVerdict.TakeBack,
    "the same body is emptied first"
);
Check(book.Take(Me, Body, 1, Saved).Parked.Count == 1, "and takes");
Check(
    book.Status(Me, Body, true, now, Limit, Saved) == ParkingVerdict.Take,
    "not emptied once it was handed over: it holds them"
);

// Two kept bodies come together, and other players' stay theirs
book = new ParkingBook();
book.Parked.Add(
    new()
    {
        Identity = Me,
        Body = Body,
        Items = "a",
    }
);
book.Parked.Add(
    new()
    {
        Identity = Me,
        Body = Body + 5,
        Items = "b",
    }
);
book.Parked.Add(
    new()
    {
        Identity = Me + 1,
        Body = Body + 6,
        Items = "c",
    }
);
take = book.Take(Me, Next, 1, Saved);
Check(take.Parked.Select(p => p.Items).SequenceEqual(["a", "b"]), "both kept bodies");
Check(book.Consume(Me, Next) == 2 && book.Parked.Single().Items == "c", "the other's stays");

// Whose partition to load: near a station, offline, the time up, once
book = new ParkingBook();
book.Observe([(Me, true), (Me + 1, false), (Me + 2, true)], 1000);
book.Observe([(Me + 2, true)], 1010);
Check(book.Wakes(1000 + Limit - 1, Limit, 5).Count == 0, "nobody's time is up");
Check(
    book.Wakes(1000 + Limit, Limit, 5).SequenceEqual([Me]),
    "only the one near a station, offline"
);
book.Players[Me].Woken = true;
Check(book.Wakes(1000 + Limit, Limit, 5).Count == 0, "once");
book.Observe([(Me, true)], 2000);
book.Observe([], 2010);
Check(book.Wakes(2000 + Limit, Limit, 5).Contains(Me), "again after the next time online");
book.Players[Me].Woken = true;
Check(book.Park(Me, 5, 0, "x", 2000 + Limit, Limit, 1) == ParkOutcome.Added, "kept");
Check(
    book.Wakes(2000 + Limit, Limit, 5).Contains(Me),
    "kept: woken again, its partition may go offline before the removal"
);
book = new ParkingBook();
foreach (var i in Enumerable.Range(1, 8))
    book.Observe([(i, true)], i);
book.Observe([], 100);
Check(book.Wakes(100 + Limit, Limit, 3).SequenceEqual([1L, 2L, 3L]), "a few a pass, oldest first");

// Long gone players are forgotten
book = new ParkingBook();
book.Observe([(Me, true)], 0);
book.Observe([], 31 * 24 * 3600);
Check(book.Players.Count == 0, "forgotten after a month");

// A book it can't read isn't silently emptied
try
{
    ParkingBook.Deserialize("nonsense line");
    Check(false, "an unknown line throws");
}
catch (FormatException)
{
    Check(true, "an unknown line throws");
}
Check(ParkingBook.Deserialize("").Parked.Count == 0, "an empty book");

Console.WriteLine($"{checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
