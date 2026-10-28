using Calypso.Core.Farm;
using static Calypso.Tests.Farm.Farms;

namespace Calypso.Tests.Farm;

public class SeedChoiceTests
{
    [Fact]
    public void TheProduceTheWorkshopIsShortOfWins()
    {
        var plan = Plan(EmptyPlots(3), new[] { Short(Produce(Isleberry), 10) });

        Assert.Equal(new int?[] { Isleberry, null, null }, Planted(plan));
        var d = plan.Plots[0];
        Assert.Equal(Crop(Isleberry).SeedItemId, d.SeedItemId);
        Assert.Contains("10 Isleberry toward this week's workshop (short 10)", d.Reason);
        Assert.Equal("stays empty: nothing is short", plan.Plots[1].Reason);
    }

    [Fact]
    public void PlotsSplitAcrossTwoNeeds()
    {
        // A plot brings 15 by day 6 (harvests on days 2, 4 and 6), so each need takes one plot.
        var plan = Plan(EmptyPlots(4), new[] { Short(Produce(Pumpkin), 15), Short(Produce(Onion), 15) });

        Assert.Equal(new int?[] { Pumpkin, Onion, null, null }, Planted(plan));
    }

    [Fact]
    public void OneNeedThatDominatesTakesSeveralPlots()
    {
        var plan = Plan(EmptyPlots(6), new[] { Short(Produce(Pumpkin), 60), Short(Produce(Onion), 5) });

        Assert.Equal(new int?[] { Pumpkin, Pumpkin, Pumpkin, Pumpkin, Onion, null }, Planted(plan));
    }

    [Fact]
    public void ANeedDueTomorrowCannotBeGrownInTime()
    {
        // Two days to grow: a need due in 1 day is out of reach, so it does not pull a plot.
        var plan = Plan(EmptyPlots(1), new[] { Short(Produce(Pumpkin), 15, due: 1) });

        Assert.Equal(new int?[] { null }, Planted(plan));
    }

    [Fact]
    public void TiesGoToTheScarcerProduceThenTheMoreHeldSeedThenTheLowestId()
    {
        var needs = new[] { Short(Produce(Tomato), 10), Short(Produce(Popoto), 10) };

        var scarcer = Plan(EmptyPlots(1), needs, OnHand(except: new[] { (Produce(Tomato), 40), (Produce(Popoto), 60) }));
        Assert.Equal(Tomato, Planted(scarcer)[0]);

        var moreSeed = Plan(EmptyPlots(1), needs, OnHand(except: new[] { (Seed(Tomato), 10), (Seed(Popoto), 5) }));
        Assert.Equal(Tomato, Planted(moreSeed)[0]);

        var lowestId = Plan(EmptyPlots(1), needs);
        Assert.Equal(Popoto, Planted(lowestId)[0]);
    }

    [Fact]
    public void TheSameInputGivesTheSamePlan()
    {
        var needs = new[] { Short(Produce(Pumpkin), 40), Short(Produce(Onion), 25), Short(Produce(Tomato), 25, due: 3) };
        var plots = EmptyPlots(5).Concat(new[] { Growing(5, Cabbage, growth: 2, waiting: 10) }).ToArray();

        var a = Plan(plots, needs);
        var b = Plan(plots, needs);

        Assert.Equal(a.Plots.Select(Describe), b.Plots.Select(Describe));
        Assert.Equal(a.Warnings, b.Warnings);

        static string Describe(PlotDecision d) => $"{d.Slot} {d.Collect} {d.Action} {d.Crop} {d.CowrieCost} {d.Reason} {string.Join(",", d.Expected)}";
    }

    [Fact]
    public void ACropWithNoSeedHeldIsNotPlantedAndTheReasonSaysWhichSeed()
    {
        var onHand = OnHand(except: (Seed(Isleberry), 0));
        var plan = Plan(EmptyPlots(2), new[] { Short(Produce(Isleberry), 10), Short(Produce(Onion), 10) }, onHand);

        Assert.Equal(new int?[] { Onion, null }, Planted(plan));
        Assert.Equal(FarmAction.Nothing, plan.Plots[1].Action);
        Assert.StartsWith("stays empty: no Isleberry Seeds to plant (gather or buy more): it would cover 10 Isleberry", plan.Plots[1].Reason);
    }

    [Fact]
    public void SeedsLimitHowLongAPlotKeepsGoing()
    {
        // Two seeds: the planting and one replant. A second plot would only share them, so it is not planted.
        var onHand = OnHand(except: (Seed(Pumpkin), 2));
        var plan = Plan(EmptyPlots(2), new[] { Short(Produce(Pumpkin), 30) }, onHand);

        Assert.Equal(new int?[] { Pumpkin, null }, Planted(plan));
        Assert.Equal(10, plan.Plots[0].Expected[Produce(Pumpkin)]);
        Assert.Contains(plan.Warnings, w => w == "Plot 1: Island Pumpkin Seeds runs out: care halts after the harvest in 4 days (gather or buy more)");
        Assert.StartsWith("stays empty: only 1 Island Pumpkin Seeds to plant", plan.Plots[1].Reason);
    }

    [Fact]
    public void WithNoDemandTheStockFloorPlantsWhatRunsLow()
    {
        var plan = Plan(EmptyPlots(3), onHand: OnHand(except: (Produce(Radish), 0)));

        // 30 short of the floor; a plot brings 15 within the week.
        Assert.Equal(new int?[] { Radish, Radish, null }, Planted(plan));
        Assert.Contains("15 Island Radish toward the stock floor (short 30)", plan.Plots[0].Reason);
    }

    [Fact]
    public void WithNoDemandAndEveryProduceAboveTheFloorNothingIsPlanted()
    {
        var plan = Plan(EmptyPlots(3));

        Assert.All(plan.Plots, d => Assert.Equal(FarmAction.Nothing, d.Action));
        Assert.All(plan.Plots, d => Assert.Equal("stays empty: nothing is short", d.Reason));
        Assert.Equal(0, plan.TotalCost);
    }

    [Fact]
    public void AGrowingCropCountsTowardTheNeedBeforeAnEmptyPlotIsPlanted()
    {
        // Pumpkin already growing (ripe tomorrow) covers 15 of the 15 by day 5; the empty plot is left for nothing.
        var plan = Plan(new[] { Growing(0, Pumpkin, growth: 3), Empty(1) }, new[] { Short(Produce(Pumpkin), 15) });

        Assert.Equal(FarmAction.KeepUnderCare, plan.Plots[0].Action);
        Assert.Contains("15 Island Pumpkin toward this week's workshop", plan.Plots[0].Reason);
        Assert.Equal(FarmAction.Nothing, plan.Plots[1].Action);
    }
}
