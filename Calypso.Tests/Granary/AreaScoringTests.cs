using Calypso.Core.Granary;
using static Calypso.Tests.Granary.Granaries;

namespace Calypso.Tests.Granary;

public class AreaScoringTests
{
    [Fact]
    public void TheAreaWhoseRareTheWorkshopIsShortOfWins()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(Garnet, 20) }));

        var d = Assert.Single(plan.Granaries);
        Assert.Equal(GranaryAction.Dispatch, d.Action);
        Assert.Equal(1, d.Area); // Fatal Falls
        Assert.Contains("Raw Island Garnet", d.Reason);
        Assert.Contains("this week's workshop", d.Reason);
    }

    [Fact]
    public void WithNothingNeededTwoGranariesGoToDifferentAreasLowestIdFirst()
    {
        var plan = Plan(Home(0), Home(1));

        Assert.Equal(new int?[] { 0, 1 }, plan.Granaries.Select(d => d.Area));
        Assert.All(plan.Granaries, d => Assert.Equal(GranaryAction.Dispatch, d.Action));
    }

    [Fact]
    public void TiesBetweenNeedsOfEqualWeightGoToTheLowerArea()
    {
        // Garnet and silver are needed alike; one granary covers either in full.
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(Silver, 10), Short(Garnet, 10) }));

        Assert.Equal(1, plan.Granaries[0].Area);
    }

    [Fact]
    public void TwoGranariesSplitAcrossTwoNeededRares()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0), Home(1) }, new[] { Short(Garnet, 20), Short(Silver, 20) }));

        Assert.Equal(new[] { 1, 4 }, plan.Granaries.Select(d => d.Area!.Value).OrderBy(a => a));
    }

    [Fact]
    public void OneRareThatDominatesTheNeedGetsBothGranaries()
    {
        // 6 a day × 6 days = 36 per granary: 60 garnet needs both, and the 5 silver is worth less than 24 more garnet.
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0), Home(1) }, new[] { Short(Garnet, 60), Short(Silver, 5) }));

        Assert.All(plan.Granaries, d => Assert.Equal(1, d.Area));
    }

    [Fact]
    public void AGranaryAlreadyOutForARareLeavesItToThatGranary()
    {
        // Granary 1 already brings garnet for 5 more days (30): the home granary goes after the silver.
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0), Away(1, area: 1, days: 5) }, new[] { Short(Garnet, 20), Short(Silver, 20) }));

        Assert.Equal(4, plan.Granaries[0].Area);
        Assert.Equal(GranaryAction.Nothing, plan.Granaries[1].Action);
    }

    [Fact]
    public void WithNoDemandTheStockFloorDecides()
    {
        var onHand = OnHand(100, (Spruce, 5));
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, onHand: onHand));

        Assert.Equal(2, plan.Granaries[0].Area);
        Assert.Contains("stock floor", plan.Granaries[0].Reason);
    }

    [Fact]
    public void TwoLowRaresEachGetAGranary()
    {
        var onHand = OnHand(100, (Alyssum, 0), (Hammerhead, 10));
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0), Home(1) }, onHand: onHand));

        Assert.Equal(new[] { 0, 3 }, plan.Granaries.Select(d => d.Area!.Value).OrderBy(a => a));
    }

    [Fact]
    public void TheWorkshopComesBeforeConstruction()
    {
        var needs = new[] { new GranaryNeed(Silver, 30, 7, NeedSource.Construction), Short(Spruce, 10) };
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, needs));

        Assert.Equal(2, plan.Granaries[0].Area);
    }

    [Fact]
    public void ABigNormalNeedCanOutweighTheStockFloor()
    {
        // Coal only comes from the caverns; 100 of it gathered slowly is worth more than topping up one rare.
        var needs = new[] { new GranaryNeed(Coal, 100, 7, NeedSource.SlowGather) };
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, needs, onHand: OnHand(100, (Alyssum, 25))));

        Assert.Equal(5, plan.Granaries[0].Area);
    }

    [Fact]
    public void NormalResourcesNeverGatheredByHandAreNotExpected()
    {
        var needs = new[] { new GranaryNeed(Coal, 100, 7, NeedSource.SlowGather) };
        var gathered = Enumerable.Range(0, 76).ToHashSet(); // everything below coal
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, needs, gatheredBefore: gathered));

        Assert.NotEqual(5, plan.Granaries[0].Area);
        Assert.DoesNotContain(Coal, plan.Granaries[0].Expected.Keys);
    }

    [Fact]
    public void LockedAreasAreNeverChosen()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, new[] { Short(CaveShrimp, 20) }, unlocked: Enumerable.Range(0, 5)));

        Assert.InRange(plan.Granaries[0].Area!.Value, 0, 4);
    }

    [Fact]
    public void NoAreaUnlockedMeansNothingAndAWarning()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, unlocked: Array.Empty<int>()));

        Assert.Equal(GranaryAction.Nothing, plan.Granaries[0].Action);
        Assert.Contains(plan.Warnings, w => w.Contains("no expedition area"));
    }

    [Fact]
    public void ExpectedMaterialsAreTheRareByLevelAndTheMeanOfEachNormal()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0, level: 2) }, new[] { Short(Garnet, 20) }));
        var d = plan.Granaries[0];

        Assert.Equal(7, d.Days);
        Assert.Equal(3 * 7, d.Expected[Garnet]); // granary II: 3 a day
        Assert.Equal(12 * 7, d.Expected[Sand]);
        Assert.Equal(GranaryFixture.Areas[1].NormalPouchRows.Count + 1, d.Expected.Count);
    }

    [Fact]
    public void TheSameInputGivesTheSamePlan()
    {
        var input = Input(new[] { Home(0), Away(1, 2, 1) }, new[] { Short(Garnet, 20), Short(Silver, 30), Short(Spruce, 4) });
        var a = GranaryPlanner.Plan(input);
        var b = GranaryPlanner.Plan(input);

        Assert.Equal(a.Granaries.Select(d => (d.Action, d.Area, d.Days, d.CowrieCost, d.Reason)), b.Granaries.Select(d => (d.Action, d.Area, d.Days, d.CowrieCost, d.Reason)));
    }

    [Fact]
    public void UnbuiltAndUpgradingGranariesDoNothing()
    {
        var upgrading = Home(1) with { UnderConstruction = true };
        var plan = Plan(Home(0, level: 0), upgrading);

        Assert.All(plan.Granaries, d => Assert.Equal(GranaryAction.Nothing, d.Action));
        Assert.Equal(new[] { "not built", "under construction" }, plan.Granaries.Select(d => d.Reason));
        Assert.Equal(0, plan.TotalCost);
    }
}
