using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>
/// Game-data facts Calypso needs, read from Lumina sheets once at load — never hardcoded. Each property says
/// which sheet it comes from; claude-memory.md "Sheets" records what the columns mean.
/// </summary>
public sealed class IslandData
{
    /// <summary>CycleTime row that holds the island's weekly reset (FirstCycle 460800 = Tue 08:00 UTC, Cycle 604800).</summary>
    private const uint WeeklyCycleRow = 2;

    public IslandData(IDataManager data, IPluginLog log)
    {
        var cycle = data.GetExcelSheet<CycleTime>().GetRowOrDefault(WeeklyCycleRow);
        if (cycle is { Cycle: > 0 } c)
        {
            FirstCycle = c.FirstCycle;
            CycleLength = c.Cycle;
        }
        else
        {
            CycleError = $"CycleTime row {WeeklyCycleRow} missing or empty — island day tracking is off.";
            log.Error(CycleError);
        }

        FunctionCount = data.GetExcelSheet<MJIFunction>().Count;

        // MJIRank row N = rank N; ExpToNext 0 marks the max rank (row 20 today).
        var ranks = data.GetExcelSheet<MJIRank>();
        ExpToNext = ranks.ToDictionary(r => (int)r.RowId, r => r.ExpToNext);
        MaxRank = ranks.Where(r => r.RowId > 0 && r.ExpToNext == 0).Select(r => (int)r.RowId).DefaultIfEmpty(ranks.Count - 1).Min();

        HudModeNames = data.GetExcelSheet<MJIHudMode>().ToDictionary(r => r.RowId, r => r.Name.ExtractText());
        HudModeIcons = data.GetExcelSheet<MJIHudMode>().ToDictionary(r => r.RowId, r => r.Icon);

        // The game numbers modes (MJIManager.CurrentMode, and the mode menu's index) in unlock-function order, not by
        // sheet row: probe 2026-10-17 saw Capture (row 8, function 6) as 5. Sorting rows by (Unknown0, RowId) gives
        // Free 0, Gather 1, Sow 2, Water 3, Cull 4, Capture 5, Feed 6, Pet 7, Beckon 8.
        var ordered = data.GetExcelSheet<MJIHudMode>().OrderBy(r => r.Unknown0).ThenBy(r => r.RowId).Select(r => r.RowId).ToList();
        _modeValueByRow = ordered.Select((row, value) => (row, value)).ToDictionary(x => x.row, x => (uint)x.value);
    }

    public long FirstCycle { get; }
    public long CycleLength { get; }

    /// <summary>Non-empty when the cycle sheet could not be read; shown on the Debug tab.</summary>
    public string CycleError { get; } = string.Empty;

    /// <summary>Row count of MJIFunction — how many IsFunctionUnlocked ids exist.</summary>
    public int FunctionCount { get; }

    public int MaxRank { get; }

    public IReadOnlyDictionary<int, uint> ExpToNext { get; }

    public IReadOnlyDictionary<uint, string> HudModeNames { get; }

    /// <summary>MJIHudMode row → icon id; the ContextIconMenu callback names the mode by it.</summary>
    public IReadOnlyDictionary<uint, uint> HudModeIcons { get; }

    /// <summary>MJIHudMode row 1 is Gather (row order: Free, Gather, Sow, Water, Cull, Feed, Pet, Beckon, Capture).</summary>
    public const uint GatherModeRow = 1;

    private readonly Dictionary<uint, uint> _modeValueByRow;

    /// <summary>The game's value for an MJIHudMode row — what CurrentMode reports and the mode menu takes.</summary>
    public uint ModeValue(uint row) => _modeValueByRow.TryGetValue(row, out var v) ? v : row;

    /// <summary>The MJIHudMode row for a game mode value (inverse of <see cref="ModeValue"/>).</summary>
    public uint ModeRow(uint value) => _modeValueByRow.FirstOrDefault(kv => kv.Value == value, new(value, value)).Key;

    public IslandCycle CycleAt(DateTime utc) =>
        CycleLength > 0 ? IslandCycle.FromUtc(utc, FirstCycle, CycleLength) : default;
}
