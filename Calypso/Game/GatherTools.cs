using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Gathering;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>
/// Which gathering tool each island item needs, and which tools the character holds — all from the sheets:
/// MJIGatheringItem.Unknown0 → MJIGatheringTool row → .Unknown0 → MJIKeyItem row (claude-memory.md "Sheets").
/// E.g. Island Log needs the stone hatchet, Copper Ore the stone hammer, Sugarcane the copper scythe.
/// </summary>
public sealed unsafe class GatherTools
{
    private readonly Dictionary<uint, int> _toolForItem;
    private readonly Dictionary<int, (int KeyItemRow, string Name)> _tools;
    private readonly HashSet<uint> _caveItems;
    private readonly Dictionary<uint, int> _stackSize;
    private readonly int _caveProgress;
    private readonly Dictionary<uint, IReadOnlyCollection<uint>> _sameArea;

    /// <summary>Other island items whose map area (MJIGatheringItem X/Y/Radius) is the same as this one's.</summary>
    public IReadOnlyCollection<uint> SameArea(uint itemId) => _sameArea.GetValueOrDefault(itemId) ?? Array.Empty<uint>();

    /// <summary>Isleventory cap for an island item (Item.StackSize).</summary>
    public int StackSize(uint itemId) => _stackSize.GetValueOrDefault(itemId, 999);

    public GatherTools(IDataManager data)
    {
        var gatherItems = data.GetExcelSheet<MJIGatheringItem>().Where(g => g.Item.RowId != 0).ToList();

        // MJIGatheringItem.Unknown1 = 1 marks cavern items; MJIGatheringObject.Unknown1 = the tutorial progress a node
        // type needs (35 and 42 are the cave nodes). A cavern item is in scope once the first cave opens.
        _caveItems = gatherItems.Where(g => g.Unknown1 != 0).Select(g => g.Item.RowId).ToHashSet();
        _caveProgress = data.GetExcelSheet<MJIGatheringObject>().Where(o => o.Unknown1 > 0).Select(o => (int)o.Unknown1).DefaultIfEmpty(int.MaxValue).Min();

        var items = data.GetExcelSheet<Item>();
        _stackSize = gatherItems.Select(g => g.Item.RowId).Distinct()
            .ToDictionary(id => id, id => (int)Math.Max(1u, items.GetRowOrDefault(id)?.StackSize ?? 999u));

        // MJIGatheringItem X/Y/Radius = the item's area on the island map. Items sharing an area come from a common node
        // type in the yields learned so far (Palm Leaf + Palm Log at 20,0 → Palm Tree; Branch + Log at 215,82 → Tualong
        // Tree; Apple + Vine at 194,72 → Island Apple Tree), so Island Limestone (-22,-46, with Island Stone) points at
        // Smooth White Rock. Only a hint — an item can also drop elsewhere (Mahogany Tree gives Island Log too), and
        // gathering confirms or corrects it.
        _sameArea = gatherItems.GroupBy(g => (g.X, g.Y, g.Radius))
            .SelectMany(a => a.Select(g => (g.Item.RowId, Others: a.Select(o => o.Item.RowId).Where(o => o != g.Item.RowId).ToList())))
            .GroupBy(x => x.RowId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<uint>)g.SelectMany(x => x.Others).Distinct().ToList());

        _toolForItem = data.GetExcelSheet<MJIGatheringItem>()
            .Where(g => g.Item.RowId != 0)
            .GroupBy(g => g.Item.RowId)
            .ToDictionary(g => g.Key, g => (int)g.First().Unknown0);

