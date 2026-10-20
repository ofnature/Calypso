using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Calypso.Core;
using Calypso.Core.Tasks;
using Calypso.Core.Workshop;
using Calypso.Workshop;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Calypso.Windows;

/// <summary>Workshop tab: mode and objective, this week's plan, gather demand, and the pasted-schedule comparison.</summary>
public sealed class WorkshopView
{
    private static readonly string[] ModeNames = { "Off", "Suggest only", "Auto (set agenda)" };
    private static readonly string[] ObjectiveNames = { "Max cowries", "Rank progress (reserve construction materials)" };

    private readonly WorkshopService _service;
    private readonly TaskRunner _runner;
    private readonly Func<IslandSnapshot> _island;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<IslandCycle> _cycle;
    private readonly Action _save;

    private bool _uiPlanning;
    private string _paste = string.Empty;
    private IReadOnlyList<string> _unmatched = Array.Empty<string>();
    private (double Ours, double Pasted)? _comparison;

    public WorkshopView(WorkshopService service, TaskRunner runner, Func<IslandSnapshot> island, Func<CharacterProfile?> profile, Func<IslandCycle> cycle, Action save)
    {
        _service = service;
        _runner = runner;
        _island = island;
        _profile = profile;
        _cycle = cycle;
        _save = save;
    }

