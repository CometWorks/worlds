using Xunit;
using static ServerPlugin.PoliceRules;

public class PoliceRulesTests
{
    [Fact]
    public void FireAtAnEnemyNpcBringsThePoliceAgainstTheNpc() =>
        Assert.Equal(Pick.Victim, Decide(Side.Other, Side.Enemy, false));

    [Fact]
    public void FireAtAPlayerOrOctShipMakesTheShooterTheTarget() =>
        Assert.Equal(Pick.Attacker, Decide(Side.Other, Side.Other, false));

    [Fact]
    public void AnEnemyNpcThatShootsIsATarget()
    {
        Assert.Equal(Pick.Attacker, Decide(Side.Enemy, Side.Other, false));
        Assert.Equal(Pick.Attacker, Decide(Side.Enemy, Side.Other, true));
        Assert.Equal(Pick.Attacker, Decide(Side.Enemy, Side.Enemy, false));
    }

    [Fact]
    public void FireBackAtATargetIsNoCrime()
    {
        Assert.Equal(Pick.None, Decide(Side.Other, Side.Other, true));
        Assert.Equal(Pick.None, Decide(Side.Other, Side.Enemy, true));
    }
}
