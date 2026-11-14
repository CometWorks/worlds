using System;

namespace ServerPlugin;

/// <summary>What a ship docked at a station pays for H2 and O2: the ice a gas generator would make them from, at the station's ice price (StationCharging), and when a docked ship's refill is due (StationCharging, Police).</summary>
public static class DockGasMath
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
    /// The liters of H2 and O2 to put into the tanks and the whole SC they cost. A gas generator makes
    /// both from the same ice (h2PerIce and o2PerIce liters per kg), so the bill is the ice the gas
    /// needing more of it takes, at the price (SC per kg, fractions allowed), rounded up to a whole SC.
    /// With less money than that, the ice the balance buys, for all of it, and of each gas what that
    /// ice makes, up to what is wanted. Nothing without a price or money.
    /// </summary>
    public static (double H2, double O2, long Sc) Bill(
        double h2Wanted,
        double o2Wanted,
        double h2PerIce,
        double o2PerIce,
        double icePrice,
        long balance
    )
    {
        var ice = Math.Max(
            h2Wanted > 0 && h2PerIce > 0 ? h2Wanted / h2PerIce : 0,
            o2Wanted > 0 && o2PerIce > 0 ? o2Wanted / o2PerIce : 0
        );
        if (ice <= 0 || icePrice <= 0 || balance <= 0)
            return (0, 0, 0);
        var sc = Math.Max(1, (long)Math.Ceiling(ice * icePrice - 1e-6));
        if (sc <= balance)
            return (Math.Max(0, h2Wanted), Math.Max(0, o2Wanted), sc);
        ice = balance / icePrice;
        return (
            Math.Clamp(ice * h2PerIce, 0, Math.Max(0, h2Wanted)),
            Math.Clamp(ice * o2PerIce, 0, Math.Max(0, o2Wanted)),
            balance
        );
    }
}
