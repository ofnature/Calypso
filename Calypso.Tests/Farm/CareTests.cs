using Calypso.Core.Farm;
using static Calypso.Tests.Farm.Farms;

namespace Calypso.Tests.Farm;

public class CareTests
{
    [Fact]
    public void EveryPlotUnderCareCarriesTheDailyFee()
    {
        var plan = Plan(new[] { Growing(0, Cabbage), Growing(1, Onion), Empty(2) }, new[] { Short(Produce(Pumpkin), 10) });

        Assert.Equal(new[] { FarmAction.KeepUnderCare, FarmAction.KeepUnderCare, FarmAction.Plant }, plan.Plots.Select(d => d.Action));
        Assert.All(plan.Plots, d => Assert.Equal(5, d.CowrieCost));
        Assert.Equal(15, plan.TotalCost);
        Assert.Contains("5 cowries a day", plan.Plots[0].Reason);
    }

    [Fact]
    public void APlotWhoseFeeIsPaidTodayPlantsForFree()
    {
        var paid = Empty(0) with { WasUnderCare = true };
        var plan = Plan(new[] { paid }, new[] { Short(Produce(Pumpkin), 10) });

        Assert.Equal(FarmAction.Plant, plan.Plots[0].Action);
        Assert.Equal(0, plan.Plots[0].CowrieCost);
    }

    [Fact]
    public void PlantingStopsWhereTheCowriesRunOut()
    {
        var input = Input(EmptyPlots(3), new[] { Short(Produce(Pumpkin), 15), Short(Produce(Onion), 15), Short(Produce(Tomato), 15) }, cowries: 12);
        var plan = FarmPlanner.Plan(input);

        Assert.Equal(new int?[] { Pumpkin, Onion, null }, Planted(plan));
        Assert.Equal("stays empty: not enough cowries for another plot's care (5 a day)", plan.Plots[2].Reason);
        Assert.Equal(10, plan.TotalCost);
    }

    [Fact]
    public void FeesForPlotsAlreadyUnderCareThatTheCowriesDoNotCoverAreWarned()
    {
        var input = Input(new[] { Growing(0, Cabbage), Growing(1, Onion) }, cowries: 7);
        var plan = FarmPlanner.Plan(input);

        Assert.Contains(plan.Warnings, w => w.Contains("7 cowries do not cover 2 plots' care (5 each a day)"));
    }

    [Theory]
    [InlineData(1, FarmAction.KeepUnderCare)] // 1 unit × 100 is not more than the threshold
    [InlineData(2, FarmAction.Replant)]
    public void ACropIsReplantedOnlyWhenTheNeedMovedPastTheThreshold(int pumpkinShort, FarmAction expected)
    {
        var plan = Plan(new[] { Growing(0, Cabbage, growth: 1, waiting: 5) }, new[] { Short(Produce(Pumpkin), pumpkinShort) });

        var d = plan.Plots[0];
        Assert.Equal(expected, d.Action);
        if (expected == FarmAction.Replant)
        {
            Assert.Equal(Pumpkin, d.Crop);
            Assert.True(d.Collect); // CollectSingleAndDismiss, then Entrust
            Assert.StartsWith("collect 5 Island Cabbage; replant Island Cabbage with Island Pumpkin", d.Reason);
        }
        else
        {
            Assert.Null(d.Crop);
        }
    }

    [Fact]
    public void ACropStillNeededIsNotReplantedForABiggerNeedAnEmptyPlotCanTake()
    {
        var plan = Plan(new[] { Growing(0, Cabbage), Empty(1) }, new[] { Short(Produce(Cabbage), 10), Short(Produce(Pumpkin), 15) });

        Assert.Equal(FarmAction.KeepUnderCare, plan.Plots[0].Action);
        Assert.Equal(FarmAction.Plant, plan.Plots[1].Action);
        Assert.Equal(Pumpkin, plan.Plots[1].Crop);
    }

    [Fact]
    public void ACropNothingWantsIsDismissedOnlyWhenItsHarvestsWouldOverflow()
    {
        var full = Plan(new[] { Growing(0, Cabbage) }, onHand: OnHand(except: (Produce(Cabbage), 990)));
        Assert.Equal(FarmAction.Dismiss, full.Plots[0].Action);
        Assert.Equal(0, full.Plots[0].CowrieCost);
        Assert.Equal("dismiss Island Cabbage: nothing needs it and its harvests would overflow the pouch", full.Plots[0].Reason);

        var room = Plan(new[] { Growing(0, Cabbage) }, onHand: OnHand(except: (Produce(Cabbage), 500)));
        Assert.Equal(FarmAction.KeepUnderCare, room.Plots[0].Action);
        Assert.EndsWith("nothing is short, so it keeps the stock growing", room.Plots[0].Reason);
    }

    [Fact]
    public void HaltedCareIsKeptBecauseItResumesByItself()
    {
        var halted = Growing(0, Cabbage) with { UnderCare = false, CareHalted = true };
        var plan = Plan(new[] { halted }, new[] { Short(Produce(Pumpkin), 1) });

        Assert.Equal(FarmAction.KeepUnderCare, plan.Plots[0].Action);
        Assert.Contains(plan.Warnings, w => w.Contains("care halted"));
    }

    [Fact]
    public void NotBuiltUnderConstructionAndBeyondTheLevelDoNothing()
    {
        var needs = new[] { Short(Produce(Pumpkin), 75) };

        Assert.All(FarmPlanner.Plan(Input(EmptyPlots(5), needs, level: 0)).Plots, d => Assert.Equal("not built", d.Reason));
        Assert.All(FarmPlanner.Plan(Input(EmptyPlots(5), needs) with { UnderConstruction = true }).Plots, d => Assert.Equal("under construction", d.Reason));

        var level1 = FarmPlanner.Plan(Input(EmptyPlots(7), needs, level: 1));
        Assert.All(level1.Plots.Take(5), d => Assert.Equal(FarmAction.Plant, d.Action));
        Assert.All(level1.Plots.Skip(5), d => Assert.Equal("not a plot at this cropland level", d.Reason));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    public void GrowthLevelReadsAsDaysToHarvest(int growth, int days) => Assert.Equal(days, new FarmRules().DaysToHarvest(growth));

    [Fact]
    public void HarvestsComeEveryTwoDaysAndExpectedCountsTheWeek()
    {
        var plan = Plan(EmptyPlots(1), new[] { Short(Produce(Pumpkin), 15) });

        // Planted today: harvests after 2, 4 and 6 resets within the 7-day horizon.
        Assert.Equal(15, plan.Plots[0].Expected[Produce(Pumpkin)]);
        Assert.Contains("first harvest in 2 days", plan.Plots[0].Reason);
    }
}
