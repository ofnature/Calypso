using Calypso.Core.Granary;
using static Calypso.Tests.Granary.Granaries;

namespace Calypso.Tests.Granary;

public class CollectionTests
{
    private static Dictionary<int, int> W(params (int Row, int Count)[] items) => items.ToDictionary(i => i.Row, i => i.Count);

    [Fact]
    public void AnExpeditionThatCameHomeIsCollectedThenSentOut()
    {
        var waiting = W((Garnet, 42), (Sand, 80));
        var plan = Plan(Home(0, waiting: waiting) with { Area = 1 });

        var d = plan.Granaries[0];
        Assert.True(d.Collect);
        Assert.Equal(waiting, d.Collected);
        Assert.Empty(d.Discarded);
        Assert.Equal(GranaryAction.Dispatch, d.Action);
        Assert.StartsWith("collect (the expedition is home)", d.Reason);
    }

    [Fact]
    public void OverflowWarnsWithTheExactDiscard()
    {
        var onHand = OnHand(100, (Garnet, 980));
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0, waiting: W((Garnet, 30), (Sand, 24))) }, onHand: onHand));

        var d = plan.Granaries[0];
        Assert.True(d.Collect);
        Assert.Equal(19, d.Collected[Garnet]);
        Assert.Equal(24, d.Collected[Sand]);
        Assert.Equal(W((Garnet, 11)), d.Discarded);
        Assert.Contains(plan.Warnings, w => w.Contains("discards 11 Raw Island Garnet"));
    }

    [Fact]
    public void CollectionReportsWhatFitsAndWhatIsLost()
    {
        var (collected, discarded) = GranaryPlanner.Collection(W((Garnet, 30), (Sand, 5), (Coal, 0)), W((Garnet, 990), (Sand, 999)), 999);

        Assert.Equal(W((Garnet, 9), (Sand, 0)), collected);
        Assert.Equal(W((Garnet, 21), (Sand, 5)), discarded);
    }

    [Fact]
    public void TheSecondGranaryCollectsIntoWhatTheFirstLeft()
    {
        var onHand = OnHand(100, (Garnet, 970));
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0, waiting: W((Garnet, 20))), Home(1, waiting: W((Garnet, 20))) }, onHand: onHand));

        Assert.Equal(20, plan.Granaries[0].Collected[Garnet]);
        Assert.Empty(plan.Granaries[0].Discarded);
        Assert.Equal(9, plan.Granaries[1].Collected[Garnet]);
        Assert.Equal(11, plan.Granaries[1].Discarded[Garnet]);
    }

    [Fact]
    public void AwayItCollectsWhileItStillFitsBeforeTheNextPayoutCouldOverflow()
    {
        // 985 + 10 waiting fits; tomorrow's 6 more would not.
        var onHand = OnHand(100, (Garnet, 985));
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 4, waiting: W((Garnet, 10))) }, onHand: onHand));

        var d = plan.Granaries[0];
        Assert.True(d.Collect);
        Assert.Empty(d.Discarded);
        Assert.Contains("collect while it fits", d.Reason);
        Assert.Contains("Raw Island Garnet", d.Reason);
    }

    [Fact]
    public void AwayWithRoomToSpareItLeavesTheMaterialsWaiting()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 4, waiting: W((Garnet, 10), (Sand, 24))) }));

        Assert.False(plan.Granaries[0].Collect);
        Assert.Equal(GranaryAction.Nothing, plan.Granaries[0].Action);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void AwayItHoldsWhenCollectingWouldDiscard()
    {
        var onHand = OnHand(100, (Garnet, 990));
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 4, waiting: W((Garnet, 30))) }, onHand: onHand));

        Assert.False(plan.Granaries[0].Collect);
        Assert.Contains(plan.Warnings, w => w.Contains("held") && w.Contains("21 Raw Island Garnet"));
    }

    [Fact]
    public void AwayItCollectsARareTheWorkshopIsWaitingFor()
    {
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 4, waiting: W((Garnet, 12))) }, new[] { Short(Garnet, 20) }));

        var d = plan.Granaries[0];
        Assert.True(d.Collect);
        Assert.Contains("Raw Island Garnet is needed (this week's workshop)", d.Reason);
    }

    [Fact]
    public void WhenCollectionOnlyHappensAtTheReturnNothingIsCollectedOnTheWay()
    {
        var rules = new GranaryRules { CollectAnyTime = false };
        var onHand = OnHand(100, (Garnet, 985));
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 4, waiting: W((Garnet, 10))) }, new[] { Short(Garnet, 20) },
            onHand: onHand, rules: rules));

        Assert.False(plan.Granaries[0].Collect);
    }

    [Fact]
    public void ExtendingCollectsFirst()
    {
        // Last day out, still the pick: everything has to be claimed before the length can go back to the maximum —
        // even sand nobody is waiting for.
        var plan = GranaryPlanner.Plan(Input(new[] { Away(0, area: 1, days: 1, waiting: W((Sand, 24))) }, new[] { Short(Garnet, 60) }));

        var d = plan.Granaries[0];
        Assert.Equal(GranaryAction.Extend, d.Action);
        Assert.True(d.Collect);
        Assert.Equal(24, d.Collected[Sand]);
        Assert.StartsWith("collect (extending needs every material claimed)", d.Reason);
    }
}
