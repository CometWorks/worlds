using System;

namespace ServerPlugin;

/// <summary>What a ship docked at a station pays for hydrogen: the ice a gas generator would make it from, at the station's ice price (StationCharging), and when a docked ship's refill is due (StationCharging, Police).</summary>
public static class DockHydrogenMath
{
    /// <summary>A player's ship gets its tanks filled once they are this share short of full.</summary>
    public const double PlayerShort = 0.01;

    /// <summary>
    /// The bots' service ships (police, taxis, janitors, traders) are refilled only below this share of full: H2, O2,
    /// ammo, anything the plugin tops up. A ship that burns its fuel while docked would otherwise be refilled, and
    /// billed through the World Authority, every few seconds (SE-1523).
    /// </summary>
    public const double ServiceBelow = 0.9;

    /// <summary>Whether a refill is due: 'have' of 'full' (liters, items), for a bot's service ship or a player's.</summary>
    public static bool Due(double have, double full, bool service) =>
        full > 0 && have < full * (service ? ServiceBelow : 1 - PlayerShort);

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
