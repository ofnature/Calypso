using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Calypso.Game;

/// <summary>
/// Reverse-engineering probe (unparked 2026-10-17 for capture, feed, and the progression windows): while recording, it
/// logs what the game does when the player acts by hand, so Calypso can later do the same thing.
///
/// Logged, one JSON object per line in <c>&lt;config&gt;/probe/probe-&lt;time&gt;.jsonl</c>:
/// - every addon callback (AtkUnitBase.FireCallback): addon name + the AtkValues;
/// - every ReceiveEvent on the island's agents (MJI* AgentIds): event kind + values;
/// - every TargetSystem.InteractWithObject and ActionManager.UseAction: target / action ids;
/// - every window that opens or closes (addon lifecycle PostSetup / PreFinalize) — the sequence of windows a flow
///   like construction passes through;
/// - per-frame changes in HUD mode + selected mode item, island progress step, Isleventory counts and pasture count.
///
/// A recording survives a plugin reload (dev builds reload mid-recording — it cut the first cabin recording short on
/// 2026-10-17): unloading leaves <c>probe/recording.active</c> naming the file and label, and the next load resumes
/// appending to that same file.
///
/// The hooks are created disabled and only enabled while recording. Every detour calls the original first-class and
/// swallows its own errors, so a logging bug can never change what the game does.
/// </summary>
public sealed unsafe class Probe : IDisposable
{
    private const int RecentLines = 200;

    private static readonly AgentId[] IslandAgents =
    {
        AgentId.MJIHud, AgentId.MJIPouch, AgentId.MJIRecipeNoteBook, AgentId.MJICraftSchedule, AgentId.MJICraftSales,
        AgentId.MJIAnimalManagement, AgentId.MJIFarmManagement, AgentId.MJIGatheringHouse, AgentId.MJIBuilding,
        AgentId.MJIGatheringNoteBook, AgentId.MJIDisposeShop, AgentId.MJIMinionManagement, AgentId.MJIMinionNoteBook,
        AgentId.MJIBuildingMove, AgentId.MJIEntrance, AgentId.MJISettings, AgentId.MJIHousingMenu, AgentId.MJINekomimiRequest,
    };

