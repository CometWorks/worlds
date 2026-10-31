using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks the contract rewards and time limits (ServerPlugin/ContractMath.cs) and the rules of the
// contracts that watch grids (ServerPlugin/ContractBook.cs). Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/contracts/ContractTests

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

// Hauling: the base and 20 SC a km, rounded up; a minute more for every 3 km started
Check(ContractMath.HaulingReward(0, 2_000, 20) == 2_000, "no distance pays the base");
Check(ContractMath.HaulingReward(100, 2_000, 20) == 4_000, "100 km");
Check(ContractMath.HaulingReward(0.01, 2_000, 20) == 2_001, "a part of a credit rounds up");
Check(ContractMath.HaulingReward(1e12, 2_000, 20) == int.MaxValue, "capped at int.MaxValue");
Check(ContractMath.HaulingMinutes(0, 20) == 20, "no distance takes the base time");
Check(ContractMath.HaulingMinutes(90, 20) == 50, "90 km adds 30 minutes");
Check(ContractMath.HaulingMinutes(91, 20) == 51, "91 km adds 31 minutes");

// Acquisitions ask for what is missing, less what contracts bring already, at most the max
Check(ContractMath.AcquisitionAmount(20_000, 0, 5_000) == 5_000, "capped at the max");
Check(ContractMath.AcquisitionAmount(2_000, 0, 5_000) == 2_000, "the rest");
Check(ContractMath.AcquisitionAmount(2_000, 2_000, 5_000) == 0, "pending covers it");
Check(ContractMath.AcquisitionAmount(2_000, 3_000, 5_000) == 0, "pending covers more");

// Iron ingots at 10 SC = 10,000 milli-SC a unit
Check(
    ContractMath.AcquisitionReward(5_000, 10_000, 100) == 50_000,
    "5 t of iron at the reference price"
);
Check(ContractMath.AcquisitionReward(5_000, 10_000, 60) == 30_000, "at 60 %");
Check(ContractMath.AcquisitionReward(1, 1, 100) == 1, "a thousandth of a credit rounds up");

Check(ContractMath.BountyReward(250.4, 20) == 5_008, "bounty by threat");
Check(ContractMath.SalvageReward(12_500, 100) == 1_250, "salvage by tonne");

// --- the book: every rule below runs on a clock the test sets, so no check waits ---

var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
var week = TimeSpan.FromDays(7);
var goneAfter = TimeSpan.FromMinutes(3);
var alpha = new ContractPoint(0, 0, 0);
const ContractGridFlags Wreck = ContractGridFlags.Wreck | ContractGridFlags.Unpowered;
const ContractGridFlags Dead = ContractGridFlags.Unpowered;
const long Hunter = 99;

ContractGrid Row(long id, ContractGridFlags flags, int blocks = 100, double x = 100_000) =>
    new()
    {
        Id = id,
        Flags = flags,
        Blocks = blocks,
        MassKg = blocks * 1000,
        Position = new ContractPoint(x, 0, 0),
        Name = "grid " + id,
    };

ContractBook Book(params ContractGrid[] rows)
{
    var book = new ContractBook(goneAfter) { Offline = [] };
    book.Report("n1", rows, t0);
    return book;
}

ContractVerdict Judge(
    ContractBook book,
    ContractTarget target,
    bool active,
    bool near,
    DateTime at,
    bool owns = false
) => book.Judge(target, active, alpha, near, 3000, 150, at, Hunter, owns);

// The week, both ways: a dead grid isn't a wreck a second before it and is one right at it
Check(!ContractBook.IsWreck(Dead, week - TimeSpan.FromSeconds(1), week), "dead: under a week, no");
Check(ContractBook.IsWreck(Dead, week, week), "dead: a week, yes");
Check(ContractBook.IsWreck(Dead, week * 4, week), "dead: a month, yes");
Check(ContractBook.IsWreck(Wreck, TimeSpan.Zero, week), "armor only: at once");
Check(!ContractBook.IsWreck(ContractGridFlags.None, week * 52, week), "powered: never");
Check(!ContractBook.IsWreck(ContractGridFlags.Derelict, week * 52, week), "a derelict with power");

