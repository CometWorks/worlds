using System;
using System.Linq;
using ServerPlugin;

// Checks the contract rewards, time limits and stock list (ServerPlugin/ContractMath.cs). Exit code 0 = all pass.
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
Check(ContractMath.AcquisitionAmount(20_000, 0, 0, 5_000) == 5_000, "capped at the max");
Check(ContractMath.AcquisitionAmount(20_000, 18_000, 0, 5_000) == 2_000, "the rest");
Check(ContractMath.AcquisitionAmount(20_000, 18_000, 2_000, 5_000) == 0, "pending covers it");
Check(ContractMath.AcquisitionAmount(20_000, 25_000, 0, 5_000) == 0, "more than enough");

// Iron ingots: minimal price 100 a unit, reference price 10 SC
Check(
    ContractMath.AcquisitionReward(5_000, 100, 100) == 50_000,
    "5 t of iron at the reference price"
);
Check(ContractMath.AcquisitionReward(5_000, 100, 60) == 30_000, "at 60 %");
Check(ContractMath.AcquisitionReward(1, 1, 100) == 1, "a tenth of a credit rounds up");

Check(ContractMath.BountyReward(250.4, 20) == 5_008, "bounty by threat");
Check(ContractMath.SalvageReward(12_500, 100) == 1_250, "salvage by tonne");

var stock = ContractMath.ParseStock(" Ingot/Iron=20000, Ore/Ice=5 ,");
Check(
    stock.Select(s => (s.Key, s.Value)).SequenceEqual([("Ingot/Iron", 20_000L), ("Ore/Ice", 5L)]),
    "stock list"
);
Check(ContractMath.ParseStock("").Count == 0, "an empty stock list");
foreach (
    var bad in new[] { "Iron=5", "Ingot/Iron", "Ingot/Iron=-5", "Ingot/Iron=0", "Ingot/Iron=1.5" }
)
{
    try
    {
        ContractMath.ParseStock(bad);
        Check(false, $"\"{bad}\" refused");
    }
    catch (FormatException)
    {
        checks++;
    }
}

Console.WriteLine($"{checks - failures}/{checks} contract math checks passed");
return failures == 0 ? 0 : 1;
