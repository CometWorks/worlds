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
Check(ContractMath.WreckReward(12_500, 100) == 12_500, "a wreck pays its components' value");
Check(ContractMath.WreckReward(12_500.2, 50) == 6_251, "at half, rounded up");

// --- the book: every rule below runs on a clock the test sets, so no check waits ---

var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
var week = TimeSpan.FromDays(7);
var goneAfter = TimeSpan.FromMinutes(3);
var alpha = new ContractPoint(0, 0, 0);
const ContractGridFlags Wreck =
    ContractGridFlags.Wreck | ContractGridFlags.Unpowered | ContractGridFlags.Abandoned;
const ContractGridFlags Dead = ContractGridFlags.Unpowered | ContractGridFlags.Abandoned;
const long Hunter = 99;

ContractGrid Row(long id, ContractGridFlags flags, int blocks = 100, double x = 100_000) =>
    new()
    {
        Id = id,
        Flags = flags,
        Blocks = blocks,
        MassKg = blocks * 1000,
        Value = blocks * 10,
        Position = new ContractPoint(x, 0, 0),
        Name = "grid " + id,
    };

ContractBook Book(params ContractGrid[] rows)
{
    var book = new ContractBook(goneAfter) { Offline = [] };
    book.Report("n1", rows, t0);
    return book;
}

// The wrecks the book would post with one station at alpha, its shipyard where a test puts one
List<ContractGrid> Hunted(ContractBook book, DateTime at) =>
    book.Hunts([(1, alpha)], _ => 99, 3000, 150, at)
        .Where(h => h.Hunt.Kind == 'w')
        .Select(h => h.Hunt.Grid)
        .ToList();

ContractVerdict Judge(
    ContractBook book,
    ContractTarget target,
    bool active,
    bool near,
    DateTime at,
    bool owns = false
) => book.Judge(target, active, alpha, near, 3000, 150, at, Hunter, owns);

// What a wreck hunt takes: nobody's, an NPC's down for a while, a bot's, a player's long away
{
    var book = Book();
    bool Target(ContractGridFlags flags, long owner = 0, long id = 1)
    {
        var row = Row(id, flags);
        row.Owner = owner;
        book.Report("n1", [row], t0);
        return book.IsTarget(book.Grids[id], t0);
    }
    Check(Target(ContractGridFlags.Derelict), "a movable derelict, powered or not");
    Check(Target(Wreck), "nothing works and nobody's: at once");
    Check(Target(Dead), "nobody's without power, static too");
    Check(!Target(ContractGridFlags.Abandoned), "a static base of nobody's with power");
    Check(!Target(ContractGridFlags.None), "powered and someone's: never");
    Check(!Target(ContractGridFlags.Derelict | ContractGridFlags.Seated), "someone sits in it");
    Check(!Target(ContractGridFlags.Derelict | ContractGridFlags.SafeZone), "in a safe zone");
    Check(!Target(ContractGridFlags.Debris), "debris: an area's piece, not a wreck");

    // A player's: only once they and their faction are away
    Check(!Target(ContractGridFlags.Unpowered, 5), "a player's, they are around");
    book.Rules.OwnerAway = o => o == 5;
    Check(Target(ContractGridFlags.Unpowered, 5), "a player's, they are away");
    Check(!Target(ContractGridFlags.None, 5), "away, but it has power");

    // An NPC's: out of power or control for NpcDown, counted from the first report saying so
    var npc = ContractGridFlags.Npc | ContractGridFlags.Disabled;
    book = Book(Row(2, npc));
    Check(!book.IsTarget(book.Grids[2], t0 + TimeSpan.FromMinutes(9)), "NPC: 9 minutes down");
    Check(book.IsTarget(book.Grids[2], t0 + TimeSpan.FromMinutes(10)), "NPC: 10 minutes down");
    book.Report("n1", [Row(2, ContractGridFlags.Npc)], t0 + TimeSpan.FromMinutes(11));
    book.Report("n1", [Row(2, npc)], t0 + TimeSpan.FromMinutes(12));
    Check(
        !book.IsTarget(book.Grids[2], t0 + TimeSpan.FromMinutes(21)),
        "NPC: back in control starts it anew"
    );
    var bot = ContractGridFlags.Bot | ContractGridFlags.Unpowered;
    book = Book(Row(3, bot));
    Check(!book.IsTarget(book.Grids[3], t0 + TimeSpan.FromMinutes(59)), "bot: under an hour");
    Check(book.IsTarget(book.Grids[3], t0 + TimeSpan.FromHours(1)), "bot: an hour");
}

