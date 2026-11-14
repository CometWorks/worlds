using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks the reputation book (ServerPlugin/ReputationBook.cs): changes, the forgiven ram, the
// countdown to removal with its warnings, the save and the nodes' wire format.
// Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/reputation/ReputationTests

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

const long A = 101,
    B = 102,
    C = 103;
var settings = new ReputationSettings
{
    Start = 10_000,
    KickBelow = -10_000,
    KickAfterSeconds = 3600,
    WarnSeconds = ReputationSettings.ParseWarnings("300, 1800,600,x,-5"),
    RamGraceSeconds = 3600,
};
Check(settings.WarnSeconds.SequenceEqual([1800.0, 600, 300]), "warnings parsed, largest first");

var book = new ReputationBook { Settings = settings };
Check(book.Get(A) == 10_000, "everyone starts at 10,000");

// Damage, deposits and shares add up, the first time below zero is told once
Check(book.Apply(A, ReputationKind.Damage, -4_000, null, 0) == null, "above zero: nothing told");
Check(book.Apply(A, ReputationKind.Deposit, 1_000, null, 0) == null, "a deposit");
Check(book.Get(A) == 7_000, "10,000 - 4,000 + 1,000");
var told = book.Apply(A, ReputationKind.Damage, -7_500, null, 0);
Check(told != null && told.Contains("below zero"), "below zero is told");
Check(book.Apply(A, ReputationKind.Damage, -1, null, 0) == null, "below zero is told once");
Check(book.Apply(A, ReputationKind.Share, 600, null, 0) == null, "back above zero");
Check(
    book.Apply(A, ReputationKind.Damage, -200, null, 0) != null,
    "below zero again is told again"
);
Check(book.Dirty, "changes make the book dirty");

// The first ram in an hour is forgiven, all of it, the next one costs
var r = new ReputationBook { Settings = settings };
Check(
    r.Apply(B, ReputationKind.Ram, -500, "ram1", 1000)?.Contains("accident") == true,
    "first ram forgiven"
);
Check(
    r.Apply(B, ReputationKind.Ram, -500, "ram1", 1005) == null && r.Get(B) == 10_000,
    "the same ram stays forgiven"
);
r.Apply(B, ReputationKind.Ram, -300, "ram2", 1100);
Check(r.Get(B) == 9_700, "a second ram within the hour costs");
r.Apply(B, ReputationKind.Ram, -300, "ram3", 1000 + 3600);
Check(r.Get(B) == 9_700, "an hour later a ram is forgiven again");
var noGrace = new ReputationBook { Settings = new ReputationSettings { RamGraceSeconds = 0 } };
noGrace.Apply(B, ReputationKind.Ram, -300, "ram", 0);
Check(noGrace.Get(B) == 200_000 - 300, "no grace: every ram costs");

// The countdown: start, warnings at 30, 10 and 5 minutes left, removal at the end
var k = new ReputationBook { Settings = settings };
var inOct = new HashSet<long> { A, B };
var tell = new List<(long Identity, string Text)>();
var kick = new List<long>();
List<string> Run(double seconds)
{
    tell.Clear();
    kick.Clear();
    k.Tick(seconds, inOct.Contains, tell, kick);
    return tell.Where(t => t.Identity == A).Select(t => t.Text).ToList();
}
k.Apply(A, ReputationKind.Damage, -20_001, null, 0);
var start = Run(1);
Check(
    start.Count == 1 && start[0].Contains("60 minutes"),
    "the countdown starts and is told: " + string.Join("|", start)
);
Check(k.Left(A) == 3600, "60 minutes left at the start");
Check(Run(1799).Count == 0, "nothing told 1,801 s before the end");
var w30 = Run(1);
Check(
    w30.Count == 1 && w30[0].StartsWith("30 minutes left"),
    "30 minutes left: " + string.Join("|", w30)
);

// The countdown survives a save and a load
k = ReputationBook.Deserialize(k.Serialize());
k.Settings = settings;
Check(k.Get(A) == -10_001 && k.Left(A) == 1800, "the countdown is saved: " + k.Left(A));
Check(
    Run(1200)
        .SequenceEqual([
            "10 minutes left before you are removed from OCT for low reputation (-10,001, below -10,000).",
        ]),
    "10 minutes left"
);
Check(Run(300).Count == 1 && tell[0].Text.StartsWith("5 minutes left"), "5 minutes left");
Check(Run(299).Count == 0 && kick.Count == 0, "not removed a second early");
var end = Run(1);
Check(
    kick.SequenceEqual([A]) && end.Count == 1 && end[0].StartsWith("You were removed from OCT"),
    "removed at the end, with the reason: " + string.Join("|", end)
);
Check(
    Run(1).Count == 0 && kick.SequenceEqual([A]),
    "still in OCT a moment later: removed again, nothing told"
);
inOct.Remove(A);
Check(Run(10_000).Count == 0 && kick.Count == 0, "no longer a member: no countdown, nothing told");
Check(k.Get(A) == -10_001, "the reputation stays after the removal");

// Back above stops the countdown; down again starts it over in full
inOct.Add(A);
Run(1);
Run(1000);
k.Apply(A, ReputationKind.Deposit, 5_000, null, 0);
var back = Run(1);
Check(
    back.Count == 1 && back[0].Contains("back above") && k.Left(A) == null,
    "back above is told, the countdown stops"
);
k.Apply(A, ReputationKind.Damage, -5_000, null, 0);
Run(1);
Check(k.Left(A) == 3600, "below again: the full countdown again");
inOct.Remove(A);
Check(Run(1).Count == 0 && k.Left(A) == null, "leaving OCT stops the countdown silently");

