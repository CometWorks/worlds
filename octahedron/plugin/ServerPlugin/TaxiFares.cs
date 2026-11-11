using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerPlugin;

/// <summary>
/// The fare of a ride called with "!oct taxi call" (SE-1358). The cabs serve the stations and the Earth ends
/// of the space elevators, which lead up to their stations. A caller within the call range of the nearest of
/// these pays the base fare; one further out pays the per km fee on top for each whole km beyond that range.
/// The ride goes to that station, or up that elevator to its station. No game types, so tests run it offline.
/// </summary>
public static class TaxiFares
{
    /// <summary>The ride to the nearest gate's station and its fare; no station when there are no gates.</summary>
    public static (string Station, long Fare) CallRide(
        IEnumerable<(string Station, double Distance)> gates,
        double range,
        long baseFare,
        long perKm
    )
    {
        var nearest = gates.OrderBy(g => g.Distance).FirstOrDefault();
        if (nearest.Station == null)
            return (null, baseFare);
        var km = (long)Math.Floor(Math.Max(0, nearest.Distance - range) / 1000);
        return (nearest.Station, baseFare + perKm * km);
    }

    /// <summary>
    /// A space elevator's Earth end: on the line from the planet's center to the elevator's station, radius m
    /// from that center (build_world.py ELEVATORS).
    /// </summary>
    public static (double X, double Y, double Z) ElevatorEnd(
        (double X, double Y, double Z) station,
        (double X, double Y, double Z) center,
        double radius
    )
    {
        var (x, y, z) = (station.X - center.X, station.Y - center.Y, station.Z - center.Z);
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length == 0)
            return center;
        var k = radius / length;
        return (center.X + x * k, center.Y + y * k, center.Z + z * k);
    }
}
