using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks the leaderboard's arithmetic, ranking, text and wire formats (ServerPlugin/LeaderboardMath.cs).
// Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/leaderboard/LeaderboardTests

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

const long Unit = Amounts.Unit;

// Values: raw amounts at milli-SC per unit
Check(
    LeaderboardMath.Value(10 * Unit, 14_000) == 140_000m,
    "10 units at 14 SC are 140,000 milli-SC"
);
Check(LeaderboardMath.Value(Unit / 2, 1_000) == 500m, "half a unit");
Check(
    LeaderboardMath.Value(long.MaxValue, 1_000_000_000_000) > 0,
    "no overflow with the largest amount at a trillion SC"
);
Check(LeaderboardMath.Sc(1_999m) == 1, "milli-SC rounded down to SC");
Check(LeaderboardMath.Sc(decimal.MaxValue) == long.MaxValue, "SC at most long.MaxValue");

// The middle of OCT's bid and ask, averaged over the stations that trade the item
Check(LeaderboardMath.MidPrice([(6_000, 14_000)]) == 10_000, "the mid price of one station");
Check(
    LeaderboardMath.MidPrice([(6_000, 14_000), (12_000, 28_000)]) == 15_000,
    "averaged over two stations"
);
Check(
    LeaderboardMath.MidPrice([(6_000, 14_000), (0, 0), (0, 14_000)]) == 10_000,
    "stations not trading the item are left out"
);
Check(LeaderboardMath.MidPrice([]) == 0, "no station: no price");
Check(LeaderboardMath.MidPrice([(1, 2)]) == 2, "rounded to the nearest milli-SC");

// The grids add up per faction, or per OCT player, by their owners
LeaderboardGrid G(long id, long owner, long milli, long pcu) =>
    new()
    {
        Id = id,
        Owner = owner,
        Milli = milli,
        Pcu = pcu,
    };
long? KeyOf(long owner) => owner < 0 ? null : owner / 10;
var sum = LeaderboardMath.Sum(
    [G(1, 10, 100_999, 10), G(2, 20, 5_000, 1), G(3, 11, 50_001, 7), G(4, -1, 9_000, 9)],
    KeyOf
);
Check(sum.Count == 2, "two factions");
Check(sum[1].Value == 151 && sum[1].Pcu == 17, "faction 1 over two owners, rounded down once");
Check(sum[2].Value == 5 && sum[2].Pcu == 1, "faction 2");
Check(LeaderboardMath.Sum([], KeyOf).Count == 0, "no grids, nothing");
Check(LeaderboardMath.Milli(1_234.9m) == 1_234, "a grid's value in whole milli-SC");
Check(LeaderboardMath.Milli(decimal.MaxValue) == long.MaxValue, "at most long.MaxValue");

// The World Authority's book of grids (SE-1270)
var t0 = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
var interval = TimeSpan.FromMinutes(10);
LeaderboardBook NewBook() =>
    new()
    {
        GoneAfter = 2 * interval,
        SilentAfter = 3 * interval,
        Offline = new Dictionary<long, DateTime>(),
    };
List<long> Ids(LeaderboardBook b, DateTime now) =>
    b.Current(now, out _).Select(g => g.Id).OrderBy(i => i).ToList();

var book = NewBook();
book.Report("node-1", [G(1, 10, 1_000, 1), G(2, 20, 2_000, 2)], t0);
book.Report("node-2", [G(3, 30, 3_000, 3)], t0);
Check(Ids(book, t0).SequenceEqual([1L, 2L, 3L]), "every node's grids count");

// Grid 1 moves to node-2: it counts once, with node-2's count
book.Report("node-2", [G(3, 30, 3_000, 3), G(1, 10, 1_500, 1)], t0 + interval);
var moved = book.Current(t0 + interval, out _);
Check(
    moved.Count(g => g.Id == 1) == 1 && moved.Single(g => g.Id == 1).Milli == 1_500,
    "a grid that moved counts once, as its new node counted it"
);