// The same boundary through the book, with the partition's time from the offline table
{
    var book = Book(Row(1, Dead));
    book.Offline[1] = t0;
    var random = new Random(1);
    Check(
        book.Wrecks(t0 + week - TimeSpan.FromSeconds(1), week, 10, 5, random).Count == 0,
        "book: a second before the week"
    );
    Check(book.Wrecks(t0 + week, week, 10, 5, random).Count == 1, "book: at the week");

    // A visit brings the partition online: back offline, its week starts anew
    book.Offline.Remove(1);
    Check(book.Wrecks(t0 + week * 2, week, 10, 5, random).Count == 0, "book: online, none");
    book.Report("n1", [Row(1, Dead)], t0 + week * 2);
    book.Offline[1] = t0 + week * 2;
    Check(book.Wrecks(t0 + week * 2, week, 10, 5, random).Count == 0, "book: the week starts anew");
    Check(
        book.Wrecks(t0 + week * 3, week, 10, 5, random).Count == 1,
        "book: a week after the visit"
    );
}

// Not in the offline table: no wreck contract, however dead. An unknown table: none at all.
{
    var book = Book(Row(1, Wreck));
    Check(book.Wrecks(t0, week, 10, 5, new Random(1)).Count == 0, "an online wreck isn't posted");
    book.Offline[1] = t0;
    Check(book.Wrecks(t0, week, 10, 5, new Random(1)).Count == 1, "offline: posted");
    book.Offline = null;
    Check(book.Wrecks(t0, week, 10, 5, new Random(1)).Count == 0, "an unknown table: none");
}

// Always from the biggest: over many draws only the pool comes up, each of it, and nothing twice
{
    var rows = Enumerable.Range(1, 20).Select(i => Row(i, Wreck, i * 10)).ToArray();
    var book = Book(rows);
    foreach (var row in rows)
        book.Offline[row.Id] = t0;
    var random = new Random(42);
    var drawn = new HashSet<long>();
    var repeats = false;
    var orders = new HashSet<string>();
    for (var i = 0; i < 200; i++)
    {
        var picked = book.Wrecks(t0, week, 5, 3, random);
        repeats |= picked.Select(p => p.Id).Distinct().Count() != picked.Count;
        orders.Add(string.Join(",", picked.Select(p => p.Id)));
        foreach (var p in picked)
            drawn.Add(p.Id);
    }
    Check(
        drawn.SetEquals([16, 17, 18, 19, 20]),
        "the 5 biggest and only those: " + string.Join(",", drawn)
    );
    Check(!repeats, "no wreck twice in a draw");
    Check(orders.Count > 1, "the draw is random");
    Check(book.Wrecks(t0, week, 5, 9, random).Count == 5, "at most the pool");
    book.Targets[99] = new ContractTarget('w', 20, 1, 200, alpha);
    Check(
        !Enumerable.Range(0, 50).Any(_ => book.Wrecks(t0, week, 5, 5, random).Any(p => p.Id == 20)),
        "a wreck with a contract isn't drawn again"
    );
    Check(
        book.Wrecks(t0, week, 5, 5, random).Select(p => p.Id).Contains(15),
        "the next biggest takes its place"
    );
}

// A wreck contract: the partition waking up isn't its end
{
    var book = Book(Row(1, Dead));
    book.Offline[1] = t0;
    var target = new ContractTarget('w', 1, 1, 100, new ContractPoint(100_000, 0, 0));
    book.Targets[7] = target;
    Check(Judge(book, target, false, false, t0 + week) == ContractVerdict.Keep, "offline: stays");
    book.Offline.Remove(1);
    var woke = t0 + week;
    book.Report("n1", [Row(1, Dead)], woke);
    Check(Judge(book, target, false, false, woke) == ContractVerdict.Keep, "awake and dead: stays");

    // Someone powers it up
    book.Report("n1", [Row(1, ContractGridFlags.None)], woke);
    Check(
        Judge(book, target, false, false, woke) == ContractVerdict.Withdraw,
        "powered: withdrawn"
    );
    Check(Judge(book, target, true, true, woke) == ContractVerdict.Fail, "powered, taken: fails");

    // Ground down to a quarter with the player there; not without them
    book.Report("n1", [Row(1, Dead, 26)], woke);
    Check(
        Judge(book, target, true, true, woke) == ContractVerdict.Keep,
        "26 of 100 blocks: not yet"
    );
    book.Report("n1", [Row(1, Dead, 25)], woke);
    Check(Judge(book, target, true, false, woke) == ContractVerdict.Keep, "a quarter, nobody near");
    Check(Judge(book, target, true, true, woke) == ContractVerdict.Scrap, "a quarter, player near");
}

