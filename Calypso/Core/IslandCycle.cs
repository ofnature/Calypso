using System;

namespace Calypso.Core;

/// <summary>
/// Which island week and day "now" is, from the CycleTime sheet's weekly row (row 2: FirstCycle 460800 =
/// Tuesday 1970-01-06 08:00 UTC, Cycle 604800 = one week). The parameters come from the sheet at runtime;
/// nothing here hardcodes the reset time.
///
/// <see cref="Week"/> is a monotonically increasing index since the sheet's epoch — a ledger key, NOT the
/// popularity row or the community's "season" number. <see cref="Day"/> is 0-6, 0 = the day of the weekly reset.
/// </summary>
public readonly record struct IslandCycle(long Week, int Day)
{
    private const long SecondsPerDay = 86400;

    public static IslandCycle FromUnixSeconds(long unixSeconds, long firstCycle, long cycleLength)
    {
        if (cycleLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(cycleLength), "CycleTime row has no cycle length.");

        var sinceEpoch = unixSeconds - firstCycle;
        var week = FloorDiv(sinceEpoch, cycleLength);
        var intoWeek = sinceEpoch - week * cycleLength;
        return new IslandCycle(week, (int)(intoWeek / SecondsPerDay));
    }

    public static IslandCycle FromUtc(DateTime utc, long firstCycle, long cycleLength) =>
        FromUnixSeconds(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds(), firstCycle, cycleLength);

    /// <summary>Stable string key for persisted per-cycle records, e.g. "2905.3".</summary>
    public string Key => $"{Week}.{Day}";

    public override string ToString() => $"week {Week}, day {Day + 1}";

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
