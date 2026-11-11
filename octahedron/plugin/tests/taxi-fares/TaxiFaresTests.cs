using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;
using Xunit;
using static ServerPlugin.TaxiFares;

// The Octahedron numbers: TaxiCallFare 1000, TaxiCallFarePerKm 100, TaxiCallRange 5 km; EarthLike at
// the origin, North Station 138.5 km above its north pole and its elevator's Earth end 62.8 km from
// its center (build_world.py ELEVATORS)
public class TaxiFaresTests
{
    private const double Range = 5000;
    private const long Base = 1000;
    private const long PerKm = 100;
    private static readonly (double X, double Y, double Z) Earth = (0, 0, 0);
    private static readonly (double X, double Y, double Z) North = (0, 138_500, 0);
    private static readonly (double X, double Y, double Z) Alpha = (300_000, 0, 0);
    private static readonly (double X, double Y, double Z) NorthEnd = ElevatorEnd(
        North,
        Earth,
        62_800
    );

    private static double Dist(
        (double X, double Y, double Z) a,
        (double X, double Y, double Z) b
    ) =>
        Math.Sqrt(
            (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z)
        );

    // A call from the point: the stations and the North elevator's Earth end are the gates
    private static (string Station, long Fare) Call((double X, double Y, double Z) at) =>
        CallRide(
            [
                ("North Station", Dist(at, North)),
                ("Alpha Station", Dist(at, Alpha)),
                ("North Station", Dist(at, NorthEnd)),
            ],
            Range,
            Base,
            PerKm
        );

    [Fact]
    public void ElevatorEndIsOnTheLineToTheStation()
    {
        Assert.Equal((0.0, 62_800.0, 0.0), NorthEnd);
        var end = ElevatorEnd((10, 20, 50), (10, 20, 30), 5);
        Assert.Equal((10.0, 20.0, 35.0), end);
    }

    [Fact]
    public void ElevatorEndOfAStationAtTheCenterIsTheCenter() =>
        Assert.Equal((1.0, 2.0, 3.0), ElevatorEnd((1, 2, 3), (1, 2, 3), 100));

    [Theory]
    [InlineData(0, 1000)] // at the Earth end
    [InlineData(-1_013, 1000)] // on the ground under it (61,787 m from the center)
    [InlineData(4_999, 1000)]
    [InlineData(5_000, 1000)]
    [InlineData(5_999, 1000)] // not a whole km beyond the range yet
    [InlineData(6_000, 1100)]
    [InlineData(15_500, 2000)] // 10.5 km beyond
    [InlineData(-25_000, 3000)] // 20 km beyond, down toward the center
    public void CallNearTheElevatorEndGoesUpIt(double fromEnd, long fare)
    {
        var at = (0.0, 62_800 + fromEnd, 0.0);
        Assert.Equal(("North Station", fare), Call(at));
    }

    [Fact]
    public void CallBesideTheElevatorEndCountsTheStraightDistance() =>
        // 8 km to the side of it: 3 km beyond the range
        Assert.Equal(("North Station", 1300L), Call((8_000, 62_800, 0)));

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(5_000, 1000)]
    [InlineData(5_999, 1000)]
    [InlineData(7_000, 1200)]
    [InlineData(25_000, 3000)]
    public void CallNearAStationGoesThere(double fromStation, long fare) =>
        Assert.Equal(("Alpha Station", fare), Call((300_000, 0, fromStation)));

    [Fact]
    public void TheNearestGateCounts()
    {
        // 30 km below North Station, 45.7 km above the Earth end: the station is nearer
        Assert.Equal(("North Station", 3500L), Call((0, 108_500, 0)));
        // a third of the way from EarthLike to Alpha Station: the Earth end is nearer
        var at = (100_000.0, 0.0, 0.0);
        Assert.True(Dist(at, NorthEnd) < Dist(at, Alpha));
        Assert.Equal("North Station", Call(at).Station);
    }

    [Fact]
    public void NoGatesNoStation() =>
        Assert.Equal(((string)null, 1000L), CallRide([], Range, Base, PerKm));

    [Fact]
    public void FareGrowsWithDistance()
    {
        var fares = Enumerable
            .Range(0, 200)
            .Select(i => Call((300_000, 0, i * 500.0)).Fare)
            .ToList();
        Assert.Equal(fares.OrderBy(f => f), fares);
        Assert.All(fares, f => Assert.True(f >= Base));
    }
}