// Towed to the shipyard, the contract's other way. A shipyard in an offline partition still counts.
{
    var yard = Row(2, ContractGridFlags.Shipyard, 500, 1000);
    var book = Book(Row(1, Dead, 100, 100_000), yard);
    book.Offline[2] = t0;
    var target = new ContractTarget('w', 1, 1, 100, new ContractPoint(100_000, 0, 0));
    book.Targets[7] = target;
    Check(Judge(book, target, true, false, t0) == ContractVerdict.Keep, "far from the shipyard");
    book.Report("n1", [Row(1, Dead, 100, 1100)], t0);
    book.Offline[1] = t0;
    Check(Judge(book, target, true, false, t0) == ContractVerdict.Keep, "there, but offline");
    book.Offline.Remove(1);
    Check(Judge(book, target, true, false, t0) == ContractVerdict.Keep, "150 m from it, alone");
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Give, "with the player: done");

    // Scrapped: the node is told to close it, and its next report doesn't bring it back
    book.Scrap(1, t0);
    var answer = ContractBook.ParseAnswer(book.Report("n1", [Row(1, Dead, 100, 1100)], t0));
    Check(answer.Close.Contains(1), "the answer closes it");
    Check(!book.Grids.ContainsKey(1), "a late report doesn't bring it back");
    answer = ContractBook.ParseAnswer(book.Report("n1", [], t0 + TimeSpan.FromMinutes(11)));
    Check(answer.Close.Count == 0, "the close order ends");
}

// Gone: the node holding it reports on without it. Silence alone (a node restarting) isn't gone.
{
    var book = Book(Row(1, Dead));
    var target = new ContractTarget('b', 1, 1, 100, alpha);
    book.Targets[7] = target;
    var soon = t0 + goneAfter - TimeSpan.FromSeconds(10);
    book.Report("n1", [], soon);
    Check(!book.IsGone(1, soon), "under 3 minutes: a handover may still report it");
    var later = t0 + goneAfter;
    book.Report("n1", [], later);
    Check(book.IsGone(1, later), "3 minutes of reports without it: gone");

    book = Book(Row(1, Dead));
    var silence = t0 + TimeSpan.FromMinutes(14);
    book.Report("n2", [], silence);
    Check(!book.IsGone(1, silence), "its node silent, another reports: not gone yet");
    Check(book.IsGone(1, t0 + ContractBook.SilentNodeAfter), "silent for 15 minutes: gone");

    book = Book(Row(1, Dead));
    book.Offline[1] = t0;
    book.Report("n1", [], t0 + week);
    Check(!book.IsGone(1, t0 + week), "offline isn't gone");
    book.Offline = null;
    Check(!book.IsGone(1, t0 + week), "an unknown table says nothing");

    // Its partition wakes after a week: its node gets its time from then, not from before it slept
    book = Book(Row(1, Dead));
    book.Offline[1] = t0;
    book.Forget(t0 + week);
    book.Offline.Remove(1);
    book.Report("n1", [], t0 + week + TimeSpan.FromSeconds(10));
    Check(!book.IsGone(1, t0 + week + TimeSpan.FromSeconds(10)), "just woken: not gone");
    book.Report("n1", [], t0 + week + goneAfter);
    Check(book.IsGone(1, t0 + week + goneAfter), "woken and not reported for 3 minutes: gone");

    // At the reports' pace, on a node that watched nothing until now: a full report every minute,
    // whose answer names the new target, then a short one every 30 seconds. Its row, seen before the
    // contract was posted, isn't gone meanwhile, and the short reports keep it alive.
    book = Book(Row(1, Dead));
    book.Targets[7] = new ContractTarget('w', 1, 1, 100, alpha);
    var minute = TimeSpan.FromSeconds(60);
    var half = TimeSpan.FromSeconds(30);
    var named = ContractBook.ParseAnswer(book.Report("n1", [], t0 + minute));
    Check(named.Watched.Contains(1) && !book.IsGone(1, t0 + minute), "the full report names it");
    book.Report("n1", [Row(1, Dead)], t0 + minute + half);
    book.Report("n1", [], t0 + minute + half + goneAfter - half);
    Check(!book.IsGone(1, t0 + minute + goneAfter), "short reports count from the last with it");
    book.Report("n1", [], t0 + minute + half + goneAfter);
    Check(book.IsGone(1, t0 + minute + half + goneAfter), "3 minutes of short reports without it");

    // A plain server reports everything every time, so it knows at once
    book = new ContractBook(TimeSpan.Zero) { Offline = [] };
    book.Report("local", [Row(1, Dead)], t0);
    Check(!book.IsGone(1, t0), "plain: just seen");
    book.Report("local", [], t0 + TimeSpan.FromSeconds(10));
    Check(book.IsGone(1, t0 + TimeSpan.FromSeconds(10)), "plain: gone at the next report");
}

