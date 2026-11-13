using System;

namespace ServerPlugin;

/// <summary>What a ship docked at a station pays for hydrogen: the ice a gas generator would make it from, at the station's ice price (StationCharging).</summary>
public static class DockHydrogenMath
{
    /// <summary>
    /// The liters to put into the tanks and the whole SC they cost: the wanted liters' ice at the
    /// price (SC per kg, fractions allowed, litersPerIce liters per kg), rounded up to a whole SC. With less money than
    /// that, as many liters as the balance buys, for all of it. Nothing without a price or money.
    /// </summary>
    public static (double Liters, long Sc) Bill(
        double wanted,
        double icePrice,
        double litersPerIce,
        long balance
    )
    {
        if (wanted <= 0 || icePrice <= 0 || litersPerIce <= 0 || balance <= 0)
            return (0, 0);
        var sc = Math.Max(1, (long)Math.Ceiling(wanted / litersPerIce * icePrice - 1e-6));
        return sc <= balance ? (wanted, sc) : (balance * litersPerIce / icePrice, balance);
    }
}
