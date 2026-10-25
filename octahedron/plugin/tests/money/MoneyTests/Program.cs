using System;
using System.Linq;
using ServerPlugin;

// Checks the arithmetic of the daily tax and the hourly payout (ServerPlugin/MoneyMath.cs). Exit code 0 = all pass.
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

// The day's tax: three players, one rich, one rich faction
var (taxes, total) = MoneyMath.Taxes(
    [(1, 25_000, 200_000), (2, 1_200_000, 200_000), (3, 0, 200_000), (10, 3_000_000, 1_000_000)],
    100
);
Check(taxes.SequenceEqual([(2L, 10_000L), (10L, 20_000L)]), "taxed accounts and amounts");
Check(total == 30_000, "tax total");

// The first hour of the day releases a 24th of the held tax, OCT's excess and the subsidy
var first = MoneyMath.Pay(
    held: 30_000,
    hoursLeft: 24,
    octBalance: 20_500_000,
    octCap: 20_000_000,
    dailySubsidyPerPlayer: 5_000,
    recipients: [1, 2, 3]
);
Check(first.Tax == 1_250, "a 24th of the held tax");
Check(first.OctExcess == 500_000, "OCT above its cap");
Check(first.Subsidy == 625, "a 24th of the daily subsidy per active player");
Check(first.Pool == 501_875, "pool");
Check(first.Share == 167_291 && first.Rest == 2, "equal shares, the rest stays held");
Check(first.Share * 3 + first.Rest == first.Pool, "nothing lost or made in the split");

// The last hour releases everything still held
var last = MoneyMath.Pay(1_234, 1, 0, 20_000_000, 5_000, [7]);
Check(last.Tax == 1_234 && last.Subsidy == 208 && last.Share == 1_442, "the last hour");

// Uneven subsidies round down over all players, not per player
Check(MoneyMath.Pay(0, 24, 0, 0, 5_000, [1, 2, 3]).Subsidy == 625, "5,000 x 3 / 24");
Check(MoneyMath.Pay(0, 24, 0, 0, 100, [1, 2, 3]).Subsidy == 12, "100 x 3 / 24 = 12.5");

// Nobody active: nothing paid, OCT keeps its excess, the tax stays held
var empty = MoneyMath.Pay(30_000, 24, 25_000_000, 20_000_000, 5_000, []);
Check(empty.Pool == 0 && empty.Tax == 0 && empty.OctExcess == 0, "no recipients");

// OCT under its cap gives nothing
var poor = MoneyMath.Pay(0, 24, 2_000_000, 20_000_000, 4_800, [7]);
Check(poor.OctExcess == 0 && poor.Pool == 200 && poor.Share == 200, "OCT under its cap");

// An overflowing pool throws instead of wrapping around
try
{
    MoneyMath.Pay(long.MaxValue, 1, long.MaxValue, 0, 0, [1]);
    Check(false, "overflow throws");
}
catch (OverflowException)
{
    Check(true, "overflow throws");
}
try
{
    MoneyMath.Taxes([(1, long.MaxValue, 0), (2, long.MaxValue, 0)], 10_000);
    Check(false, "tax total overflow throws");
}
catch (OverflowException)
{
    Check(true, "tax total overflow throws");
}

// Hangar's fee for another station: 1 SC per 10 PCU, rounded up
Check(MoneyMath.RetrieveFee(60_000, 1) == 6_000, "60k PCU costs 6,000 SC");
Check(MoneyMath.RetrieveFee(1, 1) == 1 && MoneyMath.RetrieveFee(11, 1) == 2, "rounded up");
Check(MoneyMath.RetrieveFee(10, 3) == 3, "3 SC per 10 PCU");
Check(MoneyMath.RetrieveFee(0, 1) == 0 && MoneyMath.RetrieveFee(500, 0) == 0, "free");
Check(MoneyMath.RetrieveFee(int.MaxValue, 1) == 214_748_365, "no overflow at int.MaxValue PCU");

Console.WriteLine(failures == 0 ? $"PASS {checks} checks" : $"FAIL {failures} of {checks} checks");
return failures == 0 ? 0 : 1;
