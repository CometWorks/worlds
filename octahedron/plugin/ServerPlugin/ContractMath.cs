using System;
using System.Collections.Generic;
using System.Globalization;

namespace ServerPlugin;

// The contract rules that need no game: rewards, time limits and the stock list, so the unit
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

    // What one more acquisition contract asks for: the stock missing at the station, less what open
    // and accepted contracts bring already, at most max. 0: none needed.
    public static int AcquisitionAmount(long target, long have, long pending, int max) =>
        (int)Math.Max(0, Math.Min(max, target - have - pending));

    // The reference price is the game's minimal price per unit divided by 10
    public static int AcquisitionReward(int amount, long minimalPricePerUnit, int percent) =>
        Sc(amount * minimalPricePerUnit / 10.0 * percent / 100);

    public static int BountyReward(double threat, double perThreat) => Sc(threat * perThreat);

    public static int SalvageReward(double massKg, long perTonne) => Sc(massKg / 1000 * perTonne);

    // "Ingot/Iron=20000,Ore/Ice=5000": item and amount pairs; throws FormatException on a bad entry
    public static List<KeyValuePair<string, long>> ParseStock(string text)
    {
        var stock = new List<KeyValuePair<string, long>>();
        foreach (var entry in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=');
            if (
                pair.Length != 2
                || pair[0].Trim().Split('/').Length != 2
                || !long.TryParse(
                    pair[1].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long amount
                )
                || amount <= 0
            )
                throw new FormatException($"Bad stock entry \"{entry.Trim()}\"");
            stock.Add(new KeyValuePair<string, long>(pair[0].Trim(), amount));
        }
        return stock;
    }
}
