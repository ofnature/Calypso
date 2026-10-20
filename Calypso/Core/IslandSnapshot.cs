using System;
using System.Collections.Generic;

namespace Calypso.Core;

/// <summary>
/// One frame's immutable view of the island, built by <c>Game.IslandStateReader</c> and read by every
/// system (tasks, solver, UI, IPC). No Dalamud or ClientStructs types in here, so everything that
/// consumes it is unit-testable.
///
/// Field sources (verified against the shipped ClientStructs, see claude-memory.md "Core MJI state"):
/// MJIManager + IslandState for everything except <see cref="ScheduleGroove"/>, which comes from
/// AgentMJICraftSchedule.Data and is only present while that agent has data.
/// </summary>
public sealed record IslandSnapshot
{
    /// <summary>A snapshot that says "nothing is known" — not logged in, or MJIManager is null.</summary>
    public static readonly IslandSnapshot Empty = new();

    /// <summary>MJIManager.Instance() was non-null when this snapshot was taken.</summary>
    public bool Available { get; init; }

    /// <summary>MJIManager.IsPlayerInSanctuary — standing on the island right now.</summary>
    public bool InSanctuary { get; init; }

    public ushort TerritoryId { get; init; }

    // ---- Progression (IslandState) ----

    public byte Rank { get; init; }
    public uint Xp { get; init; }

    /// <summary>IslandState.CurrentProgress — an MJIProgress row; the gate MJIFunction.Unknown4 checks.</summary>
    public byte Progress { get; init; }

    public byte VillageDevelopmentLevel { get; init; }
    public byte CabinLevel { get; init; }

    /// <summary>
    /// IsFunctionUnlocked(i) for every MJIFunction row i. This is the island's IsActionLearned:
    /// every action Calypso takes is gated on one of these (see <see cref="IsFunctionUnlocked"/>).
    /// </summary>
    public IReadOnlyList<bool> FunctionUnlocked { get; init; } = Array.Empty<bool>();

    // ---- Buildings (IslandState) — levels are stored as level-1 in game memory, decoded here ----

    public IReadOnlyList<BuildingState> Workshops { get; init; } = Array.Empty<BuildingState>();
    public IReadOnlyList<BuildingState> Granaries { get; init; } = Array.Empty<BuildingState>();
    public BuildingState Farm { get; init; }
    public BuildingState Pasture { get; init; }

    /// <summary>IslandState.LandmarkIds — MJILandmark row per landmark slot, 0 = empty.</summary>
    public IReadOnlyList<byte> LandmarkIds { get; init; } = Array.Empty<byte>();

    // ---- Workshop cycle (MJIManager) ----

    /// <summary>MJIManager.CurrentCycleDay. Range/base (0- or 1-based) is UNVERIFIED — Debug tab compares it to <see cref="IslandCycle"/>.</summary>
    public byte CycleDay { get; init; }

    /// <summary>MJIManager.CraftworksRestDays — cycle indices 0-13 (this week 0-6, next week 7-13).</summary>
    public IReadOnlyList<byte> RestDays { get; init; } = Array.Empty<byte>();

    /// <summary>MJICraftworksPopularity row for this week / next week. Unset while <see cref="DemandDirty"/>.</summary>
    public byte Popularity { get; init; }
    public byte NextPopularity { get; init; }

    /// <summary>MJIManager.DemandDirty — supply/demand/popularity below are not populated yet.</summary>
    public bool DemandDirty { get; init; }

    /// <summary>AgentMJICraftSchedule.Data->Groove (u32 @0x80C) — the trusted groove source. Null when the agent has no data.</summary>
    public uint? ScheduleGroove { get; init; }

    /// <summary>
    /// MJIManager.CurrentGroove (u32 @0x3F6). UNTRUSTED: shifted blindly in 6.5 and never re-checked upstream.
    /// Kept only so the Debug tab can show it next to <see cref="ScheduleGroove"/>. Do not consume.
    /// </summary>
    public uint ManagerGrooveUnverified { get; init; }

    // ---- HUD ----

    /// <summary>MJIManager.CurrentMode — an MJIHudMode row (0 Free, 1 Gather, ... 8 Capture).</summary>
    public uint HudMode { get; init; }

    /// <summary>
    /// The island's equivalent of Daedalus's IsActionLearned: true only when the game reports this MJIFunction
    /// row unlocked. Unknown rows (snapshot not available, row out of range) are treated as LOCKED, so a missing
    /// read can never make Calypso try something the character cannot do.
    /// </summary>
    public bool IsFunctionUnlocked(int functionId) =>
        functionId >= 0 && functionId < FunctionUnlocked.Count && FunctionUnlocked[functionId];
}

/// <summary>A workshop/granary/farm/pasture slot. <see cref="Level"/> is the real level (1-based), 0 = not built.</summary>
public readonly record struct BuildingState(byte Level, bool UnderConstruction, byte HoursToCompletion)
{
    public bool IsBuilt => Level > 0;
}