// Grid 2's partition goes offline: node-1 reports without it, the gateway lists it
book.Report("node-1", [], t0 + 2 * interval);
book.Offline = new Dictionary<long, DateTime> { [2] = t0 + 2 * interval };
var later = t0 + 10 * interval;
for (var at = t0 + 3 * interval; at <= later; at += interval)
{
    book.Report("node-1", [], at);
    book.Report("node-2", [G(3, 30, 3_000, 3), G(1, 10, 1_500, 1)], at);
}
var offline = book.Current(later, out int inOffline);
Check(
    inOffline == 1 && offline.Single(g => g.Id == 2).Milli == 2_000,
    "a grid in an offline partition counts as last counted, long after"
);

// It wakes on node-2: once node-2 counts it, its new count counts, once
book.Offline = new Dictionary<long, DateTime>();
var woke = later + interval;
book.Report("node-2", [G(3, 30, 3_000, 3), G(1, 10, 1_500, 1), G(2, 20, 2_500, 2)], woke);
var awake = book.Current(woke, out inOffline);
Check(
    inOffline == 0
        && awake.Count(g => g.Id == 2) == 1
        && awake.Single(g => g.Id == 2).Milli == 2_500,
    "a woken grid counts once, as its node counts it now"
);

// Destroyed: node-2 reports without grid 3; it counts until GoneAfter, then it is forgotten
book.Report("node-2", [G(1, 10, 1_500, 1), G(2, 20, 2_500, 2)], woke + interval);
Check(
    Ids(book, woke + interval).Contains(3),
    "a missing grid still counts for a while: a handover"
);
book.Report("node-2", [G(1, 10, 1_500, 1), G(2, 20, 2_500, 2)], woke + 2 * interval);
Check(!Ids(book, woke + 2 * interval).Contains(3), "gone after GoneAfter of reports without it");
Check(book.Count == 2, "and forgotten");

// A node that stopped reporting: its grids count until SilentAfter
var silent = NewBook();
silent.Report("node-9", [G(7, 70, 1, 1)], t0);
Check(Ids(silent, t0 + 2 * interval).Contains(7), "a silent node's grid counts for a while");
Check(!Ids(silent, t0 + 3 * interval).Contains(7), "not after SilentAfter");

// The offline partitions not known yet: nothing is forgotten
var unknown = NewBook();
unknown.Offline = null;
unknown.Report("node-1", [G(8, 80, 1, 1)], t0);
unknown.Report("node-1", [], t0 + 3 * interval);
Check(
    Ids(unknown, t0 + 3 * interval).Count == 0,
    "unknown offline partitions: a missing grid doesn't count"
);
Check(unknown.Count == 1, "but it is kept");
unknown.Offline = new Dictionary<long, DateTime> { [8] = t0 };
Check(Ids(unknown, t0 + 3 * interval).SequenceEqual([8L]), "and counts once the gateway lists it");

// A plain server: its count is the whole world
var plain = NewBook();
plain.GoneAfter = TimeSpan.Zero;
plain.Report("local", [G(1, 10, 1, 1), G(2, 20, 1, 1)], t0);
plain.Report("local", [G(1, 10, 1, 1)], t0 + interval);
Check(Ids(plain, t0 + interval).SequenceEqual([1L]), "a plain server forgets a grid at once");

// Saved in the world and loaded back: every grid seen now, on its last node
var saved = NewBook();
saved.Report("node\t1", [G(144115188075855873, -5, long.MaxValue, 3)], t0);
var loaded = NewBook();
loaded.Load(saved.Serialize(), later);
var back = loaded.Current(later, out _);
Check(
    back.Count == 1
        && back[0].Id == 144115188075855873
        && back[0].Owner == -5
        && back[0].Milli == long.MaxValue
        && back[0].Pcu == 3,
    "the book round-trips"
);
loaded.Report("node 1", [], later + interval);
Check(
    loaded.Current(later + interval, out _).Count == 1,
    "a loaded grid counts until its node reports without it for GoneAfter"
);
loaded.Report("node 1", [], later + 2 * interval);
Check(loaded.Current(later + 2 * interval, out _).Count == 0, "and then it is gone");
var empty = NewBook();
empty.Load("", t0);
Check(empty.Count == 0, "an empty book");

// Richest first, then by PCU, then by tag
LeaderboardRow Row(string tag, long cash, long grids, long pcu) =>
    new()
    {
        Tag = tag,
        Name = tag + " Faction",
        Cash = cash,
        Grids = grids,
        Pcu = pcu,
    };
