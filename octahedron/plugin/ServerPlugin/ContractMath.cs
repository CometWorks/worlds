using System;

namespace ServerPlugin;

// The contract rules that need no game: rewards and time limits, so the unit
// tests (tests/contracts/ContractTests) check them without a server
internal static class ContractMath
{
    public enum TowResult
    {
        Take,

        // Taken, and the player is over their limit
        TakeOverLimit,

        // Over the limit of a ship
        TooBig,

        // More than 10 % over the player's limit
        OverLimit,
    }

    /// <summary>
    /// A grid of pcu towed in by a player with used PCU, against their limit and a ship's. A limit of
    /// zero or less, or int.MaxValue for the player's, is no limit.
    /// </summary>
    public static TowResult Tow(int pcu, int used, int limit, int shipLimit)
    {
        if (shipLimit > 0 && pcu > shipLimit)
            return TowResult.TooBig;
        long after = (long)used + pcu;
        if (limit <= 0 || limit == int.MaxValue || after <= limit)
            return TowResult.Take;
        return after * 10 <= limit * 11L ? TowResult.TakeOverLimit : TowResult.OverLimit;
    }

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

    // The market's reference price is in milli-SC per unit. Each unit pays shortagePercent more at
    // an empty stock, less as the stock nears its target: the further below, the better it pays.
    public static int AcquisitionReward(
        int amount,
        long milliPrice,
        int percent,
        long missing = 0,
        long target = 0,
        int shortagePercent = 0
    ) =>
        Sc(
            amount
                * (milliPrice / 1000.0)
                * percent
                / 100
                * (1 + shortagePercent / 100.0 * Shortage(missing, target))
        );

    // How far the stock is below its target: 0 at the target, 1 when out
    public static double Shortage(long missing, long target) =>
        target <= 0 ? 0 : Math.Clamp((double)missing / target, 0, 1);

    public static int BountyReward(double threat, double perThreat) => Sc(threat * perThreat);

    // A wreck's or area's components' value in SC, at percent
    public static int WreckReward(double value, int percent) => Sc(value * percent / 100);
}
