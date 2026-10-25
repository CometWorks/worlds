using System;
using System.Linq;
using ServerPlugin;

// Checks the daily settlement's arithmetic (ServerPlugin/MoneyMath.cs). Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/money/MoneyTests

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

// 1 % of the part above the threshold, rounded down
Check(MoneyMath.Tax(200_000, 200_000, 100) == 0, "nothing at the threshold");
Check(MoneyMath.Tax(150_000, 200_000, 100) == 0, "nothing below it");
Check(MoneyMath.Tax(300_000, 200_000, 100) == 1_000, "1 % of 100k");
Check(MoneyMath.Tax(200_199, 200_000, 100) == 1, "1 % of 199 rounds down");
Check(MoneyMath.Tax(1_000_000, 0, 0) == 0, "no tax at 0 basis points");
Check(MoneyMath.Tax(1_000_000, 0, 10_000) == 1_000_000, "100 % takes everything above");
Check(MoneyMath.Tax(long.MaxValue, 0, 100) == long.MaxValue / 100, "no overflow at long.MaxValue");
Check(MoneyMath.Tax(long.MaxValue, 0, 10_000) == long.MaxValue, "100 % of long.MaxValue");

// The ticket's example day: three players, one rich, one rich faction, OCT over its cap
var day = MoneyMath.Settle(
    [(1, 25_000, 200_000), (2, 1_200_000, 200_000), (3, 0, 200_000), (10, 3_000_000, 1_000_000)],
    100,
    octBalance: 20_500_000,
    octCap: 20_000_000,
    subsidyPerPlayer: 5_000,
    recipients: [1, 2, 3]
);
Check(day.Taxes.SequenceEqual([(2L, 10_000L), (10L, 20_000L)]), "taxed accounts and amounts");
Check(day.TaxTotal == 30_000, "tax total");
Check(day.OctExcess == 500_000, "OCT above its cap");
Check(day.Subsidy == 15_000, "subsidy per active player");
Check(day.Pool == 545_000, "pool");
Check(day.Share == 181_666 && day.ToOct == 2, "equal shares, the rest to OCT");
Check(day.Share * 3 + day.ToOct == day.Pool, "nothing lost or made in the split");

// Nobody active: no subsidy, the whole pool goes to OCT
var empty = MoneyMath.Settle([(1, 300_000, 200_000)], 100, 1_000, 20_000_000, 5_000, []);
Check(empty.Subsidy == 0 && empty.Share == 0 && empty.ToOct == 1_000, "no recipients");

// OCT under its cap gives nothing
var poor = MoneyMath.Settle([], 100, 2_000_000, 20_000_000, 5_000, [7]);
Check(poor.OctExcess == 0 && poor.Pool == 5_000 && poor.Share == 5_000, "OCT under its cap");

// An overflowing pool throws instead of wrapping around
try
{
    MoneyMath.Settle([(1, long.MaxValue, 0)], 10_000, long.MaxValue, 0, 0, [1]);
    Check(false, "overflow throws");
}
catch (OverflowException)
{
    Check(true, "overflow throws");
}

Console.WriteLine(failures == 0 ? $"PASS {checks} checks" : $"FAIL {failures} of {checks} checks");
return failures == 0 ? 0 : 1;
