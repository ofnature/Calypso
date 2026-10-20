using System;
using System.Linq;
using System.Numerics;
using Calypso.Core;
using Calypso.Core.Status;
using Calypso.Core.Tasks;
using Calypso.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Calypso.Windows;

/// <summary>
/// The one main window (agreed layout): a tab per system plus Status and Debug. Systems not built yet show
/// what they will do and, where it applies, that they are waiting on a STOP decision.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly CalypsoConfig _config;
    private readonly Action _save;
    private readonly TaskRunner _runner;
    private readonly IslandData _data;
    private readonly Func<IslandSnapshot> _island;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<IslandCycle> _cycle;
    private readonly FleetBoard _fleet;
    private readonly Func<bool> _startLoop;
    private readonly Action _stop;
    private readonly Action<string> _sendFleetControl;
    private readonly DebugView _debug;
    private readonly WorkshopView _workshop;
    private readonly ProgressionView _progression;
    private readonly GatheringView _gathering;
    private readonly MarketView _market;

    public MainWindow(
        CalypsoConfig config,
        Action save,
        TaskRunner runner,
        IslandData data,
        Func<IslandSnapshot> island,
        Func<CharacterProfile?> profile,
        Func<IslandCycle> cycle,
        FleetBoard fleet,
        Func<bool> startLoop,
        Action stop,
        Action<string> sendFleetControl,
        DebugView debug,
        WorkshopView workshop,
        ProgressionView progression,
        GatheringView gathering,
        MarketView market)
        : base($"Calypso v{CalypsoPlugin.PluginVersion}##CalypsoMain")
    {
        _config = config;
        _save = save;
        _runner = runner;
        _data = data;
        _island = island;
        _profile = profile;
        _cycle = cycle;
        _fleet = fleet;
        _startLoop = startLoop;
        _stop = stop;
        _sendFleetControl = sendFleetControl;
        _debug = debug;
        _workshop = workshop;
        _progression = progression;
        _gathering = gathering;
        _market = market;

        Size = new Vector2(560, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(1400, 1200),
        };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("CalypsoTabs"))
            return;

        Tab("Status", DrawStatus);
        Tab("Daily Loop", DrawDailyLoop);
        // [REPLACED: placeholder until the workshop solver existed]
        // Tab("Workshop", () => Planned("Agenda solver (max cowries / rank progress toggle, favors as a constraint), suggest-only mode, and setting the agenda in game."));
        Tab("Workshop", _workshop.Draw);
        // [REPLACED: placeholder until gathering existed]
        // Tab("Gathering", () => Planned("Route gathering from game-data node positions plus imported visland routes; \"gather until X of Y\" and \"fill the workshop plan\" targets; Isleventory caps."));
        Tab("Gathering", _gathering.Draw);
        Tab("Farm & Pasture", () =>
        {
            Planned("Granary expeditions; cropland plant/collect; pasture leavings.");
            ImGui.TextColored(CalypsoTheme.StatusYellow, "Waiting on a decision: pasture capture, entrust, feed and release are not reverse-engineered.");
        });
        // [REPLACED: placeholder until selling and the cowrie shop existed]
        // Tab("Exports", () => Planned("Rule-based selling at the Enterprising Exporter: keep N, sell the rest, never sell what the agenda or rank tasks need."));
        Tab("Exports & Shop", _market.Draw);
        // [REPLACED: placeholder until the tutorial runner existed]
        // Tab("Progression", () => Planned("Island quests and rank-ups, first arrival to max rank."));
        Tab("Progression", _progression.Draw);
        Tab("Layout", () =>
        {
            Planned("Building and landmark layout planning.");
            ImGui.TextColored(CalypsoTheme.StatusYellow, "Waiting on a decision: placement and upgrades are not reverse-engineered.");
        });
        Tab("Debug", _debug.Draw);

        ImGui.EndTabBar();
    }

    private static void Tab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label))
            return;

        if (ImGui.BeginChild($"##{label}", Vector2.Zero))
            draw();
        ImGui.EndChild();
        ImGui.EndTabItem();
    }

    private void DrawStatus()
    {
        var island = _island();

        CalypsoTheme.Section("Runner");
        var (color, label) = _runner.State switch
        {
            RunnerState.Running => (CalypsoTheme.StatusGreen, "Running"),
            RunnerState.Waiting => (CalypsoTheme.StatusYellow, "Waiting"),
            _ => (CalypsoTheme.StatusGrey, "Idle"),
        };
        CalypsoTheme.Dot(color);
        ImGui.TextUnformatted(_runner.IsBusy ? $"{label}: {_runner.Detail}" : label);
        if (!string.IsNullOrEmpty(_runner.LastResult))
            ImGui.TextColored(CalypsoTheme.TextSecondary, $"Last: {_runner.LastResult}");

        using (ImRaii.Disabled(_runner.IsBusy || _profile() == null))
        {
            if (ImGui.Button("Do my island"))
                _startLoop();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!_runner.IsBusy))
        {
            if (ImGui.Button("Stop"))
                _stop();
        }

        CalypsoTheme.Section("Island");
        if (!island.Available)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "No island data (not logged in).");
        }
        else
        {
            var toNext = _data.ExpToNext.TryGetValue(island.Rank, out var n) ? n : 0;
            CalypsoTheme.Label("Rank:", island.Rank >= _data.MaxRank ? $"{island.Rank} (max)" : $"{island.Rank} / {_data.MaxRank}   XP {island.Xp} / {toNext}");
            CalypsoTheme.Label("Cycle:", _cycle().ToString());
            CalypsoTheme.Label("On the island:", island.InSanctuary ? "yes" : "no");
            CalypsoTheme.Label("Workshops:", string.Join("  ", island.Workshops.Select(w => w.IsBuilt ? $"{w.Level}" : "-")));
        }

        DrawFleet();

        CalypsoTheme.Section("Settings");
        var overlay = _config.ShowStatusOverlay;
        if (ImGui.Checkbox("Status overlay", ref overlay))
        {
            _config.ShowStatusOverlay = overlay;
            _save();
        }

        var publish = _config.PublishStatusToFleet;
        if (ImGui.Checkbox("Share status with the fleet", ref publish))
        {
            _config.PublishStatusToFleet = publish;
            _save();
        }
        CalypsoTheme.Help("Broadcast this box's status over the Daedalus LAN relay.\nDoes nothing while Daedalus or its LAN coordinator is off.");

        var accept = _config.AcceptFleetControl;
        if (ImGui.Checkbox("Obey fleet start/stop", ref accept))
        {
            _config.AcceptFleetControl = accept;
            _save();
        }
        CalypsoTheme.Help("Let other boxes start or stop this box's daily loop over the LAN relay.");
    }

    private void DrawFleet()
    {
        var boxes = _fleet.Current(DateTime.UtcNow);
        CalypsoTheme.Section($"Fleet ({boxes.Count})");

        if (ImGui.SmallButton("Start all"))
            _sendFleetControl(ControlMessage.ActStart);
        ImGui.SameLine();
        if (ImGui.SmallButton("Stop all"))
            _sendFleetControl(ControlMessage.ActStop);
        CalypsoTheme.Help("Sends to every box with \"Obey fleet start/stop\" on. This box is included.");

        if (boxes.Count == 0)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "No other boxes heard (needs Daedalus with its LAN coordinator on).");
            return;
        }

        if (!ImGui.BeginTable("fleet", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 12);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Doing", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var (status, _) in boxes)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(status.State switch
            {
                "running" => CalypsoTheme.StatusGreen,
                "waiting" => CalypsoTheme.StatusYellow,
                _ => CalypsoTheme.StatusGrey,
            }, "●");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(status.Character);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(status.Rank.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(status.Busy ? status.Detail : status.LastResult);
        }

        ImGui.EndTable();
    }

    private void DrawDailyLoop()
    {
        var profile = _profile();
        if (profile == null)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "Log in to see this character's loop.");
            return;
        }

        CalypsoTheme.Section($"Tasks for {profile.DisplayName}");
        if (_runner.Tasks.Count == 0)
        {
            ImGui.TextColored(CalypsoTheme.TextMuted, "No tasks registered yet: each system adds its tasks as it is built.");
        }
        else if (ImGui.BeginTable("tasks", 5, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 12);
            ImGui.TableSetupColumn("Task", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableHeadersRow();

            foreach (var (task, check) in _runner.Evaluate(_island(), profile.Ledger))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var enabled = profile.IsTaskEnabled(task.Id);
                if (ImGui.Checkbox($"##on{task.Id}", ref enabled))
                {
                    profile.SetTaskEnabled(task.Id, enabled);
                    _save();
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(check.State switch
                {
                    TaskState.Ready => CalypsoTheme.StatusBlue,
                    TaskState.Done => CalypsoTheme.StatusGreen,
                    TaskState.Blocked => CalypsoTheme.StatusYellow,
                    _ => CalypsoTheme.StatusGrey,
                }, "●");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(task.Name);
                ImGui.TableNextColumn();
                var failures = profile.Ledger.Failures(task.Id);
                ImGui.TextColored(CalypsoTheme.TextSecondary,
                    $"{check.State}{(string.IsNullOrEmpty(check.Reason) ? string.Empty : $": {check.Reason}")}{(failures > 0 ? $"  ({failures} failed today)" : string.Empty)}");

                ImGui.TableNextColumn();
                using (ImRaii.Disabled(_runner.IsBusy))
                {
                    if (ImGui.SmallButton($"Run##{task.Id}"))
                        _runner.StartSingle(task.Id);
                }
            }

            ImGui.EndTable();
        }

        CalypsoTheme.Section("Recent");
        if (_runner.History.Count == 0)
            ImGui.TextColored(CalypsoTheme.TextMuted, "Nothing has run yet.");
        foreach (var r in _runner.History)
        {
            CalypsoTheme.Dot(r.Succeeded ? CalypsoTheme.StatusGreen : CalypsoTheme.StatusRed);
            ImGui.TextUnformatted($"{r.Utc.ToLocalTime():HH:mm:ss}  {r.TaskName}{(string.IsNullOrEmpty(r.Detail) ? string.Empty : $": {r.Detail}")}");
        }
    }

    private static void Planned(string what)
    {
        ImGui.TextColored(CalypsoTheme.TextMuted, "Not built yet.");
        ImGui.TextWrapped(what);
    }
}