    public void Draw()
    {
        var profile = _profile();
        var island = _island();
        if (profile == null)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "Log in to see this character's workshop.");
            return;
        }

        CollectUiPlan(profile);
        var settings = profile.Workshop;
        var week = profile.WorkshopWeek;

        CalypsoTheme.Section("Settings");
        var mode = (int)settings.Mode;
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Mode", ref mode, ModeNames, ModeNames.Length))
        {
            settings.Mode = (WorkshopMode)mode;
            _save();
        }
        CalypsoTheme.Help("Suggest only: plan every day, never touch the in-game agenda.\nAuto: also set tomorrow's agenda and rest days during \"Do my island\".");

        var objective = (int)settings.Objective;
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Objective", ref objective, ObjectiveNames, ObjectiveNames.Length))
        {
            settings.Objective = (WorkshopObjective)objective;
            _save();
        }
        CalypsoTheme.Help("Rank progress keeps the materials for every construction your rank already allows out of the plan.");

        var cost = (float)settings.GatherCostPerUnit;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Gather cost / unit", ref cost, 0f, 10f, "%.1f cowries"))
        {
            settings.GatherCostPerUnit = cost;
            _save();
        }
        CalypsoTheme.Help("How much a unit of material you'd have to go and gather counts against a plan.\n0 = gathering is free; higher = stick to what's in the Isleventory.");

        var skip = settings.SkipFavorsWeek == week.Week;
        if (ImGui.Checkbox("Ignore favors this week", ref skip))
        {
            settings.SkipFavorsWeek = skip ? week.Week : -1;
            _save();
        }

        CalypsoTheme.Section("This week");
        if (!WorkshopService.HasWorkshop(island))
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, island.Available ? "No workshop built yet." : "No island data.");
            return;
        }

        var today = _cycle().Day;
        ImGui.TextColored(CalypsoTheme.TextSecondary, "Demand read:");
        for (var d = 0; d < 7; d++)
        {
            ImGui.SameLine();
            ImGui.TextColored(week.Observed.ContainsKey(d) ? CalypsoTheme.StatusGreen : d <= today ? CalypsoTheme.StatusYellow : CalypsoTheme.TextMuted, $"C{d + 1}");
        }

        CalypsoTheme.Label("Groove:", $"{week.LastGroove}{(island.ScheduleGroove == null ? " (last seen)" : string.Empty)}");

        using (ImRaii.Disabled(_service.IsPlanning || _runner.IsBusy || today >= 6))
        {
            if (ImGui.Button(_service.IsPlanning ? "Planning…" : "Plan now"))
                StartUiPlan(island, profile, today);
        }
        CalypsoTheme.Help("Plan from tomorrow to the end of the week with what's known now. Doesn't touch the agenda.");
        if (!string.IsNullOrEmpty(_service.LastError))
            ImGui.TextColored(CalypsoTheme.StatusRed, _service.LastError);

        DrawPlan(week.Plan, week, island);
        DrawPaste(profile, island, today);
    }

    private void DrawPlan(WeekPlan? plan, WorkshopWeek week, IslandSnapshot island)
    {
        if (plan == null)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "No plan yet — it is made by \"Do my island\" or \"Plan now\".");
            return;
        }

        CalypsoTheme.Label("Expected:", $"{plan.ExpectedValue:0} cowries (planned on C{plan.PlannedOnDay + 1})");
        if (!plan.FavorsMet)
            ImGui.TextColored(CalypsoTheme.StatusYellow, "This plan does not fill every favor.");
        foreach (var w in plan.Warnings)
            ImGui.TextColored(CalypsoTheme.StatusYellow, w);

        var built = island.Workshops.Count(w => w.IsBuilt);
        if (ImGui.BeginTable("plan", 5, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Day", ImGuiTableColumnFlags.WidthFixed, 34);
            ImGui.TableSetupColumn("Crafts", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Sure", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, 26);
            ImGui.TableHeadersRow();

            foreach (var c in plan.Cycles.OrderBy(c => c.Day))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"C{c.Day + 1}");
                ImGui.TableNextColumn();
                if (c.Rest)
                {
                    ImGui.TextColored(CalypsoTheme.TextMuted, "Rest");
                }
                else
                {
                    var mainCount = Math.Min(c.MainCount, built);
                    ImGui.TextWrapped($"{Names(c.Main)}  ×{mainCount}");
                    if (c.Fourth != null && built > mainCount)
                        ImGui.TextWrapped($"{Names(c.Fourth)}  ×{built - mainCount}");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(c.Rest ? "-" : $"{c.Value:0}");
                ImGui.TableNextColumn();
                ImGui.TextColored(c.Confidence >= 0.99 ? CalypsoTheme.StatusGreen : c.Confidence >= 0.5 ? CalypsoTheme.StatusYellow : CalypsoTheme.StatusRed,
                    c.Rest ? "-" : $"{c.Confidence:P0}");
                ImGui.TableNextColumn();
                if (c.Committed)
                    ImGui.TextColored(CalypsoTheme.StatusGreen, "✓");
            }

            ImGui.EndTable();
        }

        if (plan.GatherDemand.Count > 0)
        {
            CalypsoTheme.Section("To gather for this plan");
            foreach (var (row, units) in plan.GatherDemand.OrderByDescending(kv => kv.Value))
            {
                var name = _service.Catalog.Pouch.TryGetValue(row, out var p) ? p.Name : $"pouch #{row}";
                ImGui.TextUnformatted($"{units,4} × {name}");
            }
        }
    }

    private void DrawPaste(CharacterProfile profile, IslandSnapshot island, int today)
    {
        CalypsoTheme.Section("Compare a community schedule");
        ImGui.TextColored(CalypsoTheme.TextMuted, "Paste a schedule (e.g. from Overseas Casuals). Used for comparison only.");
        ImGui.InputTextMultiline("##paste", ref _paste, 8000, new Vector2(-1, 90));

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_paste) || today >= 6))
        {
            if (ImGui.Button("Compare"))
            {
                var week = profile.WorkshopWeek;
                var (pasted, unmatched) = OcScheduleParser.Parse(_paste, _service.Catalog, week.Week);
                week.Pasted = pasted;
                _unmatched = unmatched;

                var input = _service.BuildInput(island, profile, today + 1, null);
                _comparison = (week.Plan == null ? double.NaN : WeekPlanner.Evaluate(input, week.Plan), WeekPlanner.Evaluate(input, pasted));
                _save();
            }
        }

        if (_unmatched.Count > 0)
            ImGui.TextColored(CalypsoTheme.StatusYellow, $"Not recognised: {string.Join(", ", _unmatched.Take(12))}");

        if (_comparison is { } cmp)
        {
            CalypsoTheme.Label("From tomorrow, our model says:", double.IsNaN(cmp.Ours)
                ? $"pasted {cmp.Pasted:0} cowries (no Calypso plan yet)"
                : $"Calypso {cmp.Ours:0} vs pasted {cmp.Pasted:0} cowries");
        }

        if (profile.WorkshopWeek.Pasted is { } p)
        {
            foreach (var c in p.Cycles.OrderBy(c => c.Day))
                ImGui.TextColored(CalypsoTheme.TextSecondary, c.Rest ? $"C{c.Day + 1}: rest" : $"C{c.Day + 1}: {Names(c.Main)}{(c.Fourth != null ? $"  | 4th: {Names(c.Fourth)}" : string.Empty)}");
        }
    }

    private void StartUiPlan(IslandSnapshot island, CharacterProfile profile, int today)
    {
        var input = _service.BuildInput(island, profile, today + 1, null);
        _uiPlanning = _service.StartPlan(input, profile.WorkshopWeek.Week, today);
    }

    /// <summary>Only collects a plan this view started, so it never steals the agenda task's result.</summary>
    private void CollectUiPlan(CharacterProfile profile)
    {
        if (!_uiPlanning || _service.IsPlanning)
            return;

        _uiPlanning = false;
        if (_service.TakeResult() is { } plan)
        {
            profile.WorkshopWeek.Plan = plan;
            _save();
        }
    }

    private string Names(IReadOnlyList<int>? chain) =>
        chain == null ? "-" : string.Join(" → ", chain.Select(id => Short(_service.Catalog.Get(id)?.Name ?? $"#{id}")));

    private static string Short(string name) => name.StartsWith("Isleworks ", StringComparison.Ordinal) ? name["Isleworks ".Length..] : name;
}
