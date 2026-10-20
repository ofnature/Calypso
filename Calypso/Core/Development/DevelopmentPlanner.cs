using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Development;

/// <summary>
/// Island development after the tutorial stops guiding (step 27 "The Perfect Paradise" completes with 3 workshops,
/// 2 granaries and 4 landmarks; step 29 at rank 12 — claude-memory.md "Step 27 explained"). Everything here comes from
/// the sheets, checked against the community wiki's rank table where noted:
/// - building tiers: MJIBuilding (Unknown1 = rank needed — matches the wiki for every tier);
/// - landmarks: MJILandmark (Unknown2 = village development level needed, Unknown3 = rank needed or 0 — both match);
/// - pathfinders: MJIVillageDevelopment (ENPC, Unknown5 = rank needed — matches for Mk. II-V; Mk. VI reads 12 where the
///   wiki says 15, so it is not trusted);
/// - tools: MJIRecipe key items (the craft log gates them by the recipe-unlocked bits).
/// </summary>
public enum DevelopmentKind
{
    /// <summary>Craft a tool (gathering tool or builder's tools) — automated (CraftRun).</summary>
    CraftTool,

    /// <summary>Construct a first-tier building on a free place — automated while the board offers one kind (ConstructRun).</summary>
    Construct,

    /// <summary>Upgrade the cabin, a workshop or a granary to its next tier.</summary>
    Upgrade,

    /// <summary>Hand builder's tools (and cowries) to the next Pathological Pathfinder: expands the hideaway.</summary>
    Pathfinder,

    /// <summary>Build a landmark on a free landmark place.</summary>
    Landmark,
}

public enum DevelopmentState
{
    /// <summary>Calypso can do it now (materials on hand).</summary>
    Ready,

    /// <summary>Calypso can do it once the listed materials are gathered.</summary>
    NeedsMaterials,

    /// <summary>The materials can be gathered, but the action itself hasn't been recorded yet — do it by hand.</summary>
    NeedsRecording,

    /// <summary>Not yet: a rank, a development level or a building under construction.</summary>
    Waiting,
}

public sealed record DevelopmentTask(
    DevelopmentKind Kind,
    string Name,
    DevelopmentState State,
    string Note,
    IReadOnlyList<(int PouchRow, int Amount)> Materials,
    IReadOnlyList<(int PouchRow, int Missing)> Missing)
{
    /// <summary>CraftTool: the MJIRecipe row. Construct/Upgrade: the MJIBuilding row (0 cabin, 1 workshop, 2 granary).</summary>
    public int Row { get; init; }

    /// <summary>Construct/Upgrade: the tier being built (0 = tier I).</summary>
    public int Tier { get; init; }

    /// <summary>Upgrade: which workshop/granary slot (index into the snapshot's list).</summary>
    public int Slot { get; init; }
}

/// <summary>A building tier (MJIBuilding row/subrow): its name, the rank it needs, its materials.</summary>
public sealed record BuildingTier(int Building, int Tier, string Name, int Rank, IReadOnlyList<(int PouchRow, int Amount)> Materials);

/// <summary>A landmark (MJILandmark): development level and rank it needs, quest gate (UnlockLink, 0 = none), materials.</summary>
public sealed record LandmarkInfo(int Row, string Name, int DevelopmentLevel, int Rank, int UnlockLink, IReadOnlyList<(int PouchRow, int Amount)> Materials);

/// <summary>
/// A pathfinder visit (MJIVillageDevelopment row = the development level it brings): who, the rank it needs, and the
/// builder's tools it takes (Unknown6 = MJIKeyItem row — VERIFIED by name: Mk. II 9 Basic … Mk. VI 16 Bestestest).
/// </summary>
public sealed record PathfinderStep(int Level, uint Npc, string NpcName, int Rank, int ToolsKeyItem);

/// <summary>Why a key-item recipe exists — tools handed to an NPC vanish once given, so only gathering tools are kept.</summary>
public enum ToolPurpose
{
    /// <summary>A gathering tool (an MJIGatheringTool points at its key item): kept forever.</summary>
    Gathering,

    /// <summary>Builder's tools for a pathfinder (MJIVillageDevelopment.Unknown6).</summary>
    Builders,

    /// <summary>Handed to some other NPC (cropworker's, pastureworker's, spelunking) — not planned yet.</summary>
    HandIn,
}

