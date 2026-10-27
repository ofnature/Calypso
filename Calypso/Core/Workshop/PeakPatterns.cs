// The supply/demand pattern table below is ported from WorkshopOptimizerPlugin
// (https://github.com/belzaru17/WorkshopOptimizerPlugin, Data/SupplyDemandPattern.cs).
// Changed here: the cycle-2 patterns' first-day demand shift (see FirstDayDemandNever).
//
// MIT License — Copyright (c) 2022 belzaru17
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
// Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Workshop;

/// <summary>
/// One weekly peak shape: on which day an item peaks (0-based, so "C2" is day 1), whether strongly, and the natural
/// supply bucket and demand shift it shows each day — before anyone's own production moves it.
/// A null demand entry matches anything. <paramref name="FirstDayDemandNever"/> is the one first-day shift that rules
/// the pattern out.
/// </summary>
public sealed record PeakPattern(int PeakDay, bool Strong, Supply[] Supply, DemandShift?[] Demand, DemandShift? FirstDayDemandNever = null)
{
    public string Name => $"C{PeakDay + 1}{(Strong ? "S" : "W")}";
}

public static class PeakPatterns
{
    private const Supply Non = Supply.Nonexistent;
    private const Supply Ins = Supply.Insufficient;
    private const Supply Suf = Supply.Sufficient;
    private static readonly DemandShift? Any = null;
    private static readonly DemandShift? Sky = DemandShift.Skyrocketing;
    private static readonly DemandShift? Inc = DemandShift.Increasing;
    private static readonly DemandShift? Non0 = DemandShift.None;
    private static readonly DemandShift? Dec = DemandShift.Decreasing;
    private static readonly DemandShift? Plu = DemandShift.Plummeting;

    /// <summary>
    /// WorkshopOptimizer matches at most the first four observed days ("cycle > 4 → 4"); later days are where the
    /// table's shapes are least reliable. Kept identical so the two tools agree.
    /// </summary>
    public const int MaxEvidenceDays = 4;

    public static readonly IReadOnlyList<PeakPattern> All = new PeakPattern[]
    {
        // Day 0's shift compares with last week's final day, so it depends on last week, not on this pattern.
        // WorkshopOptimizer pins it (weak Increasing, strong Skyrocketing), which rules out a strong peak seen with no
        // change. Beachcomber (Squawkbot's model) and mji-craftwork agree only that no change means strong and
        // Increasing means weak; every other shift fits both (PeakPatternTests.InsufficientOnTheFirstDayIsACycleTwoPeak).
        new(1, false, new[] { Ins, Ins, Suf, Suf, Suf, Suf, Suf }, new[] { Any, Inc, Plu, Non0, Non0, Non0, Non0 }, FirstDayDemandNever: DemandShift.None),
        new(1, true,  new[] { Ins, Non, Suf, Suf, Suf, Suf, Suf }, new[] { Any, Sky, Plu, Non0, Non0, Non0, Non0 }, FirstDayDemandNever: DemandShift.Increasing),
        new(2, false, new[] { Suf, Ins, Ins, Suf, Suf, Suf, Suf }, new[] { Any, Inc, Inc, Plu, Non0, Non0, Non0 }),
        new(2, true,  new[] { Suf, Ins, Non, Suf, Suf, Suf, Suf }, new[] { Any, Sky, Sky, Plu, Non0, Non0, Non0 }),
        new(3, false, new[] { Suf, Suf, Ins, Ins, Suf, Suf, Suf }, new[] { Any, Non0, Inc, Inc, Plu, Non0, Non0 }),
        new(3, true,  new[] { Suf, Suf, Ins, Non, Suf, Suf, Suf }, new[] { Any, Non0, Sky, Sky, Plu, Non0, Non0 }),
        new(4, false, new[] { Suf, Suf, Suf, Ins, Ins, Suf, Suf }, new[] { Any, Non0, Non0, Inc, Inc, Plu, Non0 }),
        new(4, true,  new[] { Suf, Suf, Suf, Ins, Non, Suf, Suf }, new[] { Any, Non0, Non0, Sky, Sky, Plu, Non0 }),
        new(5, false, new[] { Suf, Ins, Suf, Suf, Ins, Ins, Suf }, new[] { Any, Inc, Dec, Inc, Inc, Inc, Plu }),
        new(5, true,  new[] { Suf, Ins, Suf, Suf, Ins, Non, Suf }, new[] { Any, Inc, Plu, Sky, Sky, Sky, Plu }),
        new(6, false, new[] { Suf, Ins, Suf, Suf, Suf, Ins, Ins }, new[] { Any, Inc, Plu, Inc, Inc, Inc, Inc }),
        new(6, true,  new[] { Suf, Ins, Suf, Suf, Suf, Ins, Non }, new[] { Any, Inc, Plu, Non0, Sky, Sky, Sky }),
    };

    /// <summary>
    /// The patterns consistent with what was seen for one item. <paramref name="observed"/>[day] is null for a day
    /// not observed. A day's supply is ignored when <paramref name="supplyDistorted"/> says our own production had
    /// already moved it (the table describes natural supply); its demand shift still counts.
    /// </summary>
    public static IReadOnlyList<PeakPattern> Consistent(
        IReadOnlyList<(Supply Supply, DemandShift Demand)?> observed,
        System.Func<int, bool> supplyDistorted)
    {
        return All.Where(p =>
        {
            for (var day = 0; day < MaxEvidenceDays && day < observed.Count; day++)
            {
                if (observed[day] is not { } seen)
                    continue;
                if (!supplyDistorted(day) && p.Supply[day] != seen.Supply)
                    return false;
                if (p.Demand[day] is { } d && d != seen.Demand)
                    return false;
                if (day == 0 && p.FirstDayDemandNever == seen.Demand)
                    return false;
            }

            return true;
        }).ToList();
    }
}
