using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

/// <summary>Pattern matching (<see cref="PeakPatterns"/>) and what <see cref="SupplyForecast"/> makes of it.</summary>
public class PeakPatternTests
{
    private static (Supply, DemandShift)?[] Seen(params (int Day, Supply Supply, DemandShift Demand)[] days)
    {
        var seen = new (Supply, DemandShift)?[7];
        foreach (var d in days)
            seen[d.Day] = (d.Supply, d.Demand);
        return seen;
    }

    private static string[] Names(IEnumerable<PeakPattern> patterns) => patterns.Select(p => p.Name).OrderBy(n => n).ToArray();

    // Day 0's demand shift compares with the last day of the PREVIOUS week, which no pattern of this week fixes.
    // Beachcomber (Squawkbot's model) and mji-craftwork agree only on: Insufficient with no change is the strong
    // cycle-2 peak, Insufficient and Increasing is the weak one; any other shift leaves both.
    [Theory]
    [InlineData(DemandShift.None, new[] { "C2S" })]
    [InlineData(DemandShift.Increasing, new[] { "C2W" })]
    [InlineData(DemandShift.Skyrocketing, new[] { "C2S", "C2W" })]
    [InlineData(DemandShift.Decreasing, new[] { "C2S", "C2W" })]
    [InlineData(DemandShift.Plummeting, new[] { "C2S", "C2W" })]
    public void InsufficientOnTheFirstDayIsACycleTwoPeak(DemandShift shift, string[] expected)
    {
        var left = PeakPatterns.Consistent(Seen((0, Supply.Insufficient, shift)), _ => false);
        Assert.Equal(expected, Names(left));
    }

    [Fact]
    public void ADemandShiftWeCausedDoesNotRuleOutPatterns()
    {
        // The shift shown on day d is the day-over-day supply change, and our own items made on day d-1 are part of it
        // (Squawkbot's model; Beachcomber adds the day's crafts before predicting a shift). Making 8 of a weak
        // cycle-3 item on day 0 turns its natural day-1 change of -4 (Increasing) into +4 (Decreasing).
        var cat = Fixture.Catalog;
        const int craft = 1;
        var observed = new byte[]?[7];
        observed[0] = Enumerable.Repeat((byte)0x22, 91).ToArray(); // Sufficient, no change
        observed[1] = (byte[])observed[0]!.Clone();
        observed[1]![craft] = 0x23;                                // Sufficient, Decreasing
        var produced = new int[]?[7];
        produced[0] = new int[cat.MaxCraftId + 1];
        produced[0]![craft] = 8;

        var forecast = new SupplyForecast(cat, observed, produced);

        // Before: no pattern shows Decreasing on day 1, so every pattern was ruled out and the item's peak was lost.
        Assert.Equal(10, forecast.PatternCount(craft)); // all but the two cycle-2 shapes (day 0 was Sufficient)
        Assert.Equal(4, forecast.PatternCount(2));      // untouched, Sufficient + no change on day 1: C4W, C4S, C5W, C5S
    }

    public static IEnumerable<object[]> AllPatterns() => PeakPatterns.All.Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(AllPatterns))]
    public void EachPatternIsPinnedDownByItsOwnFirstDays(string name)
    {
        var pattern = PeakPatterns.All.Single(p => p.Name == name);
        var cat = Fixture.Catalog;

        // What the game shows for an item on this pattern on days 0-3, with nothing made by us.
        var observed = new byte[]?[7];
        for (var day = 0; day < PeakPatterns.MaxEvidenceDays; day++)
            observed[day] = Enumerable.Repeat(Scenarios.Pack(pattern, day), 91).ToArray();
        var forecast = new SupplyForecast(cat, observed, new int[]?[7]);

        Assert.Equal(1, forecast.PatternCount(1));
        for (var day = PeakPatterns.MaxEvidenceDays; day < 7; day++)
        {
            Assert.Equal(1.0, forecast.Confidence(1, day), 9);
            Assert.Equal(cat.Rules.SupplyRatio[(int)pattern.Supply[day]], forecast.ExpectedRatio(1, day, 0, 0), 9);
        }
    }

    [Fact]
    public void WithNoEvidenceEveryPatternCountsEqually()
    {
        var cat = Fixture.Catalog;
        var forecast = Fixture.NoObservations();
        for (var day = 0; day < 7; day++)
        {
            var mean = PeakPatterns.All.Average(p => (double)cat.Rules.SupplyRatio[(int)p.Supply[day]]);
            var top = PeakPatterns.All.GroupBy(p => p.Supply[day]).Max(g => g.Count()) / 12.0;
            Assert.Equal(mean, forecast.ExpectedRatio(5, day, 0, 0), 9);
            Assert.Equal(top, forecast.Confidence(5, day), 9);
        }
    }

    [Fact]
    public void AmbiguousEvidenceAveragesOverTheConsistentPatternsOnly()
    {
        // Insufficient + Skyrocketing on day 0: weak or strong cycle-2 peak.
        var cat = Fixture.Catalog;
        var observed = new byte[]?[7];
        observed[0] = Enumerable.Repeat((byte)0x22, 91).ToArray();
        observed[0]![7] = (byte)(((int)Supply.Insufficient << 4) | (int)DemandShift.Skyrocketing);
        var forecast = new SupplyForecast(cat, observed, new int[]?[7]);

        Assert.Equal(2, forecast.PatternCount(7));
        Assert.Equal((130 + 160) / 2.0, forecast.ExpectedRatio(7, 1, 0, 0), 9); // Insufficient or Nonexistent tomorrow
        Assert.Equal(0.5, forecast.Confidence(7, 1), 9);
        Assert.Equal(100, forecast.ExpectedRatio(7, 2, 0, 0), 9);               // both back to Sufficient
        Assert.Equal(1.0, forecast.Confidence(7, 2), 9);
    }
}
