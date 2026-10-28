using System;
using System.Collections.Generic;

namespace Calypso.Core.Granary;

/// <summary>
/// One granary as the planner needs it. The game adapter fills it from <c>IslandSnapshot.Granaries[i]</c>
/// (<see cref="BuildingState"/>) and <c>MJIGranariesState.Granary[i]</c>.
/// </summary>
public sealed record GranarySlot
{
    /// <summary>The granary index CollectResources / SelectExpeditionCommit take (0 or 1).</summary>
    public required int Index { get; init; }

    /// <summary>Real level 1-5, 0 = not built (<see cref="BuildingState.Level"/>).</summary>
    public required int Level { get; init; }

    public bool UnderConstruction { get; init; }

    /// <summary>
    /// The current expedition's area (MJIGranaryState.ActiveExpeditionId), null when there is none. Area 0 is a real
    /// area, so the adapter must decide "none" from RemainingDays and the waiting counts, never from the id being 0.
    /// </summary>
    public int? Area { get; init; }

    /// <summary>MJIGranaryState.RemainingDays — daily payouts still to come; 0 = home.</summary>
    public int DaysRemaining { get; init; }

    /// <summary>Gathered and not yet collected, by pouch row: RareResourcePouchId/Count plus the 20 normal ids/counts.</summary>
    public IReadOnlyDictionary<int, int> Waiting { get; init; } = new Dictionary<int, int>();

    /// <summary>Out on an expedition that still pays out.</summary>
    public bool Away => Area != null && DaysRemaining > 0;
}

/// <summary>Why the island wants a material — the planner serves them in this order (see <see cref="GranaryRules.Weight"/>).</summary>
public enum NeedSource
{
    /// <summary>A hard-capped rare this week's workshop plan would use and cannot (see <see cref="GranaryNeeds.FromWorkshop"/>).</summary>
    WorkshopThisWeek,

    WorkshopNextWeek,

    /// <summary>Construction / upgrade costs set aside (<c>MaterialReservation</c>) that the pouch does not cover.</summary>
    Construction,

    /// <summary>A gatherable the week's plan is short of that gathering is slow to bring.</summary>
    SlowGather,

    /// <summary>The planner's own: every rare kept at <see cref="GranaryRules.RareFloor"/>.</summary>
    StockFloor,
}

/// <summary>
/// <see cref="Amount"/> more of a pouch row than the pouch holds now, wanted within <see cref="DueInDays"/> daily
/// payouts (0 = only what already waits in a granary can help). Needs are additive: two needs on one row ask for both.
/// </summary>
public sealed record GranaryNeed(int PouchRow, int Amount, int DueInDays, NeedSource Source);

public sealed record GranaryInput
{
    public required IReadOnlyList<GranarySlot> Granaries { get; init; }

    public required IReadOnlyList<ExpeditionArea> Areas { get; init; }

    /// <summary>Area ids the character can send to (read from the game, or <see cref="GranaryRules.AreasForRank"/>).</summary>
    public required IReadOnlySet<int> UnlockedAreas { get; init; }

    /// <summary>Isleventory count by pouch row, before anything is collected.</summary>
    public required IReadOnlyDictionary<int, int> OnHand { get; init; }

    /// <summary>Seafarer's cowries the granaries may spend today (the caller keeps back whatever else needs them).</summary>
    public required int Cowries { get; init; }

    public IReadOnlyList<GranaryNeed> Needs { get; init; } = Array.Empty<GranaryNeed>();

    /// <summary>
    /// Normal resources the character has gathered by hand at least once — the only ones an expedition brings
    /// (UNVERIFIED, wiki). Null = assume every one.
    /// </summary>
    public IReadOnlySet<int>? GatheredBefore { get; init; }

    /// <summary>Item names by pouch row, for the reasons and warnings only. Missing = "pouch N".</summary>
    public IReadOnlyDictionary<int, string>? MaterialNames { get; init; }

    public GranaryRules Rules { get; init; } = new();
}

public enum GranaryAction
{
    Nothing,

    /// <summary>SelectExpeditionCommit(Index, Area, Days) on a granary that is home.</summary>
    Dispatch,

    /// <summary>Same area, length back to <see cref="GranaryDecision.Days"/> (the maximum); needs every material claimed.</summary>
    Extend,

    /// <summary>New area, same days left (<see cref="GranaryDecision.Days"/> = the current RemainingDays).</summary>
    Change,
}

/// <summary>
/// What to do with one granary, in order: collect first when <see cref="Collect"/>, then <see cref="Action"/>. For
/// Dispatch / Extend / Change the adapter calls SelectExpeditionCommit(Index, Area, Days).
/// </summary>
public sealed record GranaryDecision(
    int Index,
    bool Collect,
    GranaryAction Action,
    int? Area,
    int Days,
    int CowrieCost,
    IReadOnlyDictionary<int, int> Collected,
    IReadOnlyDictionary<int, int> Discarded,
    IReadOnlyDictionary<int, int> Expected,
    IReadOnlyList<string> Warnings,
    string Reason);

public sealed record GranaryPlan(IReadOnlyList<GranaryDecision> Granaries, int TotalCost, IReadOnlyList<string> Warnings);
