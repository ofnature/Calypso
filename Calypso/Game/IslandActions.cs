using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Calypso.Game;

/// <summary>
/// The small game actions every island system needs: find and interact with objects, keep dialogue advancing, read
/// whether a dialogue menu is waiting, and switch the island HUD mode. Thin unsafe adapters, framework thread only.
/// </summary>
public sealed unsafe class IslandActions
{
    /// <summary>Charon's text-advance lease (Charon/Ipc/TextAdvanceIpc.cs): force on for N seconds, 0 releases.</summary>
    private const string TextAdvanceForceGate = "Charon.TextAdvance.Force";

    private readonly IDalamudPluginInterface _pi;
    private readonly IObjectTable _objects;
    private readonly IGameGui _gameGui;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;
    private DateTime _leaseUntilUtc = DateTime.MinValue;

    public IslandActions(IDalamudPluginInterface pi, IObjectTable objects, IGameGui gameGui, ICondition condition, IPluginLog log)
    {
        _pi = pi;
        _objects = objects;
        _gameGui = gameGui;
        _condition = condition;
        _log = log;
    }

    public Vector3? PlayerPosition => _objects.LocalPlayer?.Position;

    /// <summary>The nearest targetable object with this base id (ENpc / EObj / gathering point).</summary>
    public IGameObject? Nearest(uint baseId) =>
        _objects.Where(o => o.BaseId == baseId && o.IsTargetable)
            .OrderBy(o => Vector3.DistanceSquared(o.Position, PlayerPosition ?? o.Position))
            .FirstOrDefault();

    // [REPLACED: island nodes are ReactionEventObjects with the node EObj id, not GatheringPoints — probe 2026-10-17]
    // public IEnumerable<IGameObject> NearbyGatheringPoints() =>
    //     _objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable);

    /// <summary>
    /// Targetable island gathering nodes currently loaded: objects whose base id is a node EObj
    /// (<see cref="IslandLayout.NodeBaseIds"/>). GatheringPoint kind is still accepted in case a patch changes it back.
    /// </summary>
    public IEnumerable<IGameObject> NearbyGatheringPoints(IReadOnlySet<uint> nodeBaseIds) =>
        _objects.Where(o => o.IsTargetable && (nodeBaseIds.Contains(o.BaseId) || o.ObjectKind == ObjectKind.GatheringPoint));

    /// <summary>How many of an item the character holds (Isleventory included — read the way visland does).</summary>
    public int ItemCount(uint itemId)
    {
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        return inv == null || itemId == 0 ? 0 : inv->GetInventoryItemCount(itemId);
    }

    /// <summary>Targetable loaded objects matching <paramref name="match"/>.</summary>
    public IEnumerable<IGameObject> Nearby(Func<IGameObject, bool> match) => _objects.Where(o => o.IsTargetable && match(o));

    public float DistanceTo(Vector3 p) => PlayerPosition is { } me ? Vector3.Distance(me, p) : float.MaxValue;

    /// <summary>Interact the way the player would (TargetSystem.InteractWithObject, no line-of-sight check).</summary>
    public bool Interact(IGameObject target)
    {
        try
        {
            var ts = TargetSystem.Instance();
            if (ts == null || target.Address == nint.Zero)
                return false;
            ts->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address, false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Interact failed on {0}", target.Name.TextValue);
            return false;
        }
    }

    /// <summary>True while talking / in an event (cutscene-free dialogue counts).</summary>
    public bool InEvent => _condition[ConditionFlag.OccupiedInEvent] || _condition[ConditionFlag.OccupiedInQuestEvent]
                           || _condition[ConditionFlag.OccupiedInCutSceneEvent];

    public bool Busy => InEvent || _condition[ConditionFlag.Casting] || _condition[ConditionFlag.Occupied]
                        || _condition[ConditionFlag.Occupied30] || _condition[ConditionFlag.Occupied33] || _condition[ConditionFlag.Occupied38];

    /// <summary>A dialogue menu that needs a choice Calypso does not know how to make yet.</summary>
    public string? WaitingMenu()
    {
        foreach (var name in new[] { "SelectString", "SelectIconString", "SelectYesno" })
        {
            var a = _gameGui.GetAddonByName(name);
            if (!a.IsNull && a.IsVisible)
                return name;
        }

        return null;
    }

