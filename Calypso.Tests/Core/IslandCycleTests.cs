using Calypso.Core;

namespace Calypso.Tests.Core;

public class IslandCycleTests
{
    // CycleTime row 2 as dumped from the game (2026-10-17).
    private const long FirstCycle = 460800;
    private const long Week = 604800;

    [Fact]
    public void WeeklyResetIsTuesday0800Utc()
    {
        var reset = new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc); // a Tuesday
        Assert.Equal(DayOfWeek.Tuesday, reset.DayOfWeek);

        var at = IslandCycle.FromUtc(reset, FirstCycle, Week);
        var justBefore = IslandCycle.FromUtc(reset.AddSeconds(-1), FirstCycle, Week);

        Assert.Equal(0, at.Day);
        Assert.Equal(6, justBefore.Day);
        Assert.Equal(at.Week - 1, justBefore.Week);
    }

    [Fact]
    public void DayAdvancesAt0800Utc()
    {
        var reset = new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, IslandCycle.FromUtc(reset.AddDays(1).AddSeconds(-1), FirstCycle, Week).Day);
        Assert.Equal(1, IslandCycle.FromUtc(reset.AddDays(1), FirstCycle, Week).Day);
        Assert.Equal(6, IslandCycle.FromUtc(reset.AddDays(6).AddHours(23), FirstCycle, Week).Day);
    }

    [Fact]
    public void BeforeEpochFloorsInsteadOfTruncating()
    {
        var c = IslandCycle.FromUnixSeconds(FirstCycle - 1, FirstCycle, Week);
        Assert.Equal(-1, c.Week);
        Assert.Equal(6, c.Day);
    }

    [Fact]
    public void KeyIsStableAndDistinctPerDay()
    {
        var a = IslandCycle.FromUnixSeconds(FirstCycle + 10, FirstCycle, Week);
        var b = IslandCycle.FromUnixSeconds(FirstCycle + 86400 + 10, FirstCycle, Week);
        Assert.Equal("0.0", a.Key);
        Assert.Equal("0.1", b.Key);
    }

    [Fact]
    public void ZeroCycleLengthThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IslandCycle.FromUnixSeconds(0, FirstCycle, 0));
    }
}
