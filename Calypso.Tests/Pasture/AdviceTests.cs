using Calypso.Core.Pasture;
using static Calypso.Tests.Pasture.Pastures;

namespace Calypso.Tests.Pasture;

public class AdviceTests
{
    [Fact]
    public void AnAnimalWhoseLeavingThePlanNeedsIsAdvisedToBeKept()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, ClawFur), In(1, FleeceMilk) }, needs: new[] { Short(Fleece, 7, due: 7) }, level: 1));

        var keep = Assert.Single(Of(plan, AdviceKind.Keep));
        Assert.Equal(FleeceMilk, keep.Animal);
        Assert.Equal(1, keep.Slot);
        Assert.Contains("7 Sanctuary Fleece toward this week's workshop (short 7)", keep.Text);
        // The fleece animal covers the need: nothing to capture.
        Assert.Empty(Of(plan, AdviceKind.Capture));
        Assert.Equal(PastureAction.Nothing, plan.Action);
    }

    [Fact]
    public void AMissingLeavingIsAdvisedAsACaptureOfTheEasiestSpecies()
    {
        // Three species give eggs every day (rows 6 variant, 11 common size 2, 23 common size 3): common, then smaller.
        var plan = PasturePlanner.Plan(Input(needs: new[] { Short(Egg, 14, due: 7) }, level: 1));

        var captures = Of(plan, AdviceKind.Capture);
        Assert.Equal(new[] { EggFeather, EggFeather }, captures.Select(c => c.Animal));
        Assert.All(captures, c => Assert.Null(c.Slot));
        Assert.Contains("(common, Makeshift Restraint)", captures[0].Text);
        Assert.Contains("7 Sanctuary Egg toward this week's workshop", captures[0].Text);
        Assert.Equal(PastureAction.Advise, plan.Action);
        Assert.StartsWith("advice: capture", plan.Reason);
    }

    [Fact]
    public void AKnownSpotBreaksATieBeforeSize()
    {
        var large = PastureFixture.Animal(EggFeatherLarge);
        var plan = PasturePlanner.Plan(Input(needs: new[] { Short(Egg, 7, due: 7) }, level: 1, sighted: new HashSet<uint> { large.BNpcBase }));

        var capture = Assert.Single(Of(plan, AdviceKind.Capture));
        Assert.Equal(EggFeatherLarge, capture.Animal);
        Assert.Contains("Makeshift Soporific, seen before", capture.Text);
    }

    [Fact]
    public void CapturesNeverOverfillThePen()
    {
        // Level 1 holds 5; four are there, so one capture fits however big the need.
        var pen = Enumerable.Range(0, 4).Select(i => In(i, EggFeather)).ToArray();
        var plan = PasturePlanner.Plan(Input(pen, needs: new[] { Short(Egg, 100, due: 7) }, level: 1));

        Assert.Single(Of(plan, AdviceKind.Capture));
        Assert.Empty(Of(plan, AdviceKind.Release));
    }

    [Fact]
    public void AFullPenTradesItsLeastUsefulAnimalsOnlyForAClearlyBetterOne()
    {
        var pen = Enumerable.Range(0, 5).Select(i => In(i, ClawFur, feed: i == 0 ? Sweetfeed : 0)).ToArray();

        var big = PasturePlanner.Plan(Input(pen, needs: new[] { Short(Egg, 100, due: 7) }, level: 1));
        var releases = Of(big, AdviceKind.Release);
        var captures = Of(big, AdviceKind.Capture);
        Assert.Equal(5, releases.Count);
        Assert.Equal(releases.Count, captures.Count); // room stays at 5
        Assert.All(releases, r => Assert.Equal(ClawFur, r.Animal));
        Assert.Contains(releases, r => r.Slot == 0 && r.Text.EndsWith("(dismiss its caretaker and collect its leavings first: the game will not release it before)"));

        // One egg short is worth one unit: not past the threshold, so no animal is released for it.
        var small = PasturePlanner.Plan(Input(pen, needs: new[] { Short(Egg, 1, due: 7) }, level: 1));
        Assert.Empty(Of(small, AdviceKind.Release));
        Assert.Empty(Of(small, AdviceKind.Capture));
    }

    [Fact]
    public void BetterFeedIsAdvisedWhenTheSecondLeavingIsNeeded()
    {
        // Fleece / milk on sweetfeed: milk on 25% of days. Premium makes it 80%.
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed) }, needs: new[] { Short(Milk, 5, due: 7) }, level: 1));

        var feed = Assert.Single(Of(plan, AdviceKind.Feed));
        Assert.Equal(Premium, feed.Feed);
        Assert.Equal(0, feed.Slot);
        Assert.Contains("Premium Island Greenfeed as its caretaker's feed: Sanctuary Milk comes 80% of days at Gleeful, not 25% at Content", feed.Text);
    }

    [Fact]
    public void CheaperFeedIsAdvisedWhenNothingNeedsTheSecondLeaving()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Premium) }));

        var feed = Assert.Single(Of(plan, AdviceKind.Feed));
        Assert.Equal(Sweetfeed, feed.Feed);
        Assert.Contains("needs only Island Sweetfeed", feed.Text);
    }

    [Fact]
    public void AnAnimalWhoseLeavingsOnlyOverflowIsAdvisedOut()
    {
        var plan = PasturePlanner.Plan(Input(new[] { In(0, FleeceMilk, feed: Sweetfeed) }, onHand: OnHand(except: new[] { (Fleece, 995), (Milk, 998) })));

        var release = Assert.Single(Of(plan, AdviceKind.Release));
        Assert.Contains("would overflow the pouch (10 cowries a day saved)", release.Text);
    }

    [Fact]
    public void AdviceIsNeverAnActionTheAdapterExecutes()
    {
        // The only action the adapter can carry out is CollectLeavingsAll.
        Assert.Equal(new[] { PastureAction.Nothing, PastureAction.CollectLeavings, PastureAction.Advise }, Enum.GetValues<PastureAction>());

        // Collecting and advice together: the action is the collection; the advice stays advice.
        var plan = PasturePlanner.Plan(Input(needs: new[] { Short(Egg, 14, due: 7) }, waiting: new Dictionary<int, int> { [Egg] = 2 }, level: 1));
        Assert.Equal(PastureAction.CollectLeavings, plan.Action);
        Assert.NotEmpty(Of(plan, AdviceKind.Capture));

        // Advice alone collects nothing.
        var advise = PasturePlanner.Plan(Input(needs: new[] { Short(Egg, 14, due: 7) }, level: 1));
        Assert.Equal(PastureAction.Advise, advise.Action);
        Assert.Empty(advise.Collected);
    }

    [Fact]
    public void TheStockFloorAdvisesCapturesWithNoDemand()
    {
        var plan = PasturePlanner.Plan(Input(onHand: OnHand(except: (Horn, 0)), level: 1));

        var capture = Of(plan, AdviceKind.Capture).First();
        Assert.Contains("Sanctuary Horn", capture.Text);
        Assert.Contains("toward the stock floor (short 30)", capture.Text);
    }

    [Fact]
    public void TheSameInputGivesTheSameAdvice()
    {
        var input = Input(new[] { In(0, ClawFur), In(1, FleeceMilk, feed: Greenfeed) }, needs: new[] { Short(Egg, 20), Short(Milk, 9), Short(Horn, 4) }, level: 2);

        Assert.Equal(PasturePlanner.Plan(input).Advice, PasturePlanner.Plan(input).Advice);
    }
}
