using ServerPlugin;
using Xunit;
using static ServerPlugin.TaxiWords;

public class TaxiWordsTests
{
    private static readonly string[] Stations =
    [
        "Alpha Station",
        "Beta Station",
        "Gamma Station",
        "Delta Station",
        "Moon Station",
        "North Station",
        "South Station",
    ];

    private static (Kind Kind, string Station) M(string text)
    {
        var (kind, station, _) = Match(text, Stations);
        return (kind, station);
    }

    [Theory]
    [InlineData("moon")]
    [InlineData("MOON")]
    [InlineData("Moon Station")]
    [InlineData("moon station")]
    [InlineData("  Moon,  STATION!! ")]
    [InlineData("moon.")]
    [InlineData("\"Moon\"")]
    public void CaseAndPunctuationAndTheStationWord(string text) =>
        Assert.Equal((Kind.Station, "Moon Station"), M(text));

    [Theory]
    [InlineData("mon", "Moon Station")] // 1 letter missing in a short word
    [InlineData("mooon", "Moon Station")]
    [InlineData("alhpa", "Alpha Station")] // swapped neighbors
    [InlineData("gama", "Gamma Station")]
    [InlineData("gmaa", "Gamma Station")]
    [InlineData("nroht", "North Station")] // 2 typos in a 5-letter word
    [InlineData("soth stattion", "South Station")] // typos in "station" too
    [InlineData("Alfa", "Alpha Station")]
    public void Typos(string text, string station) =>
        Assert.Equal((Kind.Station, station), M(text));

    [Theory]
    [InlineData("call")]
    [InlineData("Call!")]
    [InlineData("CALL")]
    [InlineData("cal")]
    [InlineData("calll")]
    public void Call(string text) => Assert.Equal(Kind.Call, M(text).Kind);

    [Fact]
    public void AmbiguousNamesBothClose()
    {
        // one letter from Beta and from Delta
        var (kind, _, close) = Match("deta", Stations);
        Assert.Equal(Kind.Ambiguous, kind);
        Assert.Equal(["Beta Station", "Delta Station"], close);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("take me home")]
    [InlineData("")]
    [InlineData("?!")]
    [InlineData("station")]
    [InlineData("mars")]
    public void Nothing(string text) => Assert.Equal(Kind.None, M(text).Kind);

    [Fact]
    public void PrefixOfThreeOrMore() => Assert.Equal((Kind.Station, "Delta Station"), M("del"));

    [Theory]
    [InlineData("GPS:Drop:-111987.17:-96.8:83803.52:#FFFFFF00:", -111987.17, -96.8, 83803.52)]
    [InlineData("take me here GPS:My base:100:200:-300: please", 100, 200, -300)]
    [InlineData("gps:x:1.5:2:3:", 1.5, 2, 3)]
    public void GpsLinks(string text, double x, double y, double z)
    {
        Assert.True(TryGps(text, out var p));
        Assert.Equal((x, y, z), p);
    }

    [Theory]
    [InlineData("moon")]
    [InlineData("GPS:broken:1:2:")]
    [InlineData("GPS 1 2 3")]
    public void NoGpsLink(string text) => Assert.False(TryGps(text, out _));

    [Fact]
    public void GpsDestinationRoundTrip()
    {
        var dest = GpsDestination((-111987.4, -96.8, 83803.5));
        Assert.Equal("GPS -111987 -97 83804", dest);
        Assert.True(TryGpsDestination(dest, out var p));
        Assert.Equal((-111987.0, -97.0, 83804.0), p);
        Assert.False(TryGpsDestination("Moon Station", out _));
        Assert.False(TryGpsDestination("GPS Station", out _));
    }
}