/// <summary>A tool recipe (MJIRecipe with a key item).</summary>
public sealed record ToolRecipe(int Recipe, string Name, int KeyItemRow, bool Unlocked, bool Held, bool Automatable, ToolPurpose Purpose,
    IReadOnlyList<(int PouchRow, int Amount)> Materials);

/// <summary>The character's island as the planner needs it.</summary>
public sealed record DevelopmentInput(
    int Rank,
    int DevelopmentLevel,
    int CabinLevel,
    IReadOnlyList<BuildingState> Workshops,
    IReadOnlyList<BuildingState> Granaries,
    IReadOnlyList<byte> LandmarkIds,
    int FreeFacilityPlaces,
    int FreeLandmarkPlaces,
    Func<int, int> PouchCount);

public static class DevelopmentPlanner
{
    public const int MaxWorkshops = 4;
    public const int MaxGranaries = 2;

    /// <summary>Everything worth doing next, in the order Calypso works through it.</summary>
    public static IReadOnlyList<DevelopmentTask> Plan(
        DevelopmentInput s,
        IReadOnlyList<BuildingTier> tiers,
        IReadOnlyList<LandmarkInfo> landmarks,
        IReadOnlyList<PathfinderStep> pathfinders,
        IReadOnlyList<ToolRecipe> tools)
    {
        var plan = new List<DevelopmentTask>();

        // Materials are counted cumulatively: two tasks needing 20 limestone with 20 on hand aren't both ready.
        var reserved = new Dictionary<int, int>();
        var nextPathfinder = pathfinders.Where(p => p.Level > s.DevelopmentLevel).OrderBy(p => p.Level).FirstOrDefault();

        // 1. Tools: gathering tools open new materials. Builder's tools only for the pathfinder that's due now — they're
        //    handed over and vanish, so "not held" alone would craft them again forever. Other hand-in tools wait.
        foreach (var t in tools.Where(t => t.Unlocked && !t.Held).OrderBy(t => t.Purpose).ThenBy(t => t.Recipe))
        {
            if (t.Purpose == ToolPurpose.HandIn)
                continue;
            if (t.Purpose == ToolPurpose.Builders && (nextPathfinder == null || nextPathfinder.Rank > s.Rank || nextPathfinder.ToolsKeyItem != t.KeyItemRow))
                continue;
            plan.Add(Task(DevelopmentKind.CraftTool, t.Name, t.Materials, s, reserved, t.Automatable ? null : "its recipe takes \"any\" of a category") with { Row = t.Recipe });
        }

        // 2. Upgrades: cabin first (cropland/pasture upgrades wait on it), then workshops, then granaries.
        AddUpgrade(plan, s, reserved, tiers, 0, 0, s.CabinLevel, false, 0);
        for (var i = 0; i < s.Workshops.Count; i++)
        {
            if (s.Workshops[i].IsBuilt)
                AddUpgrade(plan, s, reserved, tiers, 1, i, s.Workshops[i].Level, s.Workshops[i].UnderConstruction, s.Workshops[i].HoursToCompletion);
        }

        for (var i = 0; i < s.Granaries.Count; i++)
        {
            if (s.Granaries[i].IsBuilt)
                AddUpgrade(plan, s, reserved, tiers, 2, i, s.Granaries[i].Level, s.Granaries[i].UnderConstruction, s.Granaries[i].HoursToCompletion);
        }

        // 3. New facilities on free places. Once granaries are unlocked the facility board offers two kinds — that
        //    choice hasn't been recorded, so it's by hand until it is.
        var workshops = s.Workshops.Count(w => w.IsBuilt);
        var granaries = s.Granaries.Count(g => g.IsBuilt);
        var granaryTier = tiers.FirstOrDefault(t => t.Building == 2 && t.Tier == 0);
        var granariesOpen = granaryTier != null && granaryTier.Rank <= s.Rank;
        for (var free = s.FreeFacilityPlaces; free > 0; free--)
        {
            // The wiki's order: a granary as soon as one can be built, workshops otherwise (3 workshops + 2 granaries
            // finishes "The Perfect Paradise").
            var building = granariesOpen && granaries < MaxGranaries && (granaries == 0 || workshops >= 3) ? 2 : workshops < MaxWorkshops ? 1 : -1;
            if (building < 0)
                break;
            var tier = tiers.FirstOrDefault(t => t.Building == building && t.Tier == 0);
            if (tier == null || tier.Rank > s.Rank)
                break;
            plan.Add(Task(DevelopmentKind.Construct, tier.Name, tier.Materials, s, reserved,
                granariesOpen ? "the facility board offers workshops and granaries — that choice isn't recorded yet" : null) with { Row = building });
            if (building == 1)
                workshops++;
            else
                granaries++;
        }

        // 4. The next pathfinder: builder's tools (crafted in step 1) + cowries.
        if (nextPathfinder != null)
        {
            plan.Add(nextPathfinder.Rank > s.Rank
                ? new DevelopmentTask(DevelopmentKind.Pathfinder, nextPathfinder.NpcName, DevelopmentState.Waiting, $"needs rank {nextPathfinder.Rank}",
                    Array.Empty<(int, int)>(), Array.Empty<(int, int)>())
                : Task(DevelopmentKind.Pathfinder, nextPathfinder.NpcName, Array.Empty<(int, int)>(), s, reserved,
                    "hand over the builder's tools and cowries — not recorded yet"));
        }

        // 5. Landmarks on free landmark places: every landmark open at this development level and rank is a choice.
        var built = s.LandmarkIds.Where(id => id != 0).Select(id => (int)id).ToHashSet();
        if (s.FreeLandmarkPlaces > 0)
        {
            foreach (var l in landmarks.Where(l => !built.Contains(l.Row) && l.UnlockLink == 0 && l.DevelopmentLevel <= s.DevelopmentLevel && l.Rank <= s.Rank))
                plan.Add(Task(DevelopmentKind.Landmark, l.Name, l.Materials, s, null, "choose and build a landmark — not recorded yet") with { Row = l.Row });
        }

        return plan;
    }

