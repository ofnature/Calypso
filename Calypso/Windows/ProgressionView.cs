using System;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Development;
using Calypso.Core.Progression;
using Calypso.Core.Tasks;
using Calypso.Game;
using Calypso.Progression;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Calypso.Windows;

/// <summary>Progression tab: where the island tutorial stands, what Calypso will do about it, and what's next.</summary>
public sealed class ProgressionView
{
    private readonly ProgressionTask _task;
    private readonly DevelopTask _develop;
    private readonly IslandLayout _layout;
    private readonly TaskRunner _runner;
    private readonly Func<IslandSnapshot> _island;
    private readonly Core.Workshop.CraftCatalog _catalog;
    private readonly GatherTools _tools;

    public ProgressionView(ProgressionTask task, DevelopTask develop, IslandLayout layout, TaskRunner runner, Func<IslandSnapshot> island,
        Core.Workshop.CraftCatalog catalog, GatherTools tools)
    {
        _develop = develop;
        _tools = tools;
        _catalog = catalog;
        _task = task;
        _layout = layout;
        _runner = runner;
        _island = island;
    }

    public void Draw()
    {
        var island = _island();

        CalypsoTheme.Section("Island unlock");
        if (UnlockTask.Unlocked)
        {
            CalypsoTheme.Dot(CalypsoTheme.StatusGreen);
            ImGui.TextUnformatted("Seeking Sanctuary complete — the island is unlocked.");
        }
        else
        {
            CalypsoTheme.Dot(CalypsoTheme.StatusYellow);
            ImGui.TextWrapped("Seeking Sanctuary isn't done yet. \"Do my island\" runs it first through Odysseus (it needs the Endwalker story finished).");
        }

        if (!island.Available)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "No island data (not logged in).");
            return;
        }

        var row = ProgressionTask.StepRow(island);
        var step = _layout.Step(row);
        var action = _task.Decide(island);

        CalypsoTheme.Section("Island tutorial");
        CalypsoTheme.Label("Rank:", island.Rank.ToString());
        CalypsoTheme.Label("Step:", $"{row} (CurrentProgress {island.Progress})");
        ImGui.TextWrapped(step?.Objective ?? "—");

        var (color, verb) = action.Kind switch
        {
            ProgressActionKind.Gather => (CalypsoTheme.StatusBlue, "Will gather"),
            ProgressActionKind.Talk => (CalypsoTheme.StatusBlue, "Will talk"),
            ProgressActionKind.Craft => (CalypsoTheme.StatusBlue, "Will craft"),
            ProgressActionKind.Sow => (CalypsoTheme.StatusBlue, "Will sow"),
            ProgressActionKind.Water => (CalypsoTheme.StatusBlue, "Will water"),
            ProgressActionKind.Capture => (CalypsoTheme.StatusBlue, "Will capture"),
            ProgressActionKind.Feed => (CalypsoTheme.StatusBlue, "Will feed"),
            ProgressActionKind.Finished => (CalypsoTheme.StatusGreen, "Done"),
            _ => (CalypsoTheme.StatusYellow, "Blocked"),
        };
        CalypsoTheme.Dot(color);
        ImGui.TextWrapped($"{verb}: {action.Reason}");
        foreach (var (pouch, missing) in action.Missing)
            ImGui.TextColored(CalypsoTheme.TextSecondary, $"   need {missing} more {(_catalog.Pouch.TryGetValue(pouch, out var p) ? p.Name : $"pouch item #{pouch}")}");

        using (ImRaii.Disabled(_runner.IsBusy || action.Kind is ProgressActionKind.Blocked or ProgressActionKind.Finished || !island.InSanctuary))
        {
            if (ImGui.Button("Run the tutorial"))
                _runner.StartSingle(_task.Id);
        }
        CalypsoTheme.Help("Runs step after step until one needs something Calypso can't do yet.\nNeeds Ariadne or vnavmesh, and Charon for dialogue.");

        if (action.Kind == ProgressActionKind.Blocked)
            ImGui.TextColored(CalypsoTheme.TextMuted, "Blocked steps can be done by hand — record them with the Probe on the Debug tab so Calypso can learn them.");

        CalypsoTheme.Section("Next steps");
        foreach (var next in Enumerable.Range(row + 1, 5).Select(_layout.Step).Where(s => s != null))
            ImGui.TextColored(CalypsoTheme.TextSecondary, $"{next!.Row}. {next.Objective}");

        DrawDevelopment(island);

        // [REPLACED: bit readings only — the Isleventory Tools tab is the ground truth to compare them with]
        // if (ImGui.CollapsingHeader("Tools (key item readings, unverified bit index)"))
        // {
        //     foreach (var (keyRow, name, byId, byRowMinusOne) in _layout.KeyItemReadings())
        //         ImGui.TextUnformatted($"{keyRow,2} {name}: bit=row {byId}, bit=row-1 {byRowMinusOne}");
        // }
        if (ImGui.CollapsingHeader("Tools (which source is right?)"))
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "Isleventory = ground truth (open the Isleventory once). The bit reading that matches it is the right one.");
            foreach (var (name, pouch, bitRow, bitRowMinusOne) in _tools.Readings())
                ImGui.TextUnformatted($"{name}: Isleventory {(pouch?.ToString() ?? "not loaded")}, bit=row {bitRow}, bit=row-1 {bitRowMinusOne}");
        }
    }

    /// <summary>The development planner's list: what Calypso does, what's by hand, what's waiting.</summary>
    private void DrawDevelopment(IslandSnapshot island)
    {
        CalypsoTheme.Section("Island development");
        if (!island.Available)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "No island data.");
            return;
        }

        ImGui.TextColored(CalypsoTheme.TextMuted, "After the tutorial: Calypso gathers for these, crafts tools and builds workshops. The rest is by hand until recorded.");
        using (ImRaii.Disabled(_runner.IsBusy))
        {
            if (ImGui.Button("Develop now"))
                _runner.StartSingle(_develop.Id);
        }

        var plan = _develop.Plan(island);
        if (plan.Count == 0)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "Nothing to develop right now.");
            return;
        }

        foreach (var t in plan)
        {
            CalypsoTheme.Dot(t.State switch
            {
                DevelopmentState.Ready => CalypsoTheme.StatusGreen,
                DevelopmentState.NeedsMaterials => CalypsoTheme.StatusYellow,
                DevelopmentState.NeedsRecording => CalypsoTheme.StatusRed,
                _ => CalypsoTheme.TextMuted,
            });
            var missing = t.Missing.Count == 0
                ? string.Empty
                : " — short: " + string.Join(", ", t.Missing.Select(m => $"{m.Missing} {(_catalog.Pouch.TryGetValue(m.PouchRow, out var p) ? p.Name : $"#{m.PouchRow}")}"));
            ImGui.TextWrapped($"{t.Kind}: {t.Name} ({t.Note}){missing}");
        }
    }
}
