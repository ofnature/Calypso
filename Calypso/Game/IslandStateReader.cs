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

    // IslandState offsets of MJIWorkshops / MJIGranaries (ClientStructs 78975d8: 0xA8, 0xC8 — the struct offsets are right,
    // the fields inside are not; see ReadBuildings).
    private const int WorkshopsOffset = 0xA8;
    private const int GranariesOffset = 0xC8;

    /// <summary>
    /// The real MJIWorkshops/MJIGranaries layout (raw dump 2026-10-19): an 8-byte pointer, then five byte arrays of
    /// <paramref name="count"/> — PlaceId, GlamourLevel, HoursToCompletion, BuildingLevel, UnderConstruction. That fills
    /// the struct's declared size exactly (0x08 + 5 × count) and keeps the granaries (0xC8) and CabinLevel (0xE0) clear.
    /// </summary>
    private static BuildingState[] ReadBuildings(byte* fields, int count)
    {
        var result = new BuildingState[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = DecodeBuilding(fields[i], fields[3 * count + i], fields[4 * count + i] != 0, fields[2 * count + i]);
        }

        return result;
    }

    /// <summary>
    /// IslandState bytes 0x81–0xE1 in hex (farm/pasture, landmarks, workshops, granaries, cabin), for checking the
    /// MJIWorkshops layout: ClientStructs declares it Size 0x08 + 5×4 with only 20 bytes of fields ("TODO: make layout
    /// explicit"), and Saar's island read as having a workshop it doesn't have (2026-10-19).
    /// </summary>
    public static string RawBuildingBytes()
    {
        var mji = MJIManager.Instance();
        if (mji == null)
            return "(no MJIManager)";
        var p = (byte*)&mji->IslandState;
        var sb = new System.Text.StringBuilder();
        for (var off = 0x80; off <= 0xE1; off += 0x10)
        {
            sb.Append($" {off:X2}:");
            for (var i = off; i < off + 0x10 && i <= 0xE1; i++)
                sb.Append($" {p[i]:X2}");
        }

        return sb.ToString();
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

        // [REPLACED: ClientStructs' MJIWorkshops/MJIGranaries put the fields at +0, but the first 8 bytes are a pointer —
        // Saar's island (no workshop) read 0xA8 = 58 1D 7E 22 F7 7F 00 00 and showed four "workshops" on places
        // 88/29/126/34 (raw dump 2026-10-19 22:42). Fields start at +8: see ReadBuildings.]
        // var workshops = new BuildingState[4];
        // for (var i = 0; i < workshops.Length; i++)
        // {
        //     var w = island.Workshops[i];
        //     workshops[i] = DecodeBuilding(w.PlaceId, w.BuildingLevel, w.UnderConstruction, w.HoursToCompletion);
        // }
        //
        // var granaries = new BuildingState[2];
        // for (var i = 0; i < granaries.Length; i++)
        // {
        //     var g = island.Granaries[i];
        //     granaries[i] = DecodeBuilding(g.PlaceId, g.BuildingLevel, g.UnderConstruction, g.HoursToCompletion);
        // }
        var raw = (byte*)&mji->IslandState;
        var workshops = ReadBuildings(raw + WorkshopsOffset + 8, 4);
        var granaries = ReadBuildings(raw + GranariesOffset + 8, 2);

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
            : new BuildingState((byte)(storedLevel + 1), underConstruction, hoursToCompletion, placeId);

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