    private static void AddUpgrade(List<DevelopmentTask> plan, DevelopmentInput s, Dictionary<int, int> reserved, IReadOnlyList<BuildingTier> tiers, int building, int slot,
        int level, bool underConstruction, int hours)
    {
        if (level <= 0)
            return;
        var next = tiers.FirstOrDefault(t => t.Building == building && t.Tier == level); // tier index = current level (I = 0)
        if (next == null)
            return;

        DevelopmentTask task;
        if (underConstruction)
            task = new DevelopmentTask(DevelopmentKind.Upgrade, next.Name, DevelopmentState.Waiting, hours > 0 ? $"under construction ({hours} h left)" : "under construction",
                next.Materials, Array.Empty<(int, int)>());
        else if (next.Rank > s.Rank)
            task = new DevelopmentTask(DevelopmentKind.Upgrade, next.Name, DevelopmentState.Waiting, $"needs rank {next.Rank}", next.Materials, Array.Empty<(int, int)>());
        else
            task = Task(DevelopmentKind.Upgrade, next.Name, next.Materials, s, reserved, "upgrading isn't recorded yet");

        plan.Add(task with { Row = building, Tier = level, Slot = slot });
    }

    /// <param name="reserved">Materials earlier tasks already claim (updated with this task's); null = an alternative
    /// (a landmark choice) that claims nothing.</param>
    /// <param name="byHand">Why Calypso can't do the action itself (null = it can).</param>
    private static DevelopmentTask Task(DevelopmentKind kind, string name, IReadOnlyList<(int PouchRow, int Amount)> materials, DevelopmentInput s,
        Dictionary<int, int>? reserved, string? byHand)
    {
        var missing = materials
            .Select(m => (m.PouchRow, Missing: m.Amount - (s.PouchCount(m.PouchRow) - (reserved?.GetValueOrDefault(m.PouchRow) ?? 0))))
            .Where(m => m.Missing > 0)
            .Select(m => (m.PouchRow, Math.Min(m.Missing, materials.First(x => x.PouchRow == m.PouchRow).Amount)))
            .ToList();
        if (reserved != null)
        {
            foreach (var (row, amount) in materials)
                reserved[row] = reserved.GetValueOrDefault(row) + amount;
        }

        var state = byHand != null ? DevelopmentState.NeedsRecording : missing.Count > 0 ? DevelopmentState.NeedsMaterials : DevelopmentState.Ready;
        return new DevelopmentTask(kind, name, state, byHand ?? (missing.Count > 0 ? "gathering its materials first" : "ready"), materials, missing);
    }
}