var ranked = LeaderboardMath.Rank([
    Row("CCC", 100, 0, 0),
    Row("AAA", 0, 300, 50),
    Row("BBB", 200, 100, 60),
    Row("DDD", 100, 0, 5),
    Row("EEE", 100, 0, 5),
]);
Check(
    ranked.Select(r => r.Tag).SequenceEqual(["BBB", "AAA", "DDD", "EEE", "CCC"]),
    "ranked: " + string.Join(",", ranked.Select(r => r.Tag))
);
Check(ranked[0].Total == 300, "the total is cash plus grids");

// The board's text
var updated = new DateTime(2026, 10, 25, 20, 31, 0, DateTimeKind.Utc);
var board = LeaderboardMath.Format(
    [
        Row("BIG", 9_000_000_000, 999_999_999, 1_234_567),
        new LeaderboardRow
        {
            Tag = "LONGTAG",
            Name = "A Very Long Faction Name",
            Cash = 25_000,
            Pcu = 0,
        },
    ],
    15,
    updated
);
var lines = board.Split('\n');
Check(lines[0].Trim() == "FACTION LEADERBOARD", "title: " + lines[0]);
Check(lines[2] == " #  Tag   Faction            Value SC     PCU", "header: " + lines[2]);
Check(lines[3] == " 1  BIG   BIG Faction   9,999,999,999 1,234,567", "first row: " + lines[3]);
Check(lines[4] == " 2  LONGT A Very Long F        25,000       0", "second row cut: " + lines[4]);
Check(board.Contains("Updated 20:31 UTC."), "updated time");
Check(
    lines.Where(l => l != lines[3]).All(l => l.Length <= LeaderboardMath.Width),
    "every line but the 10-digit row fits"
);
Check(lines[3].Length == LeaderboardMath.Width + 2, "an overlong number widens its row only");
Check(lines[2].Length == LeaderboardMath.Width, "the header is the board's width");

var many = Enumerable.Range(1, 30).Select(i => Row($"T{i:00}", 1000 - i, 0, i)).ToList();
var limited = LeaderboardMath.Format(many, 15, updated);
Check(limited.Contains("15  T15") && !limited.Contains("T16"), "only the first rows are shown");
Check(
    LeaderboardMath.Format([], 15, updated).Contains("No factions yet"),
    "an empty board says so"
);
Check(
    LeaderboardMath
        .Format([], 15, updated, players: true)
        .StartsWith("LEADERBOARD".PadLeft((LeaderboardMath.Width + 11) / 2)),
    "a board with OCT's players is no faction board"
);

// Wire formats round-trip; tabs and new lines in names can't break a record
var prices = new Dictionary<string, long> { ["Ingot/Iron"] = 10_000, ["Component/SteelPlate"] = 2 };
Check(
    LeaderboardMath
        .DecodePrices(LeaderboardMath.EncodePrices(prices))
        .OrderBy(p => p.Key)
        .SequenceEqual(prices.OrderBy(p => p.Key)),
    "prices round-trip"
);
var decoded = LeaderboardMath.DecodeGrids(
    LeaderboardMath.EncodeGrids([G(144115188075855873, 7, long.MaxValue, 3), G(-5, 0, 0, 0)])
);
Check(
    decoded.Count == 2
        && decoded[0].Id == 144115188075855873
        && decoded[0].Owner == 7
        && decoded[0].Milli == long.MaxValue
        && decoded[0].Pcu == 3
        && decoded[1].Id == -5,
    "grids round-trip"
);
Check(LeaderboardMath.DecodeGrids("").Count == 0, "no grids");
var rows = LeaderboardMath.DecodeRows(
    LeaderboardMath.EncodeRows([Row("A\tB", 1, 2, 3), Row("C", 4, 5, 6)])
);
Check(rows.Count == 2, "rows round-trip: count");
Check(rows[0].Tag == "A B" && rows[0].Name == "A\tB Faction".Replace('\t', ' '), "tabs cleaned");
Check(rows[1].Cash == 4 && rows[1].Grids == 5 && rows[1].Pcu == 6, "rows round-trip: numbers");
var named = LeaderboardMath.DecodeRows(
    LeaderboardMath.EncodeRows([new LeaderboardRow { Tag = "X", Name = "Two\nLines" }])
);
Check(named.Count == 1 && named[0].Name == "Two Lines", "new lines cleaned");

Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