// A bounty: as on a plain server. Gone with the hunter near is done, without them it fails.
{
    var book = new ContractBook(TimeSpan.Zero) { Offline = [] };
    book.Report("n", [Row(1, ContractGridFlags.Bounty)], t0);
    book.Grids[1].Threat = 100;
    var target = new ContractTarget('b', 1, 1, 100, alpha);
    book.Targets[7] = target;
    Check(Judge(book, target, false, false, t0) == ContractVerdict.Keep, "open bounty stays");
    Check(Judge(book, target, true, false, t0) == ContractVerdict.Keep, "taken, threat high");
    var row = Row(1, ContractGridFlags.Bounty);
    row.Threat = 25;
    book.Report("n", [row], t0);
    Check(Judge(book, target, true, false, t0) == ContractVerdict.Finish, "threat at a quarter");
    book.Report("n", [Row(1, ContractGridFlags.None)], t0);
    Check(Judge(book, target, true, false, t0) == ContractVerdict.Finish, "no NPC grid any more");

    var witness = t0 + TimeSpan.FromSeconds(30);
    book.Report("n", [Row(1, ContractGridFlags.Bounty)], witness);
    book.Grids[1].Threat = 100;
    Judge(book, target, true, true, witness);
    book.Report("n", [], witness + TimeSpan.FromSeconds(10));
    Check(
        Judge(book, target, true, false, witness + TimeSpan.FromSeconds(10))
            == ContractVerdict.Finish,
        "gone just after the hunter was near"
    );

    // The last check and the last report are up to 30 seconds apart, either way round, within the
    // minute of slack
    book = new ContractBook(goneAfter) { Offline = [] };
    book.Report("n", [Row(1, ContractGridFlags.Bounty)], t0);
    target = new ContractTarget('b', 1, 1, 100, alpha);
    book.Targets[7] = target;
    Judge(book, target, true, true, t0);
    var lastSeen = t0 + TimeSpan.FromSeconds(30);
    book.Report("n", [Row(1, ContractGridFlags.Bounty)], lastSeen);
    book.Report("n", [], lastSeen + goneAfter);
    Check(
        Judge(book, target, true, false, lastSeen + goneAfter) == ContractVerdict.Finish,
        "seen near a check before its last report: witnessed"
    );

    book = new ContractBook(goneAfter) { Offline = [] };
    book.Report("n", [Row(1, ContractGridFlags.Bounty)], t0);
    target = new ContractTarget('b', 1, 1, 100, alpha);
    book.Targets[7] = target;
    Judge(book, target, true, true, t0 - TimeSpan.FromMinutes(2));
    book.Report("n", [], t0 + goneAfter);
    Check(
        Judge(book, target, true, false, t0 + goneAfter) == ContractVerdict.Fail,
        "near two minutes before its last report: not witnessed"
    );

    book = new ContractBook(TimeSpan.Zero) { Offline = [] };
    book.Report("n", [Row(1, ContractGridFlags.Bounty)], t0);
    target = new ContractTarget('b', 1, 1, 100, alpha);
    book.Targets[7] = target;
    book.Report("n", [], t0 + TimeSpan.FromMinutes(1));
    Check(
        Judge(book, target, true, false, t0 + TimeSpan.FromMinutes(1)) == ContractVerdict.Fail,
        "gone with nobody near fails"
    );
    Check(
        Judge(book, target, false, false, t0 + TimeSpan.FromMinutes(1)) == ContractVerdict.Withdraw,
        "gone before anyone took it: withdrawn"
    );
}