// Short test timers: warnings longer than the countdown say nothing more than its start
var quick = new ReputationBook
{
    Settings = new ReputationSettings
    {
        Start = 10_000,
        KickBelow = -10_000,
        KickAfterSeconds = 20,
        WarnSeconds = ReputationSettings.ParseWarnings("1800,15,10,5"),
    },
};
quick.Apply(B, ReputationKind.Admin, -30_000, null, 0);
var said = new List<string>();
for (var i = 0; i <= 21 && kick.Count == 0; i++)
{
    tell.Clear();
    kick.Clear();
    quick.Tick(1, _ => true, tell, kick);
    said.AddRange(tell.Select(t => t.Text.Split(' ')[0] + t.Text.Split(' ')[1]));
}
Check(
    said.SequenceEqual(["Yourreputation", "15seconds", "10seconds", "5seconds", "Youwere"]),
    "a 20 s countdown warns at 15, 10 and 5 s, then removes: " + string.Join(",", said)
);

// The nodes' changes on the wire
var wire = ReputationBook.DecodeChanges(
    ReputationBook.EncodeChanges([
        (A, ReputationKind.Ram, "abc", -12.345m),
        (B, ReputationKind.Share, null, 7m),
    ])
);
Check(
    wire.Count == 2
        && wire[0] == (A, ReputationKind.Ram, "abc", -12.345m)
        && wire[1] == (B, ReputationKind.Share, null, 7m),
    "changes round-trip"
);
Check(ReputationBook.Deserialize("").Get(A) == 200_000, "an empty save: everyone starts over");
Check(
    ReputationBook.Sc(-10_000.6m) == "-10,001"
        && ReputationBook.Duration(3600) == "60 minutes"
        && ReputationBook.Duration(20) == "20 seconds",
    "text"
);

// OCT's leaders are never removed (SE-1495): a leader far below the line gets no countdown, and one
// running from before they were a leader stops without a word
var lead = new ReputationBook { Settings = settings };
lead.Apply(A, ReputationKind.Damage, -34_401, null, 0);
lead.Apply(B, ReputationKind.Damage, -34_401, null, 0);
var leaders = new HashSet<long> { A };
var leadKicks = new List<long>();
var leaderTold = false;
for (var i = 0; i < 7300; i++)
{
    tell.Clear();
    kick.Clear();
    lead.Tick(1, _ => true, tell, kick, leaders.Contains);
    leadKicks.AddRange(kick);
    leaderTold |= tell.Any(t => t.Identity == A);
}
Check(!leaderTold, "a leader is told nothing");
Check(!leadKicks.Contains(A) && lead.Left(A) == null, "a leader is never removed");
Check(leadKicks.Contains(B), "a member at the same reputation is removed");
Check(lead.Get(A) == -24_401, "a leader's reputation still changes");
leaders.Remove(A);
tell.Clear();
lead.Tick(1, _ => true, tell, kick, leaders.Contains);
Check(lead.Left(A) == 3600, "no longer a leader: the full countdown starts");
leaders.Add(A);
tell.Clear();
lead.Tick(1, _ => true, tell, kick, leaders.Contains);
Check(lead.Left(A) == null && tell.Count == 0, "a leader again: the countdown stops silently");

// SE-1495: the start is 200,000 and the line -200,000; a new start moves everyone by as much
var defaults = new ReputationSettings();
Check(
    defaults.Start == 200_000 && defaults.KickBelow == -200_000,
    "defaults: 200,000 and -200,000"
);
var old = ReputationBook.Deserialize($"{A} -24401.37 1000 1 1 0\n{B} 10000 -1 0 0 0");
old.Settings = defaults;
old.Rebase();
Check(
    old.Get(A) == 165_598.63m,
    "a save from before: -24,401 counted from 10,000 is 165,599: " + old.Get(A)
);
Check(old.Get(B) == 200_000, "untouched at the old start is the new start");
Check(old.Get(C) == 200_000, "a new member starts at 200,000");
Check(old.Dirty, "the move is saved");
tell.Clear();
kick.Clear();
old.Tick(1, _ => true, tell, kick);
Check(
    kick.Count == 0
        && old.Left(A) == null
        && tell.Any(t => t.Identity == A && t.Text.Contains("back above")),
    "a countdown running from before stops: " + string.Join("|", tell.Select(t => t.Text))
);
var saved = old.Serialize();
Check(saved.StartsWith("start 200000\n"), "the save keeps the start: " + saved.Split('\n')[0]);
var again = ReputationBook.Deserialize(saved);
again.Settings = defaults;
again.Rebase();
Check(again.Get(A) == 165_598.63m && !again.Dirty, "loaded again: no second move");
again.Settings = new ReputationSettings { Start = 100_000 };
again.Rebase();
Check(again.Get(A) == 65_598.63m && again.Get(B) == 100_000, "a lower start moves everyone down");
again.Apply(A, ReputationKind.Damage, -100_000, null, 0);
Check(
    ReputationBook.Deserialize(again.Serialize()).Get(A) == -34_401.37m,
    "the moved value is saved"
);

Console.WriteLine($"{checks - failures}/{checks} reputation checks passed");
return failures == 0 ? 0 : 1;
