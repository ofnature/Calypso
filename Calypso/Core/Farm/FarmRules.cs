using System;
using System.Collections.Generic;
using Calypso.Core.Granary;

namespace Calypso.Core.Farm;

/// <summary>
/// The cropland's mechanics and the planner's knobs. "Game text" = the in-game help (DescriptionString rows 700-720) and
/// item text read from xivapi/ffxiv-datamining (game data, facts only, no licence). "Wiki" = Console Games Wiki "Island
/// Cropland" and Icy Veins' Island Sanctuary guide, read through search snippets only (both sites are blocked in the
/// cloud sandbox). Everything marked UNVERIFIED is on the "needs in-game verification" list.
/// </summary>
public sealed record FarmRules
{
    /// <summary>
    /// UNVERIFIED (MJIFarmPastureRank row 0, RankData[1..3].Unknown7 = 5 / 10 / 20 — the column's meaning is inferred;
    /// wiki snippets: 5 / 10 / 20 plots for Cropland I-III). Index = level (0 = not built). The adapter can read the
    /// real count (MJIManager.GetFarmSlotCount); plots beyond this are ignored with a warning.
    /// </summary>
    public IReadOnlyList<int> SlotsByLevel { get; init; } = new[] { 0, 5, 10, 20 };

    /// <summary>Verified (game text: every seed says "[Growth Period: Two days]"). Growth stops while the soil is dry;
    /// the gardener keeps it watered.</summary>
    public int GrowDays { get; init; } = 2;

    /// <summary>
    /// UNVERIFIED (wiki: 5 produce per harvest; Icy Veins: "100 crops every two days" at 20 plots). No source says the
    /// cropland level or the crop changes it.
    /// </summary>
    public int YieldPerHarvest { get; init; } = 5;

    /// <summary>
    /// Growth stages (game text: Sprout, Seedling, Blossom, Produce). UNVERIFIED that MJIFarmState.GrowthLevel counts
    /// them 0-4 with 4 = ripe; see <see cref="DaysToHarvest"/>.
    /// </summary>
    public int RipeGrowthLevel { get; init; } = 4;

    /// <summary>
    /// UNVERIFIED (game text: the gardener replants the same seed and stops when "your supply of seeds has been
    /// exhausted", so every planting — the first and each replant — takes seeds from the pouch; the number is not stated).
    /// </summary>
    public int SeedsPerPlanting { get; init; } = 1;

    /// <summary>
    /// UNVERIFIED amount (wiki and Icy Veins: 5 per plot per day; ffxiv_visland — no licence, read for facts only —
    /// checks 5 cowries before entrusting). Verified (game text): paid in Seafarer's cowries, collected at the daily
    /// reset from every plot marked Under Care; a halted plot pays nothing. The adapter can read the island's total,
    /// IslandState.FarmDailyCareFee.
    /// </summary>
    public int CareFee { get; init; } = 5;

    /// <summary>Verified (game text): care halts once a plot holds "the maximum of 50" uncollected yields.</summary>
    public int PlotCap { get; init; } = 50;

    /// <summary>
    /// Verified (Item.StackSize is 999 for every seed and produce; the Addon warns that items over the limit are
    /// discarded). UNVERIFIED in game that the island pouch uses the stack size.
    /// </summary>
    public int PouchCap { get; init; } = 999;

    /// <summary>Days of harvests the planner counts — a week, the workshop plan's horizon.</summary>
    public int HorizonDays { get; init; } = 7;

    /// <summary>
    /// Every produce is kept at least this high (lowest priority need): about a day of one workshop line that uses it,
    /// so next week's plan has some to start on before new harvests arrive.
    /// </summary>
    public int ProduceFloor { get; init; } = 30;

    /// <summary>
    /// Score a different crop must gain over keeping a growing one (one this-week workshop unit = 100). Replanting
    /// throws away the growth so far and a seed, so a small shift in the need never pulls a crop.
    /// </summary>
    public double ReplantThreshold { get; init; } = 100;

    /// <summary>Score per unit of need covered — the granary planner's weights, so one need list ranks the same everywhere.</summary>
    public double Weight(NeedSource source) => GranaryWeights.Weight(source);

    private static readonly GranaryRules GranaryWeights = new();

    /// <summary>Plots a cropland level has (0 for unbuilt/unknown levels).</summary>
    public int Slots(int level) => level > 0 && level < SlotsByLevel.Count ? SlotsByLevel[level] : 0;

    /// <summary>
    /// Resets until a crop at <paramref name="growthLevel"/> is harvested, at least 1 (a ripe crop under care is
    /// harvested by the gardener, so what is ripe now already waits). UNVERIFIED: growth is assumed even across the
    /// <see cref="GrowDays"/>; rounding up errs on the late side.
    /// </summary>
    public int DaysToHarvest(int growthLevel)
    {
        var left = Math.Max(0, RipeGrowthLevel - growthLevel);
        return Math.Max(1, (int)Math.Ceiling((double)GrowDays * left / RipeGrowthLevel));
    }
}
