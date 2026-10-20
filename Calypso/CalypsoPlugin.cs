using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Status;
using Calypso.Core.Tasks;
using Calypso.Game;
using Calypso.Ipc;
using Calypso.Windows;
using Calypso.Workshop;
using Calypso.Gathering;
using Calypso.Progression;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Calypso;

public sealed class CalypsoPlugin : IDalamudPlugin
{
    // [REPLACED: a second copy of the version that had to be bumped by hand beside the csproj; read it off the assembly like Ariadne]
    // public const string PluginVersion = "0.0.1";
    public static readonly string PluginVersion = typeof(CalypsoPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private const string CommandName = "/calypso";

    /// <summary>Short alias, matching the suite (/daedalus → /dae, /charon → /cha). FFXIV does not prefix-match commands.</summary>
    private const string CommandAlias = "/caly";

    private static readonly TimeSpan StatusPublishInterval = TimeSpan.FromSeconds(30);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private readonly CalypsoConfig _config;
    private readonly IslandData _data;
    private readonly IslandStateReader _reader;
    private readonly PlayerContext _player;
    private readonly WorkshopService _workshop;
    private readonly Probe _probe;
    private readonly MarketPrices _prices;
    private readonly TaskRunner _runner;
    private readonly FleetBoard _fleet = new();
    private readonly RelayClient _relay;
    private readonly CalypsoIpc _ipc;

    private readonly WindowSystem _windowSystem = new("Calypso");
    private readonly MainWindow _mainWindow;
    private readonly StatusOverlay _overlay;

    private IslandSnapshot _island = IslandSnapshot.Empty;
    private CharacterProfile? _profile;
    private IslandCycle _cycle;
    private DateTime _lastStatusPublishUtc = DateTime.MinValue;
    private bool _probeResumeChecked;
    private RunnerState _lastPublishedState = RunnerState.Idle;

    public CalypsoPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IDataManager dataManager,
        IGameGui gameGui,
        IGameInteropProvider interop,
        IAddonLifecycle addonLifecycle,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _framework = framework;
        _log = log;

        _config = pluginInterface.GetPluginConfig() as CalypsoConfig ?? new CalypsoConfig();
        if (_config.Migrate())
            SaveConfig();

        _data = new IslandData(dataManager, log);
        _reader = new IslandStateReader(_data);
        _player = new PlayerContext(clientState, objectTable, condition);

        // Anything that fails below must not leave hooks, IPC gates or sockets behind: Dalamud reported 3 leaked hooks
        // when the cowrie shop threw during load (2026-10-18).
        try
        {
            _workshop = new WorkshopService(new WorkshopData(dataManager, log), new WorkshopGame(), log);
            _probe = new Probe(interop, addonLifecycle, log, pluginInterface.ConfigDirectory.FullName);

            var layout = new IslandLayout(dataManager, log);
            var nav = new NavClient(pluginInterface, log);
            var actions = new IslandActions(pluginInterface, objectTable, gameGui, condition, log);
            var travel = new IslandTravel(nav, actions, layout, dataManager, () => _config.IsleReturnLanding,
                landing => { _config.IsleReturnLanding = landing; SaveConfig(); }, log);
            var tools = new GatherTools(dataManager);
            var gatherRun = new GatherRun(actions, travel, layout, _workshop.Catalog, _config.GatherYields, _data, tools, actions.ItemCount);
            gatherRun.Learned += SaveConfig;
            uint PouchItem(int row) => _workshop.Catalog.Pouch.TryGetValue(row, out var p) ? p.ItemId : 0;
            var progression = new ProgressionTask(layout, _data, actions, gatherRun, new TalkRun(actions, travel), new CraftRun(actions, layout, actions.ItemCount),
                new ModeRun(actions, travel, _data), tools, actions.ItemCount, PouchItem);
            var gatherGoals = new GatherGoalsTask(gatherRun, () => _profile, row => actions.ItemCount(PouchItem(row)));
            // What selling must leave behind: the character's gather goals and the workshop plan's gather targets.
            int Reserved(int pouchRow) => _profile == null ? 0 : Math.Max(
                _profile.Gathering.Goals.GetValueOrDefault(pouchRow),
                _profile.WorkshopWeek.Plan?.GatherTargets.GetValueOrDefault(pouchRow) ?? 0);
            var stackSizes = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            int StackSize(uint itemId) => (int)(stackSizes.GetRowOrDefault(itemId)?.StackSize ?? 999);
            var exportRun = new Exports.ExportRun(actions, travel, layout, new ExportGame(dataManager));
            var exportTask = new ExportTask(exportRun, _workshop.Catalog, () => _profile, actions.ItemCount, StackSize, Reserved);
            var gatherFill = new GatherFillTask(gatherRun, tools, _workshop.Catalog, () => _profile, actions.ItemCount, exportRun, Reserved, StackSize);
            _prices = new MarketPrices(log);
            var cowrieShop = new CowrieShop(dataManager, log);

            // Each system registers its tasks here as it is built, in daily-loop order.
            var unlock = new UnlockTask(new OdysseusQuests(pluginInterface, log), dataManager, log);
            var tasks = new List<IIslandTask>
            {
                unlock, // first: Seeking Sanctuary unlocks Island Sanctuary
                progression,
                gatherGoals,
                exportTask,
                new WorkshopObserveTask(_workshop, () => _profile, () => _cycle),
                new WorkshopAgendaTask(_workshop, () => _profile, () => _cycle),
                gatherFill, // last: the island-XP grind can take hours and must never hold up the rest
            };
            _runner = new TaskRunner(tasks);
            _runner.TaskFinished += OnTaskFinished;

            _relay = new RelayClient(pluginInterface, log);
            _relay.OnMessage += OnRelayMessage;

            _ipc = new CalypsoIpc(pluginInterface,
                () => _runner.IsBusy,
                () => BuildStatus().ToJson(),
                StartDailyLoop,
                RunTask,
                () => Stop("Stopped via IPC"));

            var debug = new DebugView(_data, _player, gameGui, () => _island, _workshop, _probe);
            var workshopView = new WorkshopView(_workshop, _runner, () => _island, () => _profile, () => _cycle, SaveConfig);
            var progressionView = new ProgressionView(progression, layout, _runner, () => _island, _workshop.Catalog, tools);
            var marketView = new MarketView(exportTask, _runner, cowrieShop, _prices, actions, () => _profile, () => _player.HomeWorld, SaveConfig);
            var gatheringView = new GatheringView(gatherGoals, gatherFill, () => _island, _runner, nav, _workshop.Catalog, layout, _config.GatherYields, actions, tools, () => _profile, SaveConfig);
            _mainWindow = new MainWindow(_config, SaveConfig, _runner, _data, () => _island, () => _profile, () => _cycle,
                _fleet, StartDailyLoop, () => Stop("Stopped"), SendFleetControl, debug, workshopView, progressionView, gatheringView, marketView);
            _overlay = new StatusOverlay(_runner, () => _island, () => _config.ShowStatusOverlay && _player.IsLoggedIn);
            _windowSystem.AddWindow(_mainWindow);
            _windowSystem.AddWindow(_overlay);

            var help = new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Calypso. Subcommands: start (do my island), stop.",
            };
            _commandManager.AddHandler(CommandName, help);
            _commandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand) { HelpMessage = $"Alias for {CommandName}.", ShowInHelp = false });

            _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
            _pluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;
            _pluginInterface.UiBuilder.OpenConfigUi += OpenMainWindow;
            _framework.Update += OnUpdate;
        }
        catch
        {
            _probe?.Dispose();
            _prices?.Dispose();
            _ipc?.Dispose();
            _relay?.Dispose();
            _workshop?.Dispose();
            throw;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        try
        {
            _island = _player.IsLoggedIn ? _reader.Read() : IslandSnapshot.Empty;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Island state read failed");
            _island = IslandSnapshot.Empty;
        }

        _cycle = _data.CycleAt(now);
        _profile = ResolveProfile();

        if (_profile == null)
        {
            if (_runner.IsBusy)
                _runner.Stop("Logged out");
        }
        else
        {
            if (_profile.Ledger.Roll(_cycle) | _profile.WorkshopWeek.Roll(_cycle.Week))
                SaveConfig();

            // The agenda agent's groove is the trusted one; remember it for plans made while the agenda is closed.
            if (_island.ScheduleGroove is { } groove)
                _profile.WorkshopWeek.LastGroove = (int)groove;

            _runner.Tick(new TaskContext(_island, _profile.Ledger, now), _player.GlobalBlocker(), framework.UpdateDelta);
        }

        if (!_probeResumeChecked && _player.IsLoggedIn)
        {
            _probeResumeChecked = true;
            _probe.ResumeIfInterrupted(() => _workshop.Catalog.Pouch.Values.Select(p => p.ItemId));
        }

        _probe.Tick();
        PublishStatusIfDue(now);
    }

    private CharacterProfile? ResolveProfile()
    {
        if (!_player.IsLoggedIn)
            return null;

        var contentId = _player.ContentId;
        if (contentId == 0)
            return null;

        var profile = _config.ProfileFor(contentId, _player.Identity, out var changed);
        if (changed)
            SaveConfig();
        return profile;
    }

    private bool StartDailyLoop()
    {
        var profile = _profile;
        if (profile == null)
            return false;

        return _runner.StartLoop(profile.IsTaskEnabled);
    }

    private bool RunTask(string taskId) => _profile != null && _runner.StartSingle(taskId);

    private void Stop(string reason) => _runner.Stop(reason);

    private void OnTaskFinished(string taskId, bool ok, string detail)
    {
        if (ok)
            _log.Information("Task {0} done {1}", taskId, detail);
        else
            _log.Warning("Task {0} failed: {1}", taskId, detail);

        // The ledger lives in the profile — persist each completion/failure so a crash doesn't repeat work.
        SaveConfig();
    }

    private CalypsoStatus BuildStatus() =>
        CalypsoStatus.Build(_runner, _island, _profile?.Ledger ?? new CycleLedger(),
            id => _profile?.IsTaskEnabled(id) ?? false, _player.Identity, PluginVersion, _cycle, DateTime.UtcNow);

    private void PublishStatusIfDue(DateTime now)
    {
        if (!_config.PublishStatusToFleet || _profile == null)
            return;

        if (now - _lastStatusPublishUtc < StatusPublishInterval && _runner.State == _lastPublishedState)
            return;

        _lastStatusPublishUtc = now;
        _lastPublishedState = _runner.State;
        _relay.Publish(RelayChannels.Status, BuildStatus().ToJson());
    }

    private void OnRelayMessage(string channel, string json)
    {
        switch (channel)
        {
            case RelayChannels.Status:
                if (CalypsoStatus.TryParse(json) is { } status)
                    _fleet.Record(status, DateTime.UtcNow);
                break;

            case RelayChannels.Control:
                if (ControlMessage.TryParse(json) is { } msg && _config.AcceptFleetControl && msg.IsFor(_player.Identity))
                    ApplyControl(msg.Act, $"fleet ({msg.From})");
                break;
        }
    }

    /// <summary>Fleet buttons: broadcast to every other box, and apply here too (the relay never echoes to its sender).</summary>
    private void SendFleetControl(string act)
    {
        _relay.Publish(RelayChannels.Control, new ControlMessage { Act = act, From = _player.Identity }.ToJson());
        ApplyControl(act, "fleet (this box)");
    }

    private void ApplyControl(string act, string source)
    {
        if (act == ControlMessage.ActStart)
        {
            if (!StartDailyLoop())
                _log.Information("Ignored start from {0}: busy or not logged in", source);
        }
        else if (act == ControlMessage.ActStop)
        {
            Stop($"Stopped by {source}");
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "start":
                StartDailyLoop();
                break;
            case "stop":
                Stop("Stopped by command");
                break;
            default:
                OpenMainWindow();
                break;
        }
    }

    private void OpenMainWindow() => _mainWindow.IsOpen = true;

    private void SaveConfig() => _pluginInterface.SavePluginConfig(_config);

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _runner.Stop("Plugin unloading");

        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenMainWindow;
        _commandManager.RemoveHandler(CommandName);
        _commandManager.RemoveHandler(CommandAlias);
        _windowSystem.RemoveAllWindows();

        _ipc.Dispose();
        _probe.Dispose();
        _prices.Dispose();
        _workshop.Dispose();
        _relay.OnMessage -= OnRelayMessage;
        _relay.Dispose();
    }
}
