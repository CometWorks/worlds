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

// The nodes' totals add up per faction
var sum = LeaderboardMath.Sum([
    new Dictionary<long, FactionGrids>
    {
        [1] = new() { Value = 100, Pcu = 10 },
        [2] = new() { Value = 5, Pcu = 1 },
    },
    new Dictionary<long, FactionGrids>
    {
        [1] = new() { Value = 50, Pcu = 7 },
    },
]);
Check(sum.Count == 2, "two factions");
Check(sum[1].Value == 150 && sum[1].Pcu == 17, "faction 1 over both nodes");
Check(sum[2].Value == 5 && sum[2].Pcu == 1, "faction 2 on one node");
Check(LeaderboardMath.Sum([]).Count == 0, "no reports, nothing");

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

// Wire formats round-trip; tabs and new lines in names can't break a record
var prices = new Dictionary<string, long> { ["Ingot/Iron"] = 10_000, ["Component/SteelPlate"] = 2 };
Check(
    LeaderboardMath
        .DecodePrices(LeaderboardMath.EncodePrices(prices))
        .OrderBy(p => p.Key)
        .SequenceEqual(prices.OrderBy(p => p.Key)),
    "prices round-trip"
);
var grids = new Dictionary<long, FactionGrids>
{
    [144115188075855873] = new() { Value = long.MaxValue, Pcu = 3 },
    [-5] = new() { Value = 0, Pcu = 0 },
};
var decoded = LeaderboardMath.DecodeGrids(LeaderboardMath.EncodeGrids(grids));
Check(
    decoded.Count == 2
        && decoded[144115188075855873].Value == long.MaxValue
        && decoded[144115188075855873].Pcu == 3
        && decoded.ContainsKey(-5),
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