// Bounties and derelicts stay in range of their station, in their own order
{
    var near = Row(1, ContractGridFlags.Bounty, 10, 20_000);
    near.Threat = 10;
    var high = Row(2, ContractGridFlags.Bounty, 10, 25_000);
    high.Threat = 90;
    var far = Row(3, ContractGridFlags.Bounty, 10, 40_000);
    far.Threat = 500;
    var weak = Row(4, ContractGridFlags.Bounty, 10, 1000);
    weak.Threat = 1;
    var book = Book(near, high, far, weak);
    var found = book.Bounties(alpha, 30_000, 5, 5);
    Check(
        found.Select(r => r.Id).SequenceEqual([2L, 1L]),
        "bounties: highest first, range, threat"
    );
    Check(book.Bounties(alpha, 30_000, 5, 1).Single().Id == 2, "bounties: as many as wanted");

    book = Book(
        Row(1, ContractGridFlags.Derelict, 10, 20_000),
        Row(2, ContractGridFlags.Derelict, 10, 5_000),
        Row(3, ContractGridFlags.Derelict, 10, 40_000)
    );
    Check(
        book.Derelicts(alpha, 30_000, 5).Select(r => r.Id).SequenceEqual([2L, 1L]),
        "derelicts: nearest first, in range"
    );
}

// Candidates fade when no report names them, unless their partition is offline or a contract has them
{
    var book = Book(Row(1, Dead), Row(2, Dead), Row(3, Dead));
    book.Offline[2] = t0;
    book.Targets[7] = new ContractTarget('w', 3, 1, 100, alpha);
    book.Forget(t0 + ContractBook.StaleAfter);
    Check(!book.Grids.ContainsKey(1), "a candidate nobody names goes");
    Check(book.Grids.ContainsKey(2), "a grid in an offline partition stays");
    Check(book.Grids.ContainsKey(3), "a target stays");
    book.Offline = null;
    book.Report("n1", [Row(1, Dead)], t0);
    book.Forget(t0 + week);
    Check(book.Grids.ContainsKey(1), "an unknown table forgets nothing");
}

// Saved with the world, and read back, an older world's targets too
{
    var book = Book(Row(1, Wreck), Row(2, ContractGridFlags.Shipyard, 5, 1000.5));
    book.Grids[1].Name = "with\ttab";
    book.Targets[7] = new ContractTarget('w', 1, 1, 100, new ContractPoint(1.5, -2, 3))
    {
        HunterSeen = t0,
    };
    var back = new ContractBook(goneAfter) { Offline = [] };
    back.LoadTargets(book.SerializeTargets());
    back.LoadGrids(book.SerializeGrids(), t0 + week);
    var target = back.Targets[7];
    Check(
        target.Kind == 'w'
            && target.Grid == 1
            && target.Size == 100
            && target.Position.X == 1.5
            && target.Position.Y == -2
            && target.HunterSeen == t0,
        "the target comes back"
    );
    var grid = back.Grids[1];
    Check(
        grid.Flags == Wreck
            && grid.Blocks == 100
            && grid.MassKg == 100_000
            && grid.Node == "n1"
            && grid.Seen == t0 + week
            && grid.Name == "with tab",
        "the grid comes back"
    );
    Check(back.Grids[2].Position.X == 1000.5, "the shipyard comes back");

    back = new ContractBook(goneAfter);
    back.LoadTargets("5:b:11:22:3.5:1:2:3;6:s:12:22:0:4:5:6");
    Check(
        back.Targets.Count == 2
            && back.Targets[5].Size == 3.5
            && back.Targets[5].HunterSeen == DateTime.MinValue,
        "an older world's targets"
    );
    Check(ContractBook.ParseAnswer(";;").Watched.Count == 0, "an empty answer");
}

