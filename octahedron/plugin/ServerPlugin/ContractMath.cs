using System;

namespace ServerPlugin;

// The contract rules that need no game: rewards and time limits, so the unit
// tests (tests/contracts/ContractTests) check them without a server
internal static class ContractMath
{
    // Rewards are an int in the game's contracts
    private static int Sc(double amount) => (int)Math.Min(int.MaxValue, Math.Ceiling(amount));

    public static int HaulingReward(double km, long baseReward, long perKm) =>
        Sc(baseReward + km * perKm);

    // About 3 km a minute, a slow hauler's 50 m/s, on top of the base
    public static int HaulingMinutes(double km, int baseMinutes) =>
        baseMinutes + (int)Math.Ceiling(km / 3);

    // What one more acquisition contract asks for: the units missing at the station, less what
    // open and accepted contracts bring already, at most max. 0: none needed.
    public static int AcquisitionAmount(long missing, long pending, int max) =>
        (int)Math.Max(0, Math.Min(max, missing - pending));

    // The market's reference price is in milli-SC per unit
    public static int AcquisitionReward(int amount, long milliPrice, int percent) =>
        Sc(amount * (milliPrice / 1000.0) * percent / 100);

    public static int BountyReward(double threat, double perThreat) => Sc(threat * perThreat);

    public static int SalvageReward(double massKg, long perTonne) => Sc(massKg / 1000 * perTonne);
}