// Posting: anywhere, at the nearest station, the most valuable first, up to the station's room
{
    var beta = new ContractPoint(1_000_000, 0, 0);
    ContractGrid Valued(long id, double value, double x)
    {
        var row = Row(id, ContractGridFlags.Derelict, 100, x);
        row.Value = value;
        return row;
    }
    var book = Book(
        Valued(1, 5_000, 100_000),
        Valued(2, 9_000, 400_000),
        Valued(3, 7_000, 900_000),
        Valued(4, 1_000, 950_000),
        Valued(5, 500, 300_000)
    );
    book.Rules.MinValue = 800;
    var hunts = book.Hunts([(1, alpha), (2, beta)], s => s == 1 ? 1 : 5, 3000, 150, t0);
    Check(
        hunts
            .Select(h => (h.Hunt.Grid.Id, h.Station))
            .SequenceEqual([(2L, 1L), (3L, 2L), (4L, 2L)]),
        "nearest station, most valuable first, room per station, the least value: "
            + string.Join(",", hunts.Select(h => $"{h.Hunt.Grid.Id}@{h.Station}"))
    );
    book.Rules.PlayerNear = p => p.X == 400_000;
    Check(
        book.Hunts([(1, alpha), (2, beta)], _ => 9, 3000, 150, t0).All(h => h.Hunt.Grid.Id != 2),
        "nothing a player is within sync range of"
    );

    // A gateway's row has no value: priced by its mass at what the reported grids are worth per kg
    var row = Row(9, Dead);
    row.MassKg = 2_000;
    row.Value = 0;
    book = Book(Valued(1, 5_000, 0));
    book.Grids[1].MassKg = 1_000;
    Check(Math.Abs(book.Value(row) - 10_000) < 1e-6, "5 SC a kg: 10,000");
}

