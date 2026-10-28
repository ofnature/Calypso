using System;
using System.Collections.Generic;
using Calypso.Core.Granary;

namespace Calypso.Core.Farm;

/// <summary>
/// One cropland plot as the planner needs it. The game adapter fills it from MJIFarmState's per-slot arrays
/// (SeedType, GrowthLevel, GardenerYield, the UnderCare / WasUnderCare / CareHalted flags).
/// </summary>
public sealed record CropPlot
{
    /// <summary>The slot index Entrust / Dismiss / CollectSingle take (0-19).</summary>
    public required int Slot { get; init; }

    /// <summary>
    /// What grows there: the <see cref="CropSeed.Id"/> (MJICropSeed row), null when the plot is empty. The adapter maps
    /// MJIFarmState.SeedType to it (UNVERIFIED whether SeedType is that row or the seed's item id).
    /// </summary>
    public int? Crop { get; init; }

    /// <summary>MJIFarmState.GrowthLevel, read through <see cref="FarmRules.DaysToHarvest"/>.</summary>
    public int GrowthLevel { get; init; }

    /// <summary>MJIFarmState.GardenerYield: produce the mammet harvested and holds for collection.</summary>
    public int Waiting { get; init; }

    public bool UnderCare { get; init; }

    /// <summary>Today's fee is paid: dismissing and entrusting again costs nothing more today (ClientStructs' note on
    /// the pasture's identical flag; assumed the same for the cropland).</summary>
    public bool WasUnderCare { get; init; }

    /// <summary>
    /// Care suspended (game text: cowries short, seeds exhausted, or 50 yields waiting); it resumes by itself once the
    /// cause clears, so the planner keeps such a plot as it is.
    /// </summary>
    public bool CareHalted { get; init; }
}

public sealed record FarmInput
{
    /// <summary>Cropland level 1-3, 0 = not built (<c>IslandSnapshot.Farm</c>).</summary>
    public required int Level { get; init; }

    public bool UnderConstruction { get; init; }

    /// <summary>
    /// Every plot the cropland has (MJIManager.GetFarmSlotCount), in any order. Plots beyond
    /// <see cref="FarmRules.Slots"/> for <see cref="Level"/> are ignored with a warning.
    /// </summary>
    public required IReadOnlyList<CropPlot> Plots { get; init; }

    /// <summary>Every crop (MJICropSeed joined with MJIItemPouch).</summary>
    public required IReadOnlyList<CropSeed> Crops { get; init; }

    /// <summary>Isleventory count by pouch row before anything is collected — seeds and produce included.</summary>
    public required IReadOnlyDictionary<int, int> OnHand { get; init; }

    /// <summary>
    /// Seafarer's cowries the cropland may spend on care fees (game text: the fee is in seafarer's cowries; the granary
    /// spends the same purse, so the caller splits it).
    /// </summary>
    public required int Cowries { get; init; }

    /// <summary>
    /// What the island wants, from <see cref="FarmNeeds.FromWorkshop"/> and <see cref="GranaryNeeds.FromReservations"/>
    /// (the same list the granary planner takes); needs on rows no crop yields are ignored.
    /// </summary>
    public IReadOnlyList<GranaryNeed> Needs { get; init; } = Array.Empty<GranaryNeed>();

    /// <summary>Item names by pouch row, for the reasons and warnings only. Missing = the crop's name / "pouch N".</summary>
    public IReadOnlyDictionary<int, string>? MaterialNames { get; init; }

    public FarmRules Rules { get; init; } = new();
}

public enum FarmAction
{
    /// <summary>Leave the plot as it is (empty, not ours, or not built).</summary>
    Nothing,

    /// <summary>Entrust(Slot, SeedItemId) on an empty plot: plants one seed and puts it under the mammet's care.</summary>
    Plant,

    /// <summary>
    /// Dismiss(Slot) — after collecting, so CollectSingleAndDismiss when <see cref="PlotDecision.Collect"/> — then
    /// Entrust(Slot, SeedItemId): the need moved to another crop by more than <see cref="FarmRules.ReplantThreshold"/>.
    /// </summary>
    Replant,

    /// <summary>Dismiss(Slot) (CollectSingleAndDismiss when collecting): nothing wants this crop and it costs a fee.</summary>
    Dismiss,

    /// <summary>The crop stays under care: nothing to call beyond the collection; the fee is in the decision.</summary>
    KeepUnderCare,
}

/// <summary>
/// What to do with one plot, in order: collect first when <see cref="Collect"/> (CollectSingle(Slot), or
/// CollectSingleAndDismiss(Slot) for Replant / Dismiss), then <see cref="Action"/>. <see cref="Crop"/> /
/// <see cref="SeedItemId"/> are what Plant / Replant entrust. <see cref="Expected"/> = produce by pouch row the plot
/// should bring over <see cref="FarmRules.HorizonDays"/> (beyond what waits now). <see cref="CowrieCost"/> = today's care
/// fee for this plot.
/// </summary>
public sealed record PlotDecision(
    int Slot,
    bool Collect,
    FarmAction Action,
    int? Crop,
    uint? SeedItemId,
    int CowrieCost,
    IReadOnlyDictionary<int, int> Collected,
    IReadOnlyDictionary<int, int> Discarded,
    IReadOnlyDictionary<int, int> Expected,
    IReadOnlyList<string> Warnings,
    string Reason);

/// <summary>
/// The cropland's day. <see cref="CollectAll"/>: every plot with something waiting is collected and nothing is
/// discarded, so the adapter may call UpdateExpectedTotalYield() then CollectAll(false) instead of one CollectSingle per
/// plot. <see cref="TotalCost"/> = today's care fees.
/// </summary>
public sealed record FarmPlan(IReadOnlyList<PlotDecision> Plots, bool CollectAll, int TotalCost, IReadOnlyList<string> Warnings);
