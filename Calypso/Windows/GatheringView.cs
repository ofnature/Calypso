using System;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Gathering;
using Calypso.Core.Tasks;
using Calypso.Core.Workshop;
using Calypso.Game;
using Calypso.Gathering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Calypso.Windows;

/// <summary>Gathering tab: navigation status, this character's goals, and what each node type has been seen to give.</summary>
public sealed class GatheringView
{
    private readonly GatherGoalsTask _task;
    private readonly GatherFillTask _fill;
    private readonly Func<IslandSnapshot> _island;
    private readonly TaskRunner _runner;
    private readonly NavClient _nav;
    private readonly CraftCatalog _catalog;
    private readonly IslandLayout _layout;
    private readonly YieldBook _book;
    private readonly IslandActions _act;
    private readonly GatherTools _tools;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Action _save;

    private int _addIndex;
    private int _addCount = 20;

    public GatheringView(GatherGoalsTask task, GatherFillTask fill, Func<IslandSnapshot> island, TaskRunner runner, NavClient nav, CraftCatalog catalog, IslandLayout layout,
        YieldBook book, IslandActions act, GatherTools tools, Func<CharacterProfile?> profile, Action save)
    {
        _fill = fill;
        _island = island;
        _tools = tools;
        _task = task;
        _runner = runner;
        _nav = nav;
        _catalog = catalog;
        _layout = layout;
        _book = book;
        _act = act;
        _profile = profile;
        _save = save;
    }

    public void Draw()
    {
        CalypsoTheme.Section("Navigation");
        var provider = _nav.Provider;
        CalypsoTheme.Dot(provider.Length > 0 ? CalypsoTheme.StatusGreen : CalypsoTheme.StatusYellow);
        ImGui.TextUnformatted(provider.Length > 0 ? $"{provider} ready" : _nav.Unavailable);
        CalypsoTheme.Label("Gathering spots on the island:", $"{_layout.GatherSpots.Count}   loaded nearby: {_act.NearbyGatheringPoints(_layout.NodeBaseIds).Count()}");
        CalypsoTheme.Label("Tools held:", _tools.Describe(_tools.Signature()));

        var profile = _profile();
        if (profile == null)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "Log in to set goals.");
            return;
        }

        CalypsoTheme.Section("Goals (keep at least)");
        var gatherables = _catalog.Pouch.Values.Where(p => p.Kind == MaterialKind.Gatherable).OrderBy(p => p.Name).ToList();
        var goals = profile.Gathering.Goals;
        foreach (var (row, goal) in goals.ToList())
        {
            var name = _catalog.Pouch.TryGetValue(row, out var p) ? p.Name : $"#{row}";
            var have = _catalog.Pouch.TryGetValue(row, out var pd) ? _act.ItemCount(pd.ItemId) : 0;
            var needs = _catalog.Pouch.TryGetValue(row, out var pt) ? _tools.MissingTool(pt.ItemId) : null;
            ImGui.TextColored(have >= goal ? CalypsoTheme.StatusGreen : needs != null ? CalypsoTheme.TextMuted : CalypsoTheme.TextPrimary,
                $"{have,4} / {goal,-4} {name}{(needs != null ? $"  (needs {needs})" : string.Empty)}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"x##goal{row}"))
            {
                goals.Remove(row);
                _save();
            }
        }

        if (gatherables.Count > 0)
        {
            _addIndex = Math.Clamp(_addIndex, 0, gatherables.Count - 1);
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##addGoal", gatherables[_addIndex].Name))
            {
                for (var i = 0; i < gatherables.Count; i++)
                {
                    if (ImGui.Selectable(gatherables[i].Name, i == _addIndex))
                        _addIndex = i;
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputInt("##addCount", ref _addCount);
            _addCount = Math.Clamp(_addCount, 1, 999);
            ImGui.SameLine();
            if (ImGui.Button("Add goal"))
            {
                goals[gatherables[_addIndex].Row] = _addCount;
                _save();
            }
        }

        var forPlan = profile.Gathering.ForWorkshopPlan;
        if (ImGui.Checkbox("Also gather what the workshop plan is short of", ref forPlan))
        {
            profile.Gathering.ForWorkshopPlan = forPlan;
            _save();
        }

        var targets = _task.Targets(profile);
        using (ImRaii.Disabled(_runner.IsBusy || targets.Count == 0))
        {
            if (ImGui.Button("Gather now"))
                _runner.StartSingle(_task.Id);
        }

        CalypsoTheme.Section("Island XP: gather everything");
        var fillAll = profile.Gathering.FillAll;
        if (ImGui.Checkbox("Gather everything I can, up to", ref fillAll))
        {
            profile.Gathering.FillAll = fillAll;
            _save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        var fillTo = profile.Gathering.FillTo;
        if (ImGui.InputInt("each##fillTo", ref fillTo))
        {
            profile.Gathering.FillTo = Math.Clamp(fillTo, 1, 999);
            _save();
        }
        CalypsoTheme.Help("Runs last in \"Do my island\", after the tutorial and the workshop.\nEvery item your tools allow (caves once the tutorial opens them), capped at its stack size.\nWithout tools a node gives 1 item, so big numbers take a long time — stop it whenever.");

        var fillTargets = _fill.Targets(profile, _island());
        var fillShort = fillTargets.Sum(t => Math.Max(0, t.Value - (_catalog.Pouch.TryGetValue(t.Key, out var fp) ? _act.ItemCount(fp.ItemId) : 0)));
        ImGui.TextColored(CalypsoTheme.TextSecondary, $"{fillTargets.Count} items in scope, {fillShort} items to go");
        using (ImRaii.Disabled(_runner.IsBusy || fillShort == 0))
        {
            if (ImGui.Button("Start the grind now"))
                _runner.StartSingle(_fill.Id);
        }

        CalypsoTheme.Section("What nodes give (learned, per set of tools)");
        if (_book.Observed.Count == 0)
            ImGui.TextColored(CalypsoTheme.TextMuted, "Nothing learned yet — every gather teaches Calypso what that node type drops.");
        foreach (var (node, bySig) in _book.Observed.OrderBy(v => v.Key))
        {
            foreach (var (sig, obs) in bySig.OrderBy(s => s.Key.Length))
            {
                var text = string.Join(", ", obs.Items.OrderByDescending(i => i.Value).Select(i =>
                    $"{_catalog.Pouch.Values.FirstOrDefault(p => p.ItemId == i.Key)?.Name ?? $"#{i.Key}"} {i.Value / (double)Math.Max(1, obs.Visits):0.#}"));
                ImGui.TextWrapped($"{node} ({obs.Visits}×, {_tools.Describe(sig)}): {(text.Length == 0 ? "nothing" : text)}");
            }
        }
    }
}