// Debris areas: pieces near each other, worth a contract by count or value, at most the radius
{
    ContractGrid Piece(long id, double x, double value = 10)
    {
        var row = Row(id, ContractGridFlags.Debris, 3, x);
        row.Value = value;
        return row;
    }
    var book = Book(
        Piece(1, 10_000),
        Piece(2, 10_200),
        Piece(3, 10_400),
        Piece(4, 10_600),
        Piece(5, 10_800),
        Piece(6, 20_000),
        Piece(7, 20_100),
        Piece(8, 40_000, 5_000)
    );
    book.Rules.MinValue = 2_000;
    var areas = book.Areas();
    var chain = areas.Single(a => a.Members.Contains(1));
    Check(chain.Members.Length == 5, "a chain of 5 pieces 200 m apart");
    Check(
        Math.Abs(chain.Position.X - 10_400) < 1e-6 && Math.Abs(chain.Radius - 450) < 1e-6,
        "around the center, 50 m past the farthest"
    );
    Check(areas.All(a => !a.Members.Contains(6)), "2 cheap pieces: none");
    Check(areas.Any(a => a.Members.SequenceEqual([8L])), "one piece worth the least reward");
    book.Rules.DebrisMaxRadius = 300;
    book.Rules.DebrisMinPieces = 3;
    Check(
        book.Areas().Single(a => a.Members.Contains(3)).Members.Length == 3
            && book.Areas().All(a => a.Radius <= 300),
        "capped: the pieces farther out wait"
    );

    // Taken, done when none is left in it, with the player near
    book.Rules.DebrisMaxRadius = 2_000;
    book.Rules.DebrisMinPieces = 5;
    var target = new ContractTarget('a', 1, 1, chain.Radius, chain.Position)
    {
        Members = chain.Members,
    };
    book.Targets[7] = target;
    Check(!book.Areas().Any(a => a.Members.Contains(1)), "no second area on the same pieces");
    Check(
        ContractBook.ParseAnswer(book.Report("n1", [], t0)).Watched.IsSupersetOf(chain.Members),
        "every piece is watched"
    );
    Check(Judge(book, target, true, true, t0) == ContractVerdict.Keep, "all there");
    Check(
        Judge(book, target, true, true, t0, owns: true) == ContractVerdict.Fail,
        "their own debris: fails"
    );
    var later = t0 + goneAfter;
    var moved = Piece(5, 12_000);
    book.Report("n1", [Piece(1, 10_000), moved], later);
    Check(Judge(book, target, true, true, later) == ContractVerdict.Keep, "one piece left");
    book.Report("n1", [moved], later + goneAfter);
    Check(
        Judge(book, target, true, false, later + goneAfter) == ContractVerdict.Keep,
        "empty, nobody near"
    );
    Check(
        Judge(book, target, true, true, later + goneAfter) == ContractVerdict.Finish,
        "empty, towed out or ground down, the player near"
    );
    Check(
        Judge(book, target, false, false, later + goneAfter) == ContractVerdict.Withdraw,
        "nobody took it, emptied anyway: withdrawn"
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
    Check(ContractBook.ParseAnswer(";;;;").Watched.Count == 0, "an empty answer");
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
        Hunted(book, t0).Count == 0,
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

// A tow over the limits: the order repeats until the grid is gone, and it survives a restart
{
    var book = Book(Row(1, Dead, 100, 1100));
    book.BreakDown(1, Hunter, "Alpha, Station;1");
    Check(book.Watched.Contains(1), "a grid to break down is watched");
    Check(book.Given.ContainsKey(1), "no contract or other tow takes it now");
    var order = ContractBook.ParseAnswer(book.Report("n1", [Row(1, Dead, 100, 1100)], t0));
    Check(
        order.BreakDown.SequenceEqual([(1L, Hunter, "Alpha, Station;1")]),
        "the answer orders it, whatever the station's name"
    );
    Check(order.Give.Count == 0, "and doesn't give it");
    var back = new ContractBook(goneAfter);
    back.LoadBreakdowns(book.SerializeBreakdowns());
    Check(back.Breakdowns[1] == (Hunter, "Alpha, Station;1"), "it comes back from the save");
    order = ContractBook.ParseAnswer(book.Report("n1", [Row(1, Dead, 100, 1100)], t0 + goneAfter));
    Check(order.BreakDown.Count == 1, "it repeats while the grid is there");
    book.Report("n1", [], t0 + goneAfter * 3);
    book.Forget(t0 + goneAfter * 3);
    order = ContractBook.ParseAnswer(book.Report("n1", [], t0 + goneAfter * 3));
    Check(order.BreakDown.Count == 0, "and ends when it is gone");
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
        Hunted(book, t0).Select(r => r.Id).SequenceEqual([3L]),
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

// A grid in an active safe zone gets no contract, asleep or not
{
    var zone = ContractGridFlags.SafeZone;
    var wreck = ContractGridFlags.Wreck | ContractGridFlags.Unpowered;
    var book = Book(Row(1, wreck | zone, 100, 100_000), Row(2, wreck, 50, 100_000));
    book.Offline = new() { [1] = t0, [2] = t0 };
    var wrecks = Hunted(book, t0 + TimeSpan.FromDays(100));
    Check(wrecks.Select(r => r.Id).SequenceEqual([2L]), "still active however long it sleeps");

    book = Book(
        Row(1, ContractGridFlags.Derelict | zone, 100, 2000),
        Row(2, ContractGridFlags.Derelict, 50, 2000),
        Row(5, ContractGridFlags.Shipyard, 500, 1000)
    );
    Check(Hunted(book, t0).Select(r => r.Id).SequenceEqual([2L]), "no wreck contract in a zone");
    book = Book(
        Row(1, ContractGridFlags.Bounty | zone, 100, 100),
        Row(2, ContractGridFlags.Bounty, 100, 100)
    );
    book.Grids[1].Threat = book.Grids[2].Threat = 10;
    Check(
        book.Bounties(alpha, 3000, 1, 5).Select(r => r.Id).SequenceEqual([2L]),
        "no bounty in a zone"
    );

    var bounty = new ContractTarget('b', 1, 1, 10, new ContractPoint(100, 0, 0));
    book.Targets[7] = bounty;
    Check(Judge(book, bounty, false, false, t0) == ContractVerdict.Withdraw, "nobody took it");
    Check(
        Judge(book, bounty, true, false, t0) == ContractVerdict.Keep,
        "someone took it: on it goes"
    );

    // The shipyard is in its station's zone
    var towed = Row(1, ContractGridFlags.Towed | zone, 100, 1100);
    towed.Tower = Hunter;
    book = Book(towed, Row(5, ContractGridFlags.Shipyard, 500, 1000));
    Check(book.TowedIn([alpha], 3000, 150, t => 0).Count == 1, "towed into a zone: taken over");
}

// Towing a grid in against the limits: the player's limit, 10 % over it, and a ship's
{
    Check(ContractMath.Tow(100, 0, 1000, 500) == ContractMath.TowResult.Take, "well within");
    Check(
        ContractMath.Tow(100, 900, 1000, 500) == ContractMath.TowResult.Take,
        "right at the limit"
    );
    Check(
        ContractMath.Tow(101, 900, 1000, 500) == ContractMath.TowResult.TakeOverLimit,
        "a PCU over: taken, with a warning"
    );
    Check(
        ContractMath.Tow(200, 900, 1000, 500) == ContractMath.TowResult.TakeOverLimit,
        "exactly 10 % over: taken"
    );
    Check(
        ContractMath.Tow(201, 900, 1000, 500) == ContractMath.TowResult.OverLimit,
        "more than 10 % over: broken down"
    );
    Check(
        ContractMath.Tow(501, 0, 1000, 500) == ContractMath.TowResult.TooBig,
        "over a ship's limit"
    );
    Check(
        ContractMath.Tow(501, 900, 1000, 500) == ContractMath.TowResult.TooBig,
        "a ship's limit comes first"
    );
    Check(ContractMath.Tow(501, 0, 1000, 0) == ContractMath.TowResult.Take, "no ship limit");
    Check(
        ContractMath.Tow(5000, 0, int.MaxValue, 0) == ContractMath.TowResult.Take,
        "block limits off"
    );
    Check(
        ContractMath.Tow(1_000_000, 1_000_000, 1_000_000, 0) == ContractMath.TowResult.OverLimit,
        "the real limit: no overflow"
    );
}

// An NPC grid towed in, no contract needed
{
    ContractGrid Towed(long id, ContractGridFlags more = ContractGridFlags.None, double x = 1100)
    {
        var row = Row(id, ContractGridFlags.Towed | more, 100, x);
        row.Tower = Hunter;
        return row;
    }
    var yard = Row(2, ContractGridFlags.Shipyard, 500, 1000);
    long None(ContractTarget t) => 0;
    List<long> In(ContractBook b, Func<ContractTarget, long> takenBy) =>
        b.TowedIn([alpha], 3000, 150, takenBy).Select(r => r.Id).ToList();

    var book = Book(Towed(1), Towed(3, ContractGridFlags.None, 100_000), yard);
    Check(In(book, None).SequenceEqual([1L]), "at the shipyard, not elsewhere");

    book = Book(Towed(1, ContractGridFlags.Seated), yard);
    Check(In(book, None).Count == 0, "someone in it: left alone");

    book = Book(Towed(1), yard);
    book.Offline[1] = t0;
    Check(In(book, None).Count == 0, "asleep: it hasn't just been brought there");

    book = Book(Towed(1), yard);
    book.Give(1, Hunter);
    Check(In(book, None).Count == 0, "given once");

    // Contracts on it: one nobody took doesn't matter and is withdrawn once it is given away
    book = Book(Towed(1), yard);
    var bounty = new ContractTarget('b', 1, 1, 10, new ContractPoint(1100, 0, 0));
    book.Targets[7] = bounty;
    Check(In(book, None).Count == 1, "a bounty nobody took: taken over");
    Check(In(book, t => Hunter).Count == 1, "the player's own bounty: taken over");
    Check(In(book, t => 5).Count == 0, "someone else's bounty: left to them");
    book.Grids[1].Flags |= ContractGridFlags.Bounty;
    Check(Judge(book, bounty, false, false, t0) == ContractVerdict.Keep, "the posted bounty stays");
    book.Give(1, Hunter);
    Check(Judge(book, bounty, false, false, t0) == ContractVerdict.Withdraw, "until it's given");

    book = Book(Towed(1), yard);
    book.Targets[7] = new ContractTarget('s', 1, 1, 0, new ContractPoint(1100, 0, 0));
    Check(In(book, t => Hunter).Count == 0, "a salvage contract taken: its own way, and paid");
}

// Chat goes to each node once, the one with the player delivers it
{
    var book = Book();
    book.Tell(Hunter, "It's yours; 100%: a, b", t0);
    var answer = ContractBook.ParseAnswer(book.Report("n1", [], t0));
    Check(answer.Tell.SequenceEqual([(Hunter, "It's yours; 100%: a, b")]), "the text survives");
    Check(ContractBook.ParseAnswer(book.Report("n1", [], t0)).Tell.Count == 0, "told once");
    Check(ContractBook.ParseAnswer(book.Report("n2", [], t0)).Tell.Count == 1, "another node too");
    Check(
        ContractBook.ParseAnswer(book.Report("n3", [], t0 + TimeSpan.FromMinutes(3))).Tell.Count
            == 0,
        "too late for a node that was away"
    );
}

// A row and a target keep whose they were through the wire and the save
{
    var row = Row(1, Dead, 100, 1100);
    row.Owner = 5;
    row.Builder = 6;
    row.Pcu = 7;
    row.Tower = 8;
    var back = ContractGrid.Decode(row.Encode());
    Check(back.Owner == 5 && back.Builder == 6, "a row's owner and builder");
    Check(back.Pcu == 7 && back.Tower == 8, "its PCU and who tows it");
    var book = new ContractBook(goneAfter);
    book.Targets[7] = new ContractTarget('w', 1, 1, 100, alpha) { Owner = 5, Builder = 6 };
    var loaded = new ContractBook(goneAfter);
    loaded.LoadTargets(book.SerializeTargets());
    Check(loaded.Targets[7].Owner == 5 && loaded.Targets[7].Builder == 6, "a target's");
}

// The gateway's rows of offline partitions: wrecks no node reported
{
    ContractOfflineGrid Offline(
        long id,
        int blocks,
        long group,
        bool functional = false,
        bool powered = false,
        long[] owners = null,
        bool station = false,
        bool isStatic = false,
        double mass = 5000,
        string name = null
    ) =>
        new(
            id,
            name ?? "grid " + id,
            isStatic,
            blocks,
            new ContractPoint(id, 0, 0),
            owners ?? [],
            mass,
            group,
            functional,
            powered,
            station
        );
    List<ContractOfflineRow> Groups(params ContractOfflineGrid[] rows) =>
        ContractBook.OfflineGroups(rows, 3, o => o == 9, t0);
    var days = TimeSpan.FromDays(7);

    var wreck = Groups(Offline(1, 10, 1)).Single();
    Check(
        wreck.Grid.Flags
            == (
                ContractGridFlags.Wreck
                | ContractGridFlags.Unpowered
                | ContractGridFlags.Abandoned
                | ContractGridFlags.Derelict
            ),
        "ownerless, nothing works: a wreck"
    );
    Check(
        Groups(Offline(1, 10, 1, functional: true, isStatic: true)).Single().Grid.Flags
            == (ContractGridFlags.Unpowered | ContractGridFlags.Abandoned),
        "a working terminal block, static: nobody's without power"
    );
    Check(
        Groups(Offline(1, 10, 1, owners: [5])).Single()
            is { Grid: { Owner: 5, Flags: ContractGridFlags.Unpowered } },
        "an owner: only without power, and it is kept"
    );
    Check(
        Groups(Offline(1, 10, 1, functional: true, powered: true, owners: [5])).Count == 0,
        "someone's, powered: none"
    );
    Check(Groups(Offline(1, 2, 1)).Count == 0, "too few blocks");
    Check(Groups(Offline(1, 10, 1, station: true)).Count == 0, "a station");
    Check(Groups(Offline(1, 10, 1, owners: [9])).Count == 0, "OCT's");
    Check(Groups(Offline(1, 10, 1, mass: 0)).Count == 0, "a mass of 0 is unknown");
    Check(
        ContractBook
            .OfflineGroups(
                [
                    new ContractOfflineGrid(
                        1,
                        "old",
                        false,
                        10,
                        alpha,
                        [],
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                ],
                3,
                o => false,
                t0
            )
            .Count == 0,
        "a row read back from a Final: unknown is no wreck"
    );
    foreach (
        var unknown in new[]
        {
            Offline(1, 10, 1) with
            {
                MassKg = null,
            },
            Offline(1, 10, 1) with
            {
                Group = null,
            },
            Offline(1, 10, 1) with
            {
                Functional = null,
            },
            Offline(1, 10, 1) with
            {
                Powered = null,
            },
            Offline(1, 10, 1) with
            {
                Station = null,
            },
        }
    )
        Check(Groups(unknown).Count == 0, "one unknown fact is enough");
    Check(
        ContractBook
            .OfflineGroups(
                [Offline(1, 1, 1, isStatic: true, name: "TL 01 Alpha > Beta")],
                1,
                o => false,
                t0
            )
            .Count == 0,
        "a lane computer"
    );
    Check(
        ContractBook
            .OfflineGroups([Offline(1, 1, 1, isStatic: true, name: "Tower")], 1, o => false, t0)
            .Count == 1,
        "another single block"
    );

    // A group adds up, under its biggest grid
    var docked = Groups(
        Offline(3, 2, 3, mass: 1000),
        Offline(7, 8, 3, mass: 2000),
        Offline(8, 5, 8)
    );
    var hull = docked.Single(r => r.Members.Contains(7));
    Check(docked.Count == 2, "one row per physical group");
    Check(
        hull.Grid.Id == 7
            && hull.Grid.Blocks == 10
            && hull.Grid.MassKg == 3000
            && hull.Grid.Position.X == 7
            && hull.Members.Order().SequenceEqual([3L, 7L]),
        "the group's blocks and mass, at its biggest grid"
    );
    Check(
        Groups(Offline(3, 8, 3), Offline(7, 8, 3)).Single().Grid.Id == 3,
        "the same grid every time when two are as big"
    );
    Check(
        Groups(
            Offline(3, 8, 3),
            Offline(7, 8, 3, powered: true, functional: true, owners: [5])
        ).Count == 0,
        "powered by a docked ship"
    );
    Check(Groups(Offline(3, 8, 3), Offline(7, 8, 3, owners: [9])).Count == 0, "docked to OCT's");
    Check(
        Groups(Offline(3, 8, 3), Offline(7, 8, 3, station: true)).Count == 0,
        "docked to a station"
    );
    Check(
        Groups(Offline(3, 8, 3), Offline(7, 8, 3, mass: 0)).Count == 0,
        "docked to something of unknown mass"
    );
    Check(
        Groups(Offline(3, 8, 3), Offline(7, 8, 3, owners: [5])).Single().Grid
            is { Owner: 5, Flags: ContractGridFlags.Unpowered },
        "any big owner makes it someone's"
    );

    // In the book
    var book = new ContractBook(goneAfter)
    {
        Offline = new()
        {
            [1] = t0,
            [2] = t0,
            [3] = t0,
        },
        OfflineRows = Groups(Offline(1, 10, 1), Offline(2, 10, 2, owners: [5])),
    };
    // A row has no value: it is priced by what a reported grid is worth per kg
    book.Report("n1", [Row(50, ContractGridFlags.None, 10, 50_000)], t0);
    Check(
        Hunted(book, t0).Select(r => r.Id).SequenceEqual([1L]),
        "a row of nobody's is a wreck at once, a player's only once they are away"
    );
    book.Rules.OwnerAway = o => o == 5;
    Check(Hunted(book, t0).Select(r => r.Id).Order().SequenceEqual([1L, 2L]), "they are away");
    book.Rules.OwnerAway = _ => false;

    // A report wins for the id, and for the group
    book.Offline = new()
    {
        [1] = t0,
        [3] = t0,
        [7] = t0,
    };
    book.OfflineRows = Groups(Offline(3, 2, 3), Offline(7, 8, 3));
    Check(book.Unreported().Single().Id == 7, "unreported");
    book.Report("n1", [Row(3, Dead, 100, 1100)], t0);
    Check(!book.Unreported().Any(), "a report on the group's other grid");
    Check(
        Hunted(book, t0).Select(r => r.Id).SequenceEqual([3L]),
        "one contract for the group, from the report"
    );

    // Posted, with the row in the book as Contracts.Wrecks does it: taken once, kept through a restart
    book = new ContractBook(goneAfter)
    {
        Offline = new() { [1] = t0 },
        OfflineRows = Groups(Offline(1, 10, 1, owners: [5])),
    };
    book.Report("n1", [Row(50, ContractGridFlags.None, 10, 50_000)], t0);
    book.Rules.OwnerAway = _ => true;
    var posted = Hunted(book, t0).Single();
    book.Grids.TryAdd(posted.Id, posted);
    book.Targets[70] = new ContractTarget('w', posted.Id, 1, posted.Blocks, posted.Position)
    {
        Owner = posted.Owner,
    };
    Check(!Hunted(book, t0).Any(), "not posted twice");
    Check(!book.IsGone(1, t0 + days), "not gone while it sleeps");
    var restarted = new ContractBook(goneAfter)
    {
        Offline = new() { [1] = t0 },
        OfflineRows = Groups(Offline(1, 10, 1, owners: [5])),
    };
    restarted.LoadTargets(book.SerializeTargets());
    restarted.LoadGrids(book.SerializeGrids(), t0 + days);
    restarted.Report("n1", [Row(50, ContractGridFlags.None, 10, 50_000)], t0);
    restarted.Rules.OwnerAway = _ => true;
    Check(!Hunted(restarted, t0).Any(), "nor after a restart");
    Check(
        restarted.Grids[1].Node == ""
            && restarted.Grids[1].Flags.HasFlag(ContractGridFlags.Unpowered),
        "its row comes back"
    );
    Check(
        restarted.Judge(restarted.Targets[70], false, alpha, false, 500, 500, t0 + days)
            == ContractVerdict.Keep,
        "and the contract stays"
    );
    restarted.Given[1] = 0;
    restarted.Grids.Remove(1);
    restarted.Targets.Clear();
    Check(!restarted.Unreported().Any(), "a given grid gets no other contract");

    // The partition wakes: the report tells what the row couldn't
    var woken = book.Targets[70];
    book.Offline = [];
    var report = Row(1, ContractGridFlags.Unpowered | ContractGridFlags.Seated, 100, 1100);
    report.Owner = 5;
    report.Builder = Hunter;
    book.Report("n1", [report], t0 + days);
    Check(
        book.Judge(woken, false, alpha, false, 500, 500, t0 + days) == ContractVerdict.Withdraw,
        "someone sits in it after all: withdrawn"
    );
    Check(woken.Builder == Hunter, "the builder a row can't tell, for who can't be paid");
}

Console.WriteLine($"{checks - failures}/{checks} contract math checks passed");
return failures == 0 ? 0 : 1;