// Towed in, the grid goes to the player, on whichever node holds it, until a report shows it done
{
    var near = Row(1, Dead, 100, 1100);
    var book = Book(near, Row(2, ContractGridFlags.Shipyard, 500, 1000));
    book.Give(1, Hunter);
    var answer = ContractBook.ParseAnswer(book.Report("n1", [Row(1, Dead, 100, 1100)], t0));
    Check(answer.Give.SequenceEqual([(1L, Hunter)]), "the answer gives it to the player");
    Check(answer.Watched.Contains(1), "and the nodes keep reporting it");
    // Handed over before the node acted: the new node gets the same order
    answer = ContractBook.ParseAnswer(book.Report("n2", [Row(1, Dead, 100, 1100)], t0));
    Check(answer.Give.Count == 1, "another node holds it now: told too");

    var owned = Row(1, Dead, 100, 1100);
    owned.Owner = Hunter;
    answer = ContractBook.ParseAnswer(book.Report("n2", [owned], t0));
    Check(answer.Give.Count == 0 && !answer.Watched.Contains(1), "a report shows it done");
    Check(book.Grids.ContainsKey(1), "it isn't scrapped");
    Check(
        book.Derelicts(alpha, 100_000, 5).Count == 0
            && book.Wrecks(t0, week, 5, 5, new Random(1)).Count == 0,
        "a given grid gets no new contract, or it could be disowned and sold again"
    );

    // Armor only: no block can have an owner, so nothing to wait for
    book = Book(Row(1, Dead, 100, 1100));
    book.Give(1, Hunter);
    answer = ContractBook.ParseAnswer(
        book.Report("n1", [Row(1, Dead | ContractGridFlags.Unownable, 100, 1100)], t0)
    );
    Check(answer.Give.Count == 0, "armor only: nothing more to do");

    // Lost before the node acted: the order ends
    book = Book(Row(1, Dead, 100, 1100));
    book.Give(1, Hunter);
    book.Report("n1", [], t0 + goneAfter);
    book.Forget(t0 + goneAfter);
    Check(book.Watched.Count() == 0, "gone before it was given: the order ends");

    // A World Authority restart loses neither an order nor the memory of a given grid
    book = Book(Row(1, Dead, 100, 1100));
    book.Give(1, Hunter);
    book.Give(3, 0);
    var back = new ContractBook(goneAfter);
    back.LoadGiven(book.SerializeGiven());
    Check(back.Given[1] == Hunter && back.Given[3] == 0, "the orders come back from the save");
}

