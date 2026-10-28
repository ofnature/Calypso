using Calypso.Core.Pasture;
using static Calypso.Tests.Pasture.Pastures;

namespace Calypso.Tests.Pasture;

public class PastureCollectionTests
{
    private static Dictionary<int, int> W(params (int Row, int Count)[] items) => items.ToDictionary(i => i.Row, i => i.Count);

    [Fact]
    public void WaitingLeavingsAreCollected()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed, waiting: 12) }, waiting: W((Fleece, 9), (Milk, 3))));

        Assert.Equal(PastureAction.CollectLeavings, plan.Action);
        Assert.Equal(W((Fleece, 9), (Milk, 3)), plan.Collected);
        Assert.Empty(plan.Discarded);
        Assert.StartsWith("collect 9 Sanctuary Fleece, 3 Sanctuary Milk (leavings are waiting)", plan.Reason);
    }

    [Fact]
    public void NothingWaitingNothingToCollect()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed) }));

        Assert.NotEqual(PastureAction.CollectLeavings, plan.Action);
        Assert.Empty(plan.Collected);
    }

    [Fact]
    public void OverflowIsHeldWithTheExactDiscardWhenNothingNeedsIt()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed, waiting: 19) },
            waiting: W((Fleece, 3), (Milk, 12)), onHand: OnHand(except: (Milk, 995))));

        Assert.NotEqual(PastureAction.CollectLeavings, plan.Action);
        Assert.Empty(plan.Collected);
        Assert.Contains(plan.Warnings, w => w.StartsWith("held: collecting now would discard 8 Sanctuary Milk (pouch cap 999)"));
        Assert.Contains(plan.Warnings, w => w.EndsWith("1 animal near 20 leavings, where care halts"));
    }

    [Fact]
    public void OverflowIsCollectedAnywayWhenANeededLeavingComesWithIt()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed) },
            needs: new[] { Short(Fleece, 10) }, waiting: W((Fleece, 3), (Milk, 12)), onHand: OnHand(except: (Milk, 995))));

        Assert.Equal(PastureAction.CollectLeavings, plan.Action);
        Assert.Equal(W((Fleece, 3), (Milk, 4)), plan.Collected);
        Assert.Equal(W((Milk, 8)), plan.Discarded);
        Assert.Contains(plan.Warnings, w => w == "collecting discards 8 Sanctuary Milk (pouch cap 999)");
        Assert.StartsWith("collect 3 Sanctuary Fleece, 4 Sanctuary Milk (Sanctuary Fleece is needed)", plan.Reason);
    }

    [Fact]
    public void TheFeeIsCountedForEveryAnimalUnderCareThatIsNotHalted()
    {
        var halted = In(2, ClawFur, feed: Sweetfeed) with { CareHalted = true };
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed), In(1, ClawFur, feed: Premium), halted, In(3, ClawFur) }));

        Assert.Equal(20, plan.CowrieCost);
        Assert.Contains(plan.Warnings, w => w.StartsWith("slot 3: care halted"));
    }

    [Fact]
    public void ShortCowriesAreWarned()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed), In(1, ClawFur, feed: Sweetfeed) }, cowries: 15));

        Assert.Contains(plan.Warnings, w => w.StartsWith("15 cowries do not cover 2 animals' care (10 each a day)"));
    }

    [Fact]
    public void NotBuiltOrUnderConstructionDoesNothing()
    {
        var waiting = W((Fleece, 3));
        Assert.Equal("not built", PasturePlanner.Plan(Input(waiting: waiting, level: 0)).Reason);
        var building = PasturePlanner.Plan(Input(waiting: waiting) with { UnderConstruction = true });
        Assert.Equal(PastureAction.Nothing, building.Action);
        Assert.Equal("under construction", building.Reason);
    }

    [Fact]
    public void ExpectedCountsTheGuaranteedLeavingAndTheSecondByMood()
    {
        // Premium feed: Gleeful, the second leaving on 80% of days.
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Premium) }));

        Assert.Equal(W((Fleece, 7), (Milk, 6)), plan.Expected); // 7 × 0.8 = 5.6
    }
}
