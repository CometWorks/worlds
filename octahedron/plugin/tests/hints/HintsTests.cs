using System;
using System.Linq;
using ServerPlugin;
using Xunit;

public class HintsTests
{
    [Fact]
    public void TheWaitIsBetweenTheTwoLimits()
    {
        Assert.Equal(30, HintMath.Wait(30, 120, 0));
        Assert.Equal(75, HintMath.Wait(30, 120, 0.5));
        Assert.Equal(120, HintMath.Wait(30, 120, 1));
    }

    [Fact]
    public void LimitsTheWrongWayRoundOrBelowZeroStillGiveAWait()
    {
        Assert.Equal(30, HintMath.Wait(120, 30, 0));
        Assert.Equal(120, HintMath.Wait(120, 30, 1));
        Assert.Equal(0, HintMath.Wait(-5, -1, 0.5));
    }

    [Fact]
    public void NoHintComesTwiceInARowAndAllOfThemCome()
    {
        var random = new Random(1538);
        var last = -1;
        var seen = new int[3];
        for (var i = 0; i < 1000; i++)
        {
            var pick = HintMath.Pick(3, last, random.Next);
            Assert.InRange(pick, 0, 2);
            Assert.NotEqual(last, pick);
            seen[pick]++;
            last = pick;
        }
        Assert.All(seen, n => Assert.InRange(n, 250, 420));
    }

    [Fact]
    public void EveryOtherHintCanFollow()
    {
        var followers = Enumerable.Range(0, 3).Select(i => HintMath.Pick(4, 1, _ => i));
        Assert.Equal(new[] { 0, 2, 3 }, followers);
    }

    [Fact]
    public void ASingleHintRepeatsAndAStaleLastOneIsIgnored()
    {
        Assert.Equal(0, HintMath.Pick(1, 0, n => n - 1));
        Assert.Equal(1, HintMath.Pick(2, 5, n => n - 1));
    }
}
