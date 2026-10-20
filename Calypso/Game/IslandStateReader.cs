using System;
using Calypso.Core;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Calypso.Game;

/// <summary>
/// The island state service's game side: copies MJIManager / IslandState / AgentMJICraftSchedule into an
/// immutable <see cref="IslandSnapshot"/> once per framework tick. Every other system reads the snapshot,
/// never the game directly. Fail-open: any null pointer yields <see cref="IslandSnapshot.Empty"/> or leaves
/// that part unset.
///
/// Struct layouts: ClientStructs 78975d8 (shipped with Dalamud 15.0.3.7). Re-check on every Dalamud update.
/// </summary>
public sealed unsafe class IslandStateReader
{
    private readonly IslandData _data;

    public IslandStateReader(IslandData data)
    {
        _data = data;
    }

    public IslandSnapshot Read()
    {
        var mji = MJIManager.Instance();
        if (mji == null)
            return IslandSnapshot.Empty;

        ref var island = ref mji->IslandState;

        var functions = new bool[_data.FunctionCount];
        for (var i = 0; i < functions.Length && i <= byte.MaxValue; i++)
            functions[i] = mji->IsFunctionUnlocked((byte)i);

        var workshops = new BuildingState[4];
        for (var i = 0; i < workshops.Length; i++)
        {
            var w = island.Workshops[i];
            workshops[i] = DecodeBuilding(w.PlaceId, w.BuildingLevel, w.UnderConstruction, w.HoursToCompletion);
        }

        var granaries = new BuildingState[2];
        for (var i = 0; i < granaries.Length; i++)
        {
            var g = island.Granaries[i];
            granaries[i] = DecodeBuilding(g.PlaceId, g.BuildingLevel, g.UnderConstruction, g.HoursToCompletion);
        }

        return new IslandSnapshot
        {
            Available = true,
            InSanctuary = mji->IsPlayerInSanctuary,
            TerritoryId = mji->TerritoryId,
            Rank = island.CurrentRank,
            Xp = island.CurrentXP,
            Progress = island.CurrentProgress,
            VillageDevelopmentLevel = island.VillageDevelopmentLevel,
            CabinLevel = island.CabinLevel,
            FunctionUnlocked = functions,
            Workshops = workshops,
            Granaries = granaries,

            // MJIFarmPasture.Level encoding (0-based like workshops, or 1-based like the cabin) is UNVERIFIED —
            // passed through raw until the Debug tab confirms it against a known cropland level.
            Farm = new BuildingState(island.Farm.Level, island.Farm.UnderConstruction, island.Farm.HoursToCompletion),
            Pasture = new BuildingState(island.Pasture.Level, island.Pasture.UnderConstruction, island.Pasture.HoursToCompletion),
            LandmarkIds = island.LandmarkIds.ToArray(),
            CycleDay = mji->CurrentCycleDay,
            RestDays = mji->CraftworksRestDays.ToArray(),
            Popularity = mji->CurrentPopularity,
            NextPopularity = mji->NextPopularity,
            DemandDirty = mji->DemandDirty,
            ScheduleGroove = ReadScheduleGroove(),
            ManagerGrooveUnverified = mji->CurrentGroove,
            HudMode = mji->CurrentMode,
        };
    }

    /// <summary>
    /// Workshops/granaries: PlaceId 0 = not built; BuildingLevel is stored one less than the real level
    /// (ClientStructs IslandState.cs remarks: "Workshop III will report as 2").
    /// </summary>
    internal static BuildingState DecodeBuilding(byte placeId, byte storedLevel, bool underConstruction, byte hoursToCompletion) =>
        placeId == 0
            ? default
            : new BuildingState((byte)(storedLevel + 1), underConstruction, hoursToCompletion);

    private static uint? ReadScheduleGroove()
    {
        // Only while the agenda agent is active: Data is allocated for the open window, and reading it after the
        // window closes risks a stale pointer.
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive() || agent->Data == null)
            return null;

        return agent->Data->Groove;
    }
}
