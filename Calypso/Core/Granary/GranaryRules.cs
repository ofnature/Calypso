using System;
using System.Collections.Generic;

namespace Calypso.Core.Granary;

/// <summary>
/// The granary's mechanics and the planner's knobs. None of the numbers are in the sheets Calypso reads; each says
/// where it came from. "Wiki" = Console Games Wiki "Island Granary" / "Home Sweet Hideaway" and Icy Veins' granary
/// guide, read through search snippets only (both sites are blocked in the cloud sandbox). Everything marked UNVERIFIED
/// is on the "needs in-game verification" list.
/// </summary>
public sealed record GranaryRules
{
    /// <summary>UNVERIFIED (wiki: "up to 7 consecutive days"). The Addon text has "Max" next to "Expedition Length".</summary>
    public int MaxDays { get; init; } = 7;

    /// <summary>UNVERIFIED (implied by the per-day price; no source states it).</summary>
    public int MinDays { get; init; } = 1;

    /// <summary>
    /// UNVERIFIED (wiki and Icy Veins: 50 Seafarer's Cowries per day, every area alike). Extending pays the same rate
    /// for the days added: ffxiv_visland (no licence, read for facts only) checks days-added × 50 against the purse.
    /// </summary>
    public int CostPerDay { get; init; } = 50;

    /// <summary>
    /// UNVERIFIED: changing area is free (wiki: "can freely change the expedition location"; the Addon's "Change
    /// expedition area to Y?" is the one prompt with no "Spend X seafarer's cowries").
    /// </summary>
    public int ChangeCost { get; init; }

    /// <summary>
    /// UNVERIFIED (wiki): rare resource per day by granary level I-V: 2, 3, 4, 5, 6 — fixed, not rolled. Index = level
    /// (0 = not built).
    /// </summary>
    public IReadOnlyList<int> RarePerDayByLevel { get; init; } = new[] { 0, 2, 3, 4, 5, 6 };

    /// <summary>
    /// UNVERIFIED (wiki, single source): each normal resource rolls 0-24 in steps of 2 a day, a little more with more
    /// landmarks, and only for items already gathered by hand. The planner counts the mean.
    /// </summary>
    public double NormalPerDay { get; init; } = 12;

    /// <summary>UNVERIFIED: the most of one normal resource a day can bring (see <see cref="NormalPerDay"/>) — the
    /// collect-before-overflow check assumes the worst day.</summary>
    public int NormalMaxPerDay { get; init; } = 24;

    /// <summary>
    /// UNVERIFIED in game: Item.StackSize is 999 for every MJIItemPouch item, granary rares included (only the
    /// Felicitous Token is 150, and no expedition brings it). The Addon warns that items over the limit are discarded.
    /// </summary>
    public int PouchCap { get; init; } = 999;

    /// <summary>
    /// UNVERIFIED (wiki: materials arrive at each daily reset and wait in the granary; visland collects whenever any
    /// count is above 0). False would mean materials only come home when the expedition returns — the planner then
    /// shortens an expedition so a need arrives by its day.
    /// </summary>
    public bool CollectAnyTime { get; init; } = true;

    /// <summary>
    /// UNVERIFIED (wiki Island Materials table lists areas 0-4 at rank 6; the granary itself unlocks at rank 5). The
    /// higher rank is the safe guess: a granary that waits a rank loses little, a wrong dispatch call does nothing.
    /// </summary>
    public int AreasRank { get; init; } = 6;

    /// <summary>UNVERIFIED (wiki, Prima): Crumbling Caverns needs rank 12 and the Mountain Hollow (spelunking) unlock.</summary>
    public int CavernsRank { get; init; } = 12;

    /// <summary>
    /// An expedition this many days (or fewer) from home is topped up to <see cref="MaxDays"/> when its area is still
    /// the pick, so a day the loop doesn't run never leaves the granary idle. 1 = only on its last day: earlier, a top-up
    /// would just buy the same days in more trips.
    /// </summary>
    public int ExtendWhenDaysLeft { get; init; } = 1;

    /// <summary>
    /// Score an area change must gain over keeping the current expedition (one workshop rare this week = 100). Changing
    /// is free, so this only stops the granaries flip-flopping on small shifts in the need.
    /// </summary>
    public double ChangeThreshold { get; init; } = 100;

    /// <summary>Every rare is kept at least this high (lowest priority need). A few days of one rare craft line.</summary>
    public int RareFloor { get; init; } = 30;

    /// <summary>Score per unit of need covered, by where the need comes from. Gaps of ~2-10× keep the order strict
    /// for the amounts a granary moves while still letting a big lower need outweigh a trickle of a higher one.</summary>
    public double Weight(NeedSource source) => source switch
    {
        NeedSource.WorkshopThisWeek => 100,
        NeedSource.WorkshopNextWeek => 50,
        NeedSource.Construction => 10,
        NeedSource.SlowGather => 1,
        NeedSource.StockFloor => 0.5,
        _ => 0,
    };

    /// <summary>Rare per day for a granary level (0 for unbuilt/unknown levels).</summary>
    public int RarePerDay(int level) => level > 0 && level < RarePerDayByLevel.Count ? RarePerDayByLevel[level] : 0;

    /// <summary>Areas open at a rank, when the game adapter cannot read the list itself.</summary>
    public IEnumerable<int> AreasForRank(int rank, bool cavernsUnlocked)
    {
        for (var area = 0; area < ExpeditionArea.Caverns; area++)
        {
            if (rank >= AreasRank)
                yield return area;
        }

        if (cavernsUnlocked && rank >= Math.Max(AreasRank, CavernsRank))
            yield return ExpeditionArea.Caverns;
    }
}