        var keyItems = data.GetExcelSheet<MJIKeyItem>();
        _tools = data.GetExcelSheet<MJIGatheringTool>()
            .Where(t => t.RowId > 0)
            .ToDictionary(t => (int)t.RowId, t =>
            {
                var key = keyItems.GetRowOrDefault(t.Unknown0);
                var name = key is { } k && k.Item.IsValid ? k.Item.Value.Name.ExtractText() : $"tool {t.RowId}";
                return ((int)t.Unknown0, name);
            });
    }

    /// <summary>
    /// Tool rows the character holds. Primary source: the Isleventory's own Tools tab (AgentMJIPouch.InventoryData->Tools,
    /// RowId = MJIKeyItem row, StackSize > 0 = held) — only filled once the Isleventory has been opened this session.
    /// Fallback: the key-item bits with the tutorial's reading (row N at bit N-1 — UNVERIFIED, see BitKeyItemRows).
    /// </summary>
    public IReadOnlyList<int> Owned()
    {
        var keyRows = PouchKeyItemRows() ?? BitKeyItemRows(rowMinusOne: true);
        return _tools.Where(t => keyRows.Contains(t.Value.KeyItemRow)).Select(t => t.Key).ToList();
    }

    /// <summary>
    /// MJIKeyItem rows held per the Isleventory Tools tab, or null when the Isleventory hasn't been open this session.
    /// Read only while the agent is active (its data may be freed once the window closes) and remembered after, since
    /// tools change only when one is crafted — opening the Isleventory again refreshes it.
    /// </summary>
    public HashSet<int>? PouchKeyItemRows()
    {
        var agent = AgentMJIPouch.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive() || agent->InventoryData == null || agent->InventoryData->Tools.Count == 0)
            return _pouchRows;

        var rows = new HashSet<int>();
        foreach (var ptr in agent->InventoryData->Tools)
        {
            var item = ptr.Value;
            if (item != null && item->StackSize > 0 && !item->IsUndiscovered)
                rows.Add((int)item->RowId);
        }

        return _pouchRows = rows;
    }

    private HashSet<int>? _pouchRows;

    /// <summary>MJIKeyItem rows whose IslandState bit is set, under one of the two candidate readings (for the Debug comparison).</summary>
    public HashSet<int> BitKeyItemRows(bool rowMinusOne)
    {
        var mji = MJIManager.Instance();
        var rows = new HashSet<int>();
        if (mji == null)
            return rows;
        foreach (var t in _tools.Values.Where(t => t.KeyItemRow > 0))
        {
            if (mji->IsKeyItemUnlocked((ushort)(rowMinusOne ? t.KeyItemRow - 1 : t.KeyItemRow)))
                rows.Add(t.KeyItemRow);
        }

        return rows;
    }

    /// <summary>Gathering tools by name with what each source says — Progression tab, until the bit reading is settled.</summary>
    public IEnumerable<(string Name, bool? Pouch, bool BitRow, bool BitRowMinusOne)> Readings()
    {
        var pouch = PouchKeyItemRows();
        var byRow = BitKeyItemRows(rowMinusOne: false);
        var byRowMinusOne = BitKeyItemRows(rowMinusOne: true);
        return _tools.Values.Where(t => t.KeyItemRow > 0).Select(t => (t.Name,
            pouch == null ? (bool?)null : pouch.Contains(t.KeyItemRow),
            byRow.Contains(t.KeyItemRow), byRowMinusOne.Contains(t.KeyItemRow)));
    }

    public string Signature() => YieldBook.Signature(Owned());

    /// <summary>The tool an item needs that the character doesn't hold, or null when it can be gathered now.</summary>
    public string? MissingTool(uint itemId)
    {
        if (!_toolForItem.TryGetValue(itemId, out var tool) || tool == 0)
            return null;
        return Owned().Contains(tool) ? null : _tools.TryGetValue(tool, out var t) ? t.Name : $"tool {tool}";
    }

    /// <summary>
    /// Every island item that can be gathered right now: on a node (MJIGatheringItem), its tool held, and — for cavern
    /// items — the caves opened by the tutorial. With each item's stack cap from the Item sheet.
    /// </summary>
    public IEnumerable<(uint ItemId, int StackSize)> GatherableNow(int progress)
    {
        var owned = Owned();
        foreach (var (item, tool) in _toolForItem)
        {
            if (tool != 0 && !owned.Contains(tool))
                continue;
            if (_caveItems.Contains(item) && progress < _caveProgress)
                continue;
            yield return (item, _stackSize.GetValueOrDefault(item, 999));
        }
    }

    public string Describe(string signature) => signature.Length == 0
        ? "no tools"
        : string.Join(", ", signature.Split(',').Select(s => int.TryParse(s, out var r) && _tools.TryGetValue(r, out var t) ? t.Name : s));
}
