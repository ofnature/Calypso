using Calypso.Core.Farm;
using static Calypso.Tests.Farm.Farms;

namespace Calypso.Tests.Farm;

public class FarmCollectionTests
{
    private static Dictionary<int, int> W(params (int Row, int Count)[] items) => items.ToDictionary(i => i.Row, i => i.Count);

    [Fact]
    public void WhatTheGardenerHarvestedIsCollected()
    {
        var plan = Plan(new[] { Growing(0, Cabbage, waiting: 12) });

        var d = plan.Plots[0];
        Assert.True(d.Collect);
        Assert.Equal(W((Produce(Cabbage), 12)), d.Collected);
        Assert.Empty(d.Discarded);
        Assert.Equal(FarmAction.KeepUnderCare, d.Action);
        Assert.StartsWith("collect 12 Island Cabbage; keep Island Cabbage under care", d.Reason);
        Assert.True(plan.CollectAll);
    }

    [Fact]
    public void NothingWaitingNothingCollected()
    {
        var plan = Plan(new[] { Growing(0, Cabbage) });

        Assert.False(plan.Plots[0].Collect);
        Assert.False(plan.CollectAll);
    }

    [Fact]
    public void OverflowIsHeldWithTheExactDiscard()
    {
        var onHand = OnHand(except: (Produce(Cabbage), 995));
        var plan = Plan(new[] { Growing(0, Cabbage, waiting: 12), Growing(1, Pumpkin, waiting: 5) }, onHand: onHand);

        var d = plan.Plots[0];
        Assert.False(d.Collect);
        Assert.Empty(d.Collected);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Plot 1: held: collecting now would discard 8 Island Cabbage (pouch cap 999)"));
        // One plot holds back, so CollectAll would either discard or fail: collect one by one.
        Assert.False(plan.CollectAll);
        Assert.True(plan.Plots[1].Collect);
    }

    [Fact]
    public void TheSecondPlotCollectsIntoWhatTheFirstLeft()
    {
        var onHand = OnHand(except: (Produce(Cabbage), 980));
        var plan = Plan(new[] { Growing(0, Cabbage, waiting: 15), Growing(1, Cabbage, waiting: 15) }, onHand: onHand);

        Assert.True(plan.Plots[0].Collect);
        Assert.Equal(15, plan.Plots[0].Collected[Produce(Cabbage)]);
        Assert.False(plan.Plots[1].Collect);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Plot 2: held: collecting now would discard 11 Island Cabbage"));
    }

    [Fact]
    public void AFullPlotWarnsThatCareHalts()
    {
        var onHand = OnHand(except: (Produce(Cabbage), 990));
        var plan = Plan(new[] { Growing(0, Cabbage, waiting: 45) }, onHand: onHand);

        Assert.Contains(plan.Warnings, w => w.Contains("the plot holds 45 of 50, so care halts at its next harvest"));
    }

    [Fact]
    public void APlayersOwnPlotIsStillCollectedButNeverReplanted()
    {
        var mine = new CropPlot { Slot = 0, Crop = Cabbage, Waiting = 5 };
        var plan = Plan(new[] { mine }, new[] { Short(Produce(Pumpkin), 50) });

        var d = plan.Plots[0];
        Assert.True(d.Collect);
        Assert.Equal(FarmAction.Nothing, d.Action);
        Assert.EndsWith("Island Cabbage is not under the gardener's care: left to the player", d.Reason);
        Assert.Equal(0, d.CowrieCost);
    }
}
