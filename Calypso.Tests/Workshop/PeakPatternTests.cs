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
}
