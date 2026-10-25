using System;
using ServerPlugin;

// Checks the contract rewards and time limits (ServerPlugin/ContractMath.cs). Exit code 0 = all pass.
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

Console.WriteLine($"{checks - failures}/{checks} contract math checks passed");
return failures == 0 ? 0 : 1;