    /// <summary>Keep Charon's auto-advance on for the next minute; call every few seconds while talking. False = Charon absent.</summary>
    public bool KeepDialogueAdvancing()
    {
        if (DateTime.UtcNow < _leaseUntilUtc)
            return true;
        try
        {
            var ok = _pi.GetIpcSubscriber<int, bool>(TextAdvanceForceGate).InvokeFunc(60);
            _leaseUntilUtc = DateTime.UtcNow.AddSeconds(20);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public void ReleaseDialogue()
    {
        if (_leaseUntilUtc == DateTime.MinValue)
            return;
        _leaseUntilUtc = DateTime.MinValue;
        try
        {
            _pi.GetIpcSubscriber<int, bool>(TextAdvanceForceGate).InvokeFunc(0);
        }
        catch
        {
            // Charon gone — its lease expires by itself.
        }
    }

    public uint HudMode
    {
        get
        {
            var mji = MJIManager.Instance();
            return mji == null ? uint.MaxValue : mji->CurrentMode;
        }
    }

    // [REPLACED: visland's all-Int shape; the probe recorded the real value types (2026-10-17)] (kept for reference)
    // public void RequestHudMode(uint row, uint iconId)
    // {
    //     var menu = _gameGui.GetAddonByName("ContextIconMenu");
    //     if (!menu.IsNull && menu.IsVisible) { Fire((AtkUnitBase*)menu.Address, true, 0, (int)row, (int)iconId, 0, 0); return; }
    //     var hud = _gameGui.GetAddonByName("MJIHud");
    //     if (!hud.IsNull && hud.IsVisible) Fire((AtkUnitBase*)hud.Address, false, 11, 0);
    // }

    /// <summary>Marks an AtkValue of type Undefined in <see cref="FireCallback"/> — the island menus end with one.</summary>
    public static readonly object Undefined = new();

    /// <summary>
    /// Ask for an island HUD mode (MJIHudMode row), exactly as the probe recorded it (Gather→Sow, Free→Water,
    /// 2026-10-17): MJIHud callback [i:11, Undefined] opens the mode menu, then ContextIconMenu
    /// [i:0, i:row, u:iconId, u:0, Undefined] picks it (agent MJIHud event 2). The second value equalled the MJIHudMode
    /// row both times (with every mode up to it unlocked — a list position would have matched too).
    /// Call repeatedly until <see cref="HudMode"/> reports the mode.
    /// </summary>
    // [REPLACED: the menu index is neither the sheet row nor the mode value — it is the entry's position in the menu
    // (unlocked modes in sheet-row order: Feed was index 5 → mode 6, probe 2026-10-17). Entries are now found by icon.]
    // public void RequestHudMode(uint modeValue, uint iconId)
    // {
    //     if (IsAddonVisible("ContextIconMenu")) { FireCallback("ContextIconMenu", true, 0, (int)modeValue, iconId, 0u, Undefined); return; }
    //     if (IsAddonVisible("MJIHud")) FireCallback("MJIHud", true, 11, Undefined);
    // }

    /// <summary>
    /// Ask for the island HUD mode whose icon is <paramref name="iconId"/>: MJIHud [i:11, Undefined] opens the mode menu,
    /// then the entry showing that icon is picked. False when the open menu has no such entry (mode still locked).
    /// </summary>
    public bool RequestHudMode(uint iconId)
    {
        if (IsAddonVisible("ContextIconMenu"))
        {
            if (MenuEntry((icon, _) => icon == iconId) is not { } i)
            {
                CloseModeMenu();
                return false;
            }

            FireCallback("ContextIconMenu", true, 0, i.Index, i.Icon, 0u, Undefined);
            return true;
        }

        if (IsAddonVisible("MJIHud"))
            FireCallback("MJIHud", true, 11, Undefined);
        return true;
    }

    /// <summary>
    /// Choose the current mode's item: MJIHud [i:12] opens the item menu, then the first entry holding one of
    /// <paramref name="itemIds"/> is picked. False when the open menu offers none of them.
    /// </summary>
    public bool RequestModeItem(IReadOnlyCollection<uint> itemIds)
    {
        if (IsAddonVisible("ContextIconMenu"))
        {
            if (MenuEntry((_, id) => itemIds.Contains(id)) is not { } i)
            {
                CloseModeMenu();
                return false;
            }

            FireCallback("ContextIconMenu", true, 0, i.Index, i.Icon, 0u, Undefined);
            return true;
        }

        if (IsAddonVisible("MJIHud"))
            FireCallback("MJIHud", true, 12);
        return true;
    }

    /// <summary>
    /// The open ContextIconMenu's first entry matching (icon, item id). Layout decoded from the probe's menu dumps
    /// (2026-10-17): AtkValues[4] = entry count; entry i at 11 + 8i: +0 icon, +1 item id (0 for modes), +2 name,
    /// +5 count held.
    /// </summary>
    public (int Index, uint Icon)? MenuEntry(Func<uint, uint, bool> match)
    {
        var a = _gameGui.GetAddonByName("ContextIconMenu");
        if (a.IsNull || !a.IsVisible)
            return null;

        var unit = (AtkUnitBase*)a.Address;
        if (unit->AtkValues == null || unit->AtkValuesCount <= 4)
            return null;

        var count = (int)unit->AtkValues[4].UInt;
        for (var i = 0; i < count; i++)
        {
            var at = 11 + 8 * i;
            if (at + 1 >= unit->AtkValuesCount)
                break;
            var icon = unit->AtkValues[at].UInt;
            var id = unit->AtkValues[at + 1].UInt;
            if (match(icon, id))
                return (i, icon);
        }

        return null;
    }

    /// <summary>One of this character's pasture animals (MJIPastureHandler.MJIAnimals EntityIds), not a wild one.</summary>
    /// <summary>The game's "no object" entity id (GameObject.InvalidGameObjectId).</summary>
    private const uint NoObjectId = 0xE0000000;

    public bool IsPastureAnimal(IGameObject o)
    {
        var mji = MJIManager.Instance();
        if (mji == null || mji->PastureHandler == null)
            return false;
        // [REPLACED: matched Saar's minion (2026-10-19, "interacting with Wind-up Weapon"): minions carry the game's
        // "no object" id 0xE0000000, which an empty pasture slot holds too]
        // foreach (ref var animal in mji->PastureHandler->MJIAnimals)
        // {
        //     if (animal.EntityId != 0 && animal.EntityId == o.EntityId)
        //         return true;
        // }
        if (o.ObjectKind != ObjectKind.BattleNpc || o.EntityId is 0 or NoObjectId)
            return false;
        foreach (ref var animal in mji->PastureHandler->MJIAnimals)
        {
            if (animal.EntityId == o.EntityId)
                return true;
        }

        return false;
    }

    // [REPLACED: picking by index without knowing the menu's order; RequestModeItem(itemIds) reads the menu instead]
    // public void RequestModeItem(int index, uint iconId)
    // {
    //     if (IsAddonVisible("ContextIconMenu")) { FireCallback("ContextIconMenu", true, 0, index, iconId, 0u, Undefined); return; }
    //     if (IsAddonVisible("MJIHud")) FireCallback("MJIHud", true, 12);
    // }

    /// <summary>MJIManager.CurrentModeItem — the item the current mode uses (seed item id while sowing).</summary>
    public uint HudModeItem
    {
        get
        {
            var mji = MJIManager.Instance();
            return mji == null ? 0 : mji->CurrentModeItem;
        }
    }

    public void CloseModeMenu()
    {
        var menu = _gameGui.GetAddonByName("ContextIconMenu");
        if (!menu.IsNull && menu.IsVisible)
            Fire((AtkUnitBase*)menu.Address, true, -1);
    }

    /// <summary>Close an open window the way its X button does.</summary>
    public void CloseAddon(string name)
    {
        var a = _gameGui.GetAddonByName(name);
        if (!a.IsNull && a.IsVisible)
            ((AtkUnitBase*)a.Address)->Close(true);
    }

    public bool IsAddonVisible(string name)
    {
        var a = _gameGui.GetAddonByName(name);
        return !a.IsNull && a.IsVisible;
    }

    /// <summary>
    /// The open window's AtkValues, strings only (null for every other type) — for checking what a menu or window shows
    /// before clicking it. Empty when it isn't open. Same reading as the probe's.
    /// </summary>
    public IReadOnlyList<string?> AddonStrings(string name, int max = 64)
    {
        var a = _gameGui.GetAddonByName(name);
        if (a.IsNull || !a.IsVisible)
            return Array.Empty<string?>();
        var unit = (AtkUnitBase*)a.Address;
        if (unit->AtkValues == null)
            return Array.Empty<string?>();

        var list = new List<string?>();
        for (var i = 0; i < unit->AtkValuesCount && i < max; i++)
        {
            var v = unit->AtkValues[i];
            list.Add(v.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString
                ? v.String.Value == null ? string.Empty : v.String.ToString()
                : null);
        }

        return list;
    }

    /// <summary>Visible AND set up (AtkUnitBase.IsReady, ClientStructs 78975d8: OnSetup has run).</summary>
    public bool IsAddonReady(string name)
    {
        var a = _gameGui.GetAddonByName(name);
        return !a.IsNull && a.IsVisible && ((AtkUnitBase*)a.Address)->IsReady;
    }

    /// <summary>
    /// Fire an addon callback exactly as the probe recorded it: <c>int</c> args become AtkValue Int, <c>uint</c> args
    /// UInt (the craft log's second value is a UInt). False when the addon isn't open.
    /// </summary>
    public bool FireCallback(string addonName, bool close, params object[] args)
    {
        var a = _gameGui.GetAddonByName(addonName);
        // [REPLACED: fired into windows still being set up — Saar crashed 2026-10-19 21:25 (AddonMJIRecipeNoteBook.OnRefresh
        // → AtkComponentList.SetItemCount on a null list) when the craft log got a tab click the frame it appeared]
        // if (a.IsNull)
        //     return false;
        if (a.IsNull || !((AtkUnitBase*)a.Address)->IsReady)
            return false;

        var values = stackalloc AtkValue[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case var o when ReferenceEquals(o, Undefined):
                    break; // stackalloc is zeroed: an AtkValue of type Undefined
                case uint u:
                    values[i].SetUInt(u);
                    break;
                case int n:
                    values[i].SetInt(n);
                    break;
                default:
                    throw new ArgumentException($"unsupported callback value {args[i]?.GetType().Name}");
            }
        }

        ((AtkUnitBase*)a.Address)->FireCallback((uint)args.Length, values, close);
        return true;
    }

    private static void Fire(AtkUnitBase* addon, bool close, params int[] args)
    {
        var values = stackalloc AtkValue[args.Length];
        for (var i = 0; i < args.Length; i++)
            values[i].SetInt(args[i]);
        addon->FireCallback((uint)args.Length, values, close);
    }
}
