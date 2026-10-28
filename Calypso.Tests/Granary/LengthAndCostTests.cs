using Calypso.Core.Granary;
using static Calypso.Tests.Granary.Granaries;

namespace Calypso.Tests.Granary;

public class LengthAndCostTests
{
    [Fact]
    public void TheMaximumLengthByDefault()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(Garnet, 6, due: 1) }));

        var d = plan.Granaries[0];
        Assert.Equal(7, d.Days);
        Assert.Equal(350, d.CowrieCost); // 50 a day
        Assert.Equal(350, plan.TotalCost);
    }

    [Fact]
    public void NeverLongerThanTheCowriesPayFor()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(Garnet, 30) }, cowries: 220));

        Assert.Equal(4, plan.Granaries[0].Days);
        Assert.Equal(200, plan.TotalCost);
    }

    [Fact]
    public void TwoGranariesShareOnePurse()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0), Home(1) }, new[] { Short(Garnet, 20), Short(Silver, 20) }, cowries: 500));

        Assert.True(plan.TotalCost <= 500);
        Assert.Equal(new[] { 1, 4 }, plan.Granaries.Select(d => d.Area!.Value).OrderBy(a => a));
        Assert.Equal(10, plan.Granaries.Sum(d => d.Days)); // 4 days each cover 20; the rest of the purse buys longer trips
    }

    [Fact]
    public void WithoutCowriesTheGranaryStaysHomeAndSaysWhy()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(Garnet, 30) }, cowries: 30));

        Assert.Equal(GranaryAction.Nothing, plan.Granaries[0].Action);
        Assert.Contains(plan.Warnings, w => w.Contains("not enough cowries"));
    }

    [Fact]
    public void ShortenedWhenANeedMustArriveSoonerThanTheMaximumReturns()
    {
        // If materials only come home with the expedition, 10 garnet by day 3 needs an expedition back by then.
        var rules = new GranaryRules { CollectAnyTime = false };
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(Garnet, 10, due: 3) }, rules: rules));

        Assert.Equal(1, plan.Granaries[0].Area);
        Assert.Equal(3, plan.Granaries[0].Days); // the longest that is still back in time
    }

    [Fact]
    public void NotShortenedWhenMaterialsCanBeCollectedOnTheWay()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(Garnet, 10, due: 3) }));

        Assert.Equal(7, plan.Granaries[0].Days);
    }

    [Fact]
    public void AnExpeditionOnItsLastDayIsExtendedWhenItsAreaIsStillThePick()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 1) }, new[] { Short(Garnet, 20) }));

        var d = plan.Granaries[0];
        Assert.Equal(GranaryAction.Extend, d.Action);
        Assert.Equal(1, d.Area);
        Assert.Equal(7, d.Days);
        Assert.Equal(300, d.CowrieCost); // six days added
        Assert.Equal(36, d.Expected[Garnet]);
    }

    [Fact]
    public void AnExpeditionWithDaysToGoIsNotExtended()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 3) }, new[] { Short(Garnet, 60) }));

        Assert.Equal(GranaryAction.Nothing, plan.Granaries[0].Action);
        Assert.Equal(0, plan.TotalCost);
    }

    [Fact]
    public void OnItsLastDayAnExpeditionInTheWrongPlaceComesHomeInstead()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 0, days: 1) }, new[] { Short(Garnet, 20) }));

        var d = plan.Granaries[0];
        Assert.Equal(GranaryAction.Nothing, d.Action);
        Assert.Equal("home in 1 day, then Fatal Falls", d.Reason);
        Assert.Equal(0, d.CowrieCost);
    }

    [Fact]
    public void AreaChangesOnlyPastTheThreshold()
    {
        // One garnet is worth exactly the threshold (100): not enough to move.
        var small = GranaryPlanner.Plan(Input(new[] { Away(0, area: 0, days: 5) }, new[] { Short(Garnet, 1) }));
        Assert.Equal(GranaryAction.Nothing, small.Granaries[0].Action);

        var big = GranaryPlanner.Plan(Input(new[] { Away(0, area: 0, days: 5) }, new[] { Short(Garnet, 10) }));
        var d = big.Granaries[0];
        Assert.Equal(GranaryAction.Change, d.Action);
        Assert.Equal(1, d.Area);
        Assert.Equal(5, d.Days); // keeps the days left
        Assert.Equal(0, d.CowrieCost);
    }

    [Fact]
    public void AnAreaChangeThatCostsHasToBeAffordable()
    {
        var rules = new GranaryRules { ChangeCost = 100 };
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 0, days: 5) }, new[] { Short(Garnet, 10) }, cowries: 50, rules: rules));

        Assert.Equal(GranaryAction.Nothing, plan.Granaries[0].Action);
    }

    [Fact]
    public void AGranaryAlreadyBringingWhatIsNeededIsLeftAlone()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 5) }, new[] { Short(Garnet, 20) }));

        Assert.Equal(GranaryAction.Nothing, plan.Granaries[0].Action);
        Assert.Equal("keep Fatal Falls (5 days left)", plan.Granaries[0].Reason);
    }

    [Fact]
    public void WithNothingNeededALastDayExpeditionIsExtendedRatherThanMoved()
    {
        var plan = Plan(Away(0, area: 3, days: 1));

        Assert.Equal(GranaryAction.Extend, plan.Granaries[0].Action);
        Assert.Equal(3, plan.Granaries[0].Area);
    }
}