    private delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close);
    private delegate ulong InteractDelegate(TargetSystem* targetSystem, GameObject* obj, bool checkLineOfSight);
    private delegate bool UseActionDelegate(ActionManager* am, ActionType type, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted);
    private delegate AtkValue* AgentReceiveEventDelegate(AgentInterface* agent, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind);

    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly IAddonLifecycle _addons;
    private readonly string _dir;
    private readonly Hook<FireCallbackDelegate>? _fireCallback;
    private readonly Hook<InteractDelegate>? _interact;
    private readonly Hook<UseActionDelegate>? _useAction;
    private readonly List<Hook<AgentReceiveEventDelegate>> _agentHooks = new();
    private readonly Dictionary<nint, string> _agentNames = new();
    private readonly LinkedList<string> _recent = new();

    private StreamWriter? _writer;
    private uint _lastMode = uint.MaxValue;
    private uint _lastModeItem = uint.MaxValue;
    private int _lastProgress = -1;
    private int _lastAnimals = -1;
    private Dictionary<uint, int> _lastPouch = new();
    private Func<IEnumerable<uint>>? _pouchItems;

    public Probe(IGameInteropProvider interop, IAddonLifecycle addons, IPluginLog log, string configDirectory)
    {
        _log = log;
        _interop = interop;
        _addons = addons;
        _dir = Path.Combine(configDirectory, "probe");

        _fireCallback = TryHook<FireCallbackDelegate>(interop, (nint)AtkUnitBase.MemberFunctionPointers.FireCallback, FireCallbackDetour, "AtkUnitBase.FireCallback");
        _interact = TryHook<InteractDelegate>(interop, (nint)TargetSystem.MemberFunctionPointers.InteractWithObject, InteractDetour, "TargetSystem.InteractWithObject");
        _useAction = TryHook<UseActionDelegate>(interop, (nint)ActionManager.MemberFunctionPointers.UseAction, UseActionDetour, "ActionManager.UseAction");
    }

    private string ActiveMarker => Path.Combine(_dir, "recording.active");

    public bool Recording => _writer != null;
    public string CurrentFile { get; private set; } = string.Empty;
    public IEnumerable<string> Recent => _recent;

    /// <summary>Start a recording. <paramref name="label"/> is what the player is about to do ("capture a chocobo").</summary>
    public void Start(string label, Func<IEnumerable<uint>> pouchItems) =>
        Open(Path.Combine(_dir, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"), label, pouchItems, resumed: false);

    /// <summary>If the plugin was unloaded mid-recording, carry on in the same file. Call once the UI is up (agents exist).</summary>
    public void ResumeIfInterrupted(Func<IEnumerable<uint>> pouchItems)
    {
        if (Recording || !File.Exists(ActiveMarker))
            return;

        try
        {
            var lines = File.ReadAllLines(ActiveMarker);
            if (lines.Length >= 1 && File.Exists(lines[0]))
                Open(lines[0], lines.Length > 1 ? lines[1] : string.Empty, pouchItems, resumed: true);
            else
                File.Delete(ActiveMarker);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Probe: could not resume the interrupted recording");
        }
    }

    private void Open(string file, string label, Func<IEnumerable<uint>> pouchItems, bool resumed)
    {
        if (Recording)
            return;

        Directory.CreateDirectory(_dir);
        CurrentFile = file;
        _writer = new StreamWriter(CurrentFile, append: resumed, Encoding.UTF8) { AutoFlush = true };
        File.WriteAllLines(ActiveMarker, new[] { CurrentFile, label });
        _recent.Clear();
        _pouchItems = pouchItems;
        _lastMode = _lastModeItem = uint.MaxValue;
        _lastProgress = _lastAnimals = -1;
        _lastPouch = new Dictionary<uint, int>();

        HookAgents();
        _fireCallback?.Enable();
        _interact?.Enable();
        _useAction?.Enable();
        foreach (var h in _agentHooks)
            h.Enable();
        _addons.RegisterListener(AddonEvent.PostSetup, OnAddonOpen);
        _addons.RegisterListener(AddonEvent.PreFinalize, OnAddonClose);

        Write(resumed ? new { kind = "resumed after reload", label } : new { kind = "start", label });
        Tick(); // baseline state
    }

    /// <summary>End the recording (the player pressed Stop).</summary>
    public void Stop() => Close(interrupted: false);

    private void Close(bool interrupted)
    {
        if (!Recording)
            return;

        _fireCallback?.Disable();
        _interact?.Disable();
        _useAction?.Disable();
        foreach (var h in _agentHooks)
            h.Disable();
        _addons.UnregisterListener(AddonEvent.PostSetup, OnAddonOpen);
        _addons.UnregisterListener(AddonEvent.PreFinalize, OnAddonClose);

        Write(new { kind = interrupted ? "paused: plugin unloading" : "stop" });
        _writer!.Dispose();
        _writer = null;
        if (!interrupted)
        {
            try
            {
                File.Delete(ActiveMarker);
            }
            catch
            {
                // a stale marker only means the next load offers nothing to resume
            }
        }
    }

    /// <summary>Per-frame state diff while recording. Framework thread.</summary>
    public void Tick()
    {
        if (!Recording)
            return;

        try
        {
            var mji = MJIManager.Instance();
            if (mji != null)
            {
                if (mji->CurrentMode != _lastMode || mji->CurrentModeItem != _lastModeItem)
                {
                    _lastMode = mji->CurrentMode;
                    _lastModeItem = mji->CurrentModeItem;
                    Write(new { kind = "mode", mode = _lastMode, modeItem = _lastModeItem });
                }

                if (mji->IslandState.CurrentProgress != _lastProgress)
                {
                    _lastProgress = mji->IslandState.CurrentProgress;
                    Write(new { kind = "progress", step = _lastProgress, rank = mji->IslandState.CurrentRank });
                }

                if (mji->PastureHandler != null)
                {
                    var animals = 0;
                    foreach (ref var a in mji->PastureHandler->MJIAnimals)
                    {
                        if (a.EntityId != 0 && a.EntityId != 0xE0000000)
                            animals++;
                    }

                    if (animals != _lastAnimals)
                    {
                        _lastAnimals = animals;
                        Write(new { kind = "pasture", animals });
                    }
                }
            }

            var inv = InventoryManager.Instance();
            if (inv != null && _pouchItems != null)
            {
                foreach (var item in _pouchItems())
                {
                    var n = inv->GetInventoryItemCount(item);
                    if (_lastPouch.TryGetValue(item, out var before) && before == n)
                        continue;
                    if (_lastPouch.ContainsKey(item))
                        Write(new { kind = "pouch", item, from = before, to = n });
                    _lastPouch[item] = n;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Probe tick failed");
        }
    }

    private void HookAgents()
    {
        if (_agentHooks.Count > 0)
            return;

        var module = AgentModule.Instance();
        if (module == null)
            return;

        var seen = new HashSet<nint>();
        foreach (var id in IslandAgents)
        {
            var agent = module->GetAgentByInternalId(id);
            if (agent == null)
                continue;

            _agentNames[(nint)agent] = id.ToString();
            var vtable = *(nint**)agent;
            var receive = vtable[0];
            if (!seen.Add(receive))
                continue;

            // Agents only exist once the UI is up, so these hooks are made on the first recording, not in the ctor.
            if (TryHook<AgentReceiveEventDelegate>(_interop, receive, AgentReceiveEventDetour, $"Agent{id}.ReceiveEvent") is { } hook)
                _agentHooks.Add(hook);
        }
    }

    private void OnAddonOpen(AddonEvent type, AddonArgs args)
    {
        // The window's AtkValues hold what it was opened with — for ContextIconMenu, the entry list (icons, names).
        var values = new List<string>();
        try
        {
            var unit = (AtkUnitBase*)args.Addon.Address;
            if (unit != null && unit->AtkValues != null)
                values = Values(unit->AtkValues, unit->AtkValuesCount);
        }
        catch
        {
            // logging only
        }

        Write(new { kind = "window-open", addon = args.AddonName, values });
    }

    private void OnAddonClose(AddonEvent type, AddonArgs args) => Write(new { kind = "window-close", addon = args.AddonName });

    private bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close)
    {
        try
        {
            var name = addon == null ? "?" : addon->NameString;
            // Menus are shown and hidden, not set up each time, so window-open never fires for them: log what the menu
            // holds (its entry list) at the moment it's clicked.
            var menu = name is "ContextIconMenu" or "SelectString" or "SelectIconString" && addon->AtkValues != null
                ? Values(addon->AtkValues, addon->AtkValuesCount, 128)
                : null;
            Write(new { kind = "callback", addon = name, close, values = Values(values, valueCount), menu });
        }
        catch
        {
            // never let logging change the call
        }

        return _fireCallback!.Original(addon, valueCount, values, close);
    }

    private AtkValue* AgentReceiveEventDetour(AgentInterface* agent, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind)
    {
        try
        {
            if (_agentNames.TryGetValue((nint)agent, out var name))
                Write(new { kind = "agent", agent = name, eventKind, values = Values(values, valueCount) });
        }
        catch
        {
        }

        foreach (var h in _agentHooks)
        {
            if (h.Address == (*(nint**)agent)[0])
                return h.Original(agent, returnValue, values, valueCount, eventKind);
        }

        return null; // unreachable: the detour only runs from a hook we own
    }

    private ulong InteractDetour(TargetSystem* targetSystem, GameObject* obj, bool checkLineOfSight)
    {
        try
        {
            if (obj != null)
            {
                var p = obj->Position;
                Write(new { kind = "interact", name = obj->NameString, baseId = obj->BaseId, objectKind = obj->ObjectKind.ToString(), x = p.X, y = p.Y, z = p.Z });
            }
        }
        catch
        {
        }

        return _interact!.Original(targetSystem, obj, checkLineOfSight);
    }

    private bool UseActionDetour(ActionManager* am, ActionType type, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        var result = _useAction!.Original(am, type, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        try
        {
            Write(new { kind = "action", type = type.ToString(), actionId, targetId = targetId.ToString("X"), extraParam, mode = mode.ToString(), result });
        }
        catch
        {
        }

        return result;
    }

    private static List<string> Values(AtkValue* values, uint count, int max = 32)
    {
        var list = new List<string>((int)Math.Min(count, (uint)max));
        for (var i = 0; i < count && i < max; i++)
        {
            var v = values[i];
            list.Add(v.Type switch
            {
                AtkValueType.Int => $"i:{v.Int}",
                AtkValueType.UInt => $"u:{v.UInt}",
                AtkValueType.Bool => $"b:{v.Byte != 0}",
                AtkValueType.Float => $"f:{v.Float}",
                AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString => $"s:{(v.String.Value == null ? string.Empty : v.String.ToString())}",
                _ => $"{v.Type}",
            });
        }

        return list;
    }

    private void Write(object entry)
    {
        if (_writer == null)
            return;

        var line = JsonSerializer.Serialize(new { t = DateTime.Now.ToString("HH:mm:ss.fff"), e = entry });
        _writer.WriteLine(line);
        _recent.AddFirst(line);
        if (_recent.Count > RecentLines)
            _recent.RemoveLast();
    }

    private Hook<T>? TryHook<T>(IGameInteropProvider interop, nint address, T detour, string what) where T : Delegate
    {
        try
        {
            if (address == 0)
            {
                _log.Warning("Probe: no address for {0}", what);
                return null;
            }

            return interop.HookFromAddress(address, detour);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Probe: could not hook {0}", what);
            return null;
        }
    }

    public void Dispose()
    {
        Close(interrupted: true); // leaves the marker so the next load resumes
        _fireCallback?.Dispose();
        _interact?.Dispose();
        _useAction?.Dispose();
        foreach (var h in _agentHooks)
            h.Dispose();
    }
}