// Abuse: selling your own grid, someone else's claim, a grid in use, a grid already at the shipyard
{
    var yard = Row(2, ContractGridFlags.Shipyard, 500, 1000);
    var book = Book(Row(1, Dead, 100, 1100), yard);
    var target = new ContractTarget('w', 1, 1, 100, new ContractPoint(1100, 0, 0)) { Owner = 5 };
    book.Targets[7] = target;
    var salvage = new ContractTarget('s', 1, 1, 0, new ContractPoint(1100, 0, 0)) { Owner = 5 };
    book.Targets[8] = salvage;

    Check(Judge(book, target, true, true, t0, owns: true) == ContractVerdict.Fail, "own wreck");
    Check(Judge(book, salvage, true, true, t0, owns: true) == ContractVerdict.Fail, "own derelict");
    Check(Judge(book, target, false, false, t0, owns: true) == ContractVerdict.Keep, "until taken");
    Check(
        book.Judge(
            new ContractTarget('b', 1, 1, 0, new ContractPoint(1100, 0, 0)),
            true,
            alpha,
            true,
            3000,
            150,
            t0,
            Hunter,
            true
        ) != ContractVerdict.Fail,
        "the rule is no bounty's"
    );

    // The old owner is still its owner: that's the point of a wreck contract
    var row = Row(1, Dead, 100, 1100);
    row.Owner = 5;
    book.Report("n1", [row], t0);
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Give, "the old owner's: taken");

    row = Row(1, Dead, 100, 1100);
    row.Owner = 6;
    book.Report("n1", [row], t0);
    Check(
        Judge(book, target, true, true, t0) == ContractVerdict.Fail,
        "a third player's now: wreck"
    );
    Check(Judge(book, salvage, true, true, t0) == ContractVerdict.Fail, "and derelict");

    // The hunter's own repairs: their blocks and power don't end it
    row = Row(1, ContractGridFlags.None, 100, 1100);
    row.Owner = Hunter;
    book.Report("n1", [row], t0);
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Give, "repaired by the hunter");
    row = Row(1, ContractGridFlags.None, 100, 1100);
    row.Owner = 5;
    book.Report("n1", [row], t0);
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Fail, "repowered by its owner");

    // In use: nothing happens while someone sits in it, and a free grid doesn't qualify
    var seated = Dead | ContractGridFlags.Seated;
    book.Report("n1", [Row(1, seated, 100, 1100)], t0);
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Keep, "seated at the shipyard");
    Check(Judge(book, salvage, true, true, t0) == ContractVerdict.Keep, "a seated derelict too");
    Check(Judge(book, target, false, false, t0) == ContractVerdict.Withdraw, "seated, not taken");
    book.Report("n1", [Row(1, seated, 25, 100_000)], t0);
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Keep, "seated: no scrapping");
    Check(!ContractBook.IsWreck(seated, week * 52, week), "a seat in use: no wreck");

    book.Report("n1", [Row(1, Dead, 100, 1100)], t0);
    Check(Judge(book, target, true, false, t0) == ContractVerdict.Keep, "towed in by someone else");

    // Already at the shipyard: nothing to tow, so no contract
    book = Book(
        Row(1, ContractGridFlags.Derelict, 100, 1100),
        Row(3, ContractGridFlags.Derelict, 100, 5000),
        Row(4, ContractGridFlags.Derelict | ContractGridFlags.Seated, 100, 5000),
        yard
    );
    Check(
        book.Derelicts(alpha, 100_000, 5, 3000, 150).Select(r => r.Id).SequenceEqual([3L]),
        "only the derelict to tow, and nobody's in it"
    );
}

// A static grid: the same id and row once converted to a ship
{
    var flags = Dead;
    var book = Book(Row(1, flags, 100, 100_000), Row(2, ContractGridFlags.Shipyard, 500, 1000));
    var target = new ContractTarget('w', 1, 1, 100, new ContractPoint(100_000, 0, 0));
    book.Targets[7] = target;
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Keep, "static, not there yet");
    book.Report("n1", [Row(1, flags, 100, 1100)], t0);
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Give, "converted and towed in");
    book.Report("n1", [Row(1, flags, 25, 100_000)], t0);
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Scrap, "or ground down in place");
}

// A row and a target keep whose they were through the wire and the save
{
    var row = Row(1, Dead, 100, 1100);
    row.Owner = 5;
    row.Builder = 6;
    var back = ContractGrid.Decode(row.Encode());
    Check(back.Owner == 5 && back.Builder == 6, "a row's owner and builder");
    var book = new ContractBook(goneAfter);
    book.Targets[7] = new ContractTarget('w', 1, 1, 100, alpha) { Owner = 5, Builder = 6 };
    var loaded = new ContractBook(goneAfter);
    loaded.LoadTargets(book.SerializeTargets());
    Check(loaded.Targets[7].Owner == 5 && loaded.Targets[7].Builder == 6, "a target's");
}

Console.WriteLine($"{checks - failures}/{checks} contract math checks passed");
return failures == 0 ? 0 : 1;
