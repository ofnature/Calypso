using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Calypso.Core;
using Calypso.Game;
using Calypso.Workshop;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Calypso.Windows;

/// <summary>
/// Live verification readouts for every value claude-memory.md marks UNVERIFIED. The first in-game session
/// settles them; each section says what to compare against.
/// </summary>
public sealed unsafe class DebugView
{
    private readonly IslandData _data;
    private readonly PlayerContext _player;
    private readonly IGameGui _gameGui;
    private readonly Func<IslandSnapshot> _island;
    private readonly WorkshopService _workshop;
    private readonly Probe _probe;
    private string _probeLabel = string.Empty;

    public DebugView(IslandData data, PlayerContext player, IGameGui gameGui, Func<IslandSnapshot> island, WorkshopService workshop, Probe probe)
    {
        _probe = probe;
        _data = data;
        _player = player;
        _gameGui = gameGui;
        _island = island;
        _workshop = workshop;
    }

    public void Draw()
    {
        DrawProbe();
        var s = _island();

        CalypsoTheme.Section("Identity");
        CalypsoTheme.Label("Character:", _player.Identity);
        CalypsoTheme.Label("ContentId:", _player.ContentId.ToString("X16"));
        CalypsoTheme.Label("Blocker:", _player.GlobalBlocker() ?? "none");

        CalypsoTheme.Section("MJIManager");
        if (!s.Available)
        {
            ImGui.TextColored(CalypsoTheme.StatusYellow, "MJIManager.Instance() is null.");
            return;
        }

        CalypsoTheme.Label("Territory:", $"{s.TerritoryId}   InSanctuary: {s.InSanctuary}");
        CalypsoTheme.Label("Rank / XP:", $"{s.Rank} / {s.Xp}  (to next: {(_data.ExpToNext.TryGetValue(s.Rank, out var next) ? next : 0)}, max rank {_data.MaxRank})");
        CalypsoTheme.Label("Progress:", $"{s.Progress}   Village dev: {s.VillageDevelopmentLevel}   Cabin: {s.CabinLevel}");

        var unlocked = s.FunctionUnlocked.Select((u, i) => (u, i)).Where(x => x.u).Select(x => x.i).ToList();
        CalypsoTheme.Label("Functions:", $"{unlocked.Count}/{s.FunctionUnlocked.Count} unlocked");
        ImGui.TextWrapped(string.Join(", ", unlocked));

        CalypsoTheme.Section("Verify: cycle day");
        ImGui.TextColored(CalypsoTheme.TextMuted, "Compare to the in-game agenda's current cycle. Is CurrentCycleDay 0- or 1-based?");
        CalypsoTheme.Label("MJIManager.CurrentCycleDay:", s.CycleDay.ToString());
        CalypsoTheme.Label("CycleTime sheet:", string.IsNullOrEmpty(_data.CycleError) ? _data.CycleAt(DateTime.UtcNow).ToString() : _data.CycleError);
        CalypsoTheme.Label("Rest days (cycle idx):", string.Join(", ", s.RestDays));

        CalypsoTheme.Section("Verify: groove");
        ImGui.TextColored(CalypsoTheme.TextMuted, "Open the agenda (Craftworks) so the agent has data, then compare with the groove shown in game.");
        CalypsoTheme.Label("ScheduleData.Groove @0x80C:", s.ScheduleGroove?.ToString() ?? "(agenda agent not active)");
        CalypsoTheme.Label("MJIManager.CurrentGroove @0x3F6:", $"{s.ManagerGrooveUnverified}  (untrusted)");
        CalypsoTheme.Label("raw MJIManager+0x3F0..0x3FF:", Hex(0x3F0, 0x10));

        CalypsoTheme.Section("Verify: workshops & granaries");
        ImGui.TextColored(CalypsoTheme.TextMuted, "Check each level against the building's signboard (Workshop III should decode as 3).");
        for (var i = 0; i < s.Workshops.Count; i++)
            CalypsoTheme.Label($"Workshop {i + 1}:", Describe(s.Workshops[i]));
        for (var i = 0; i < s.Granaries.Count; i++)
            CalypsoTheme.Label($"Granary {i + 1}:", Describe(s.Granaries[i]));
        CalypsoTheme.Label("Cropland (raw level):", Describe(s.Farm));
        CalypsoTheme.Label("Pasture (raw level):", Describe(s.Pasture));
        CalypsoTheme.Label("Landmarks:", string.Join(", ", s.LandmarkIds));
        CalypsoTheme.Label("raw IslandState+0xA8 (workshops):", Hex(0x28 + 0xA8, 0x14));
        CalypsoTheme.Label("raw IslandState+0xC8 (granaries):", Hex(0x28 + 0xC8, 0x0A));
        CalypsoTheme.Label("raw IslandState+0x81 (farm|pasture):", Hex(0x28 + 0x81, 0x08));

        CalypsoTheme.Section("Verify: HUD mode switch");
        ImGui.TextColored(CalypsoTheme.TextMuted, "Open the mode menu on the island HUD; note EntryCount with modes locked vs unlocked.");
        CalypsoTheme.Label("CurrentMode:", $"{s.HudMode} ({(_data.HudModeNames.TryGetValue(_data.ModeRow(s.HudMode), out var mode) ? mode : "?")})   item {MJIManager.Instance()->CurrentModeItem}");
        var menu = _gameGui.GetAddonByName("ContextIconMenu");
        CalypsoTheme.Label("ContextIconMenu:", menu.IsNull || !menu.IsVisible
            ? "not open"
            : $"open, EntryCount {((AddonContextIconMenu*)menu.Address)->EntryCount}");

        CalypsoTheme.Section("Workshop demand");
        CalypsoTheme.Label("Popularity row:", $"{s.Popularity} (next {s.NextPopularity})   DemandDirty: {s.DemandDirty}");
        var demand = _workshop.Game.ReadSupplyDemand();
        CalypsoTheme.Label("Supply/demand (crafts 1-8):", demand == null ? "not loaded" : string.Join(" ", demand.Skip(1).Take(8).Select(b => b.ToString("X2"))));

        CalypsoTheme.Section("Verify: agenda");
        ImGui.TextColored(CalypsoTheme.TextMuted, "Open the agenda. Check the entries' slots against the in-game hours, and the rest mask against the rest days you see.");
        if (_workshop.Game.ReadAgenda() is { } agenda)
        {
            CalypsoTheme.Label("Displayed / in progress:", $"C{agenda.CycleDisplayed + 1} / C{agenda.CycleInProgress + 1}   groove {agenda.Groove}");
            CalypsoTheme.Label("RestCycles:", $"0x{agenda.RestCycles:X4}  (this week: {string.Join(",", Enumerable.Range(0, 7).Where(d => (agenda.RestCycles & (1u << d)) != 0).Select(d => $"C{d + 1}"))})");
            for (var w = 0; w < agenda.Workshops.Count; w++)
                CalypsoTheme.Label($"Workshop {w + 1}:", string.Join("  ", agenda.Workshops[w].Select(e => $"{_workshop.Catalog.Get(e.Craft)?.Name ?? "#" + e.Craft}@{e.Slot}")));
        }
        else
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "Agenda not open.");
        }

        CalypsoTheme.Section("Verify: favors");
        var favors = _workshop.Game.Favors(new[] { -1, -1, -1 }, _ => 0);
        ImGui.TextColored(CalypsoTheme.TextMuted, "Required shows -1 until the favors window has been opened (sheet default not used here).");
        foreach (var f in favors)
            ImGui.TextUnformatted($"{_workshop.Catalog.Get(f.CraftId)?.Name ?? "#" + f.CraftId}: left {f.Required}");
        if (favors.Count == 0)
            ImGui.TextColored(CalypsoTheme.TextMuted, "No favor data (or all delivered).");

        CalypsoTheme.Section("Isleventory (first 6)");
        var pouch = _workshop.Game.PouchCounts(_workshop.Catalog);
        ImGui.TextUnformatted(string.Join("   ", _workshop.Catalog.Pouch.Values.Take(6).Select(p => $"{p.Name} {pouch.GetValueOrDefault(p.Row)}")));
    }

    private void DrawProbe()
    {
        CalypsoTheme.Section("Probe (record what the game does)");
        ImGui.TextColored(CalypsoTheme.TextMuted, "Name what you're about to do, press Record, do it by hand once, press Stop.");
        ImGui.SetNextItemWidth(260);
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_probe.Recording))
            ImGui.InputTextWithHint("##probeLabel", "e.g. capture a chocobo with a makeshift net", ref _probeLabel, 200);
        ImGui.SameLine();
        if (!_probe.Recording)
        {
            if (ImGui.Button("Record"))
                _probe.Start(_probeLabel, () => _workshop.Catalog.Pouch.Values.Select(p => p.ItemId));
        }
        else
        {
            ImGui.TextColored(CalypsoTheme.StatusRed, "● REC");
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                _probe.Stop();
        }

        if (!string.IsNullOrEmpty(_probe.CurrentFile))
            ImGui.TextColored(CalypsoTheme.TextMuted, _probe.CurrentFile);

        if (_probe.Recording && ImGui.BeginChild("##probeLines", new System.Numerics.Vector2(-1, 140), true))
        {
            foreach (var line in _probe.Recent.Take(40))
                ImGui.TextUnformatted(line);
        }

        if (_probe.Recording)
            ImGui.EndChild();
    }

    private static string Describe(BuildingState b) =>
        b.IsBuilt || b.Level > 0
            ? $"level {b.Level}{(b.UnderConstruction ? $", building ({b.HoursToCompletion}h left)" : string.Empty)}"
            : "not built";

    /// <summary>Hex of MJIManager bytes [offset, offset+length) — for checking offsets by eye against known in-game values.</summary>
    private static string Hex(int offset, int length)
    {
        var mji = (byte*)MJIManager.Instance();
        if (mji == null)
            return "-";

        var sb = new StringBuilder(length * 3);
        for (var i = 0; i < length; i++)
            sb.Append(mji[offset + i].ToString("X2")).Append(i % 4 == 3 ? "  " : " ");
        return sb.ToString().TrimEnd();
    }
}
