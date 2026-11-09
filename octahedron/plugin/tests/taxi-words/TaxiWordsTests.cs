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
}
