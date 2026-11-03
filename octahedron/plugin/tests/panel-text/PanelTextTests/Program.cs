using System;
using System.Linq;
using ServerPlugin;

// Checks the LCD text layout (ServerPlugin/PanelText.cs) on the shipyard queue panel's screen.
// Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/panel-text/PanelTextTests

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

// ShipyardOrders' queue panel layout: a LargeTextPanel is 512x307 on a 512 texture
const int Columns = 46;
const float Width = 512f,
    Height = 307.2f,
    Texture = 512f;

Check(PanelText.Wrap("a b c", 3) == "a b\nc", "wrapped at a space");
Check(PanelText.Wrap("abcdef", 3) == "abc\ndef", "a long word is cut");
Check(PanelText.Clip("aaa bbb ccc", 7, 1) == "aaa...", "cut after the last line with dots");
Check(PanelText.Clip("aaa bbb", 7, 1) == "aaa bbb", "not cut when it fits");
Check(
    PanelText.Item("1. ", "aaa bbb ccc", 10, 2) == "1. aaa bbb\n   ccc",
    "an item's second line is indented"
);

// The ticket's entry, which made the font 0.3 on one line
var entry = PanelText.Item(
    "1. ",
    "'large Miner 2024' (19,486 PCU) for Viktor: building, done by 01:39 UTC at the latest",
    Columns,
    2
);
var lines = entry.Split('\n');
Check(lines.Length == 2, "the ticket's entry takes two lines");
Check(!entry.Contains("..."), "the ticket's entry is not cut");
Check(lines.All(l => l.Length <= Columns), "the ticket's entry fits the columns");

// SE-962: a deadline shows the minutes left with its UTC time, rounded up and never below 0
var start = new DateTime(2026, 11, 3, 2, 38, 38, DateTimeKind.Utc);
var ends = start.AddMinutes(60);
Check(PanelText.Left(ends, start) == "60 min (by 03:38 UTC)", "60 min left at the start");
Check(
    PanelText.Left(ends, start.AddSeconds(1)) == "60 min (by 03:38 UTC)",
    "59 min 59 s rounds up to 60 min"
);
Check(PanelText.Left(ends, start.AddMinutes(59.5)) == "1 min (by 03:38 UTC)", "the last minute");
Check(
    PanelText.Left(ends, ends.AddMinutes(2)) == "0 min (by 03:38 UTC)",
    "0 min past the deadline"
);
var orderEntry = PanelText.Item(
    "1. ",
    $"'Polaris Mk2 RC8' (23,276 PCU) for Viktor: building, done within {PanelText.Left(ends, start.AddSeconds(30))}",
    Columns,
    2
);
var orderLines = orderEntry.Split('\n');
Check(orderLines.Length == 2, "SE-962's entry takes two lines");
Check(!orderEntry.Contains("..."), "SE-962's entry is not cut");
Check(orderLines.All(l => l.Length <= Columns), "SE-962's entry fits the columns");
Console.WriteLine(orderEntry);

// A full queue with the longest names, a waiting ship and the longest shipyard name
var name = new string('N', 32);
var full = "Gamma Station Shipyard queue\n\n";
for (int i = 1; i <= 6; i++)
    full +=
        PanelText.Item(
            $"{i}. ",
            $"'{name}' (100,000 PCU) for {name}: loading parts within 10 min (by 01:39 UTC)",
            Columns,
            2
        ) + "\n";
full += "\n" + PanelText.Clip($"Waiting for '{name}' to leave the shipyard.", Columns, 2);
var fullLines = full.Split('\n');
Check(fullLines.Length == 17, $"a full queue is 17 lines, not {fullLines.Length}");
Check(fullLines.All(l => l.Length <= Columns), "every line of a full queue fits the columns");
Check(full.Contains("..."), "long entries are cut");
var size = PanelText.FontSize(full, Width, Height, Texture, 0.7f);
Check(size > 0.55f, $"a full queue has font size {size:0.000}, more than 0.55");
var one = PanelText.FontSize(
    "Gamma Station Shipyard queue\n\n" + entry,
    Width,
    Height,
    Texture,
    0.7f
);
Check(one > 0.55f, $"one entry has font size {one:0.000}, more than 0.55");
Console.WriteLine($"font size: one entry {one:0.000}, a full queue {size:0.000}");
Console.WriteLine(full);

Console.WriteLine(
    failures == 0 ? $"PASS {checks} checks" : $"{failures} of {checks} checks FAILED"
);
return failures == 0 ? 0 : 1;
