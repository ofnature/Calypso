using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Calypso.Core;
using Calypso.Core.Progression;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>
/// The island's fixed geography and tutorial, read once from game files (claude-memory.md "Gathering node positions",
/// "Island progression"):
/// - NPCs from planlive.lgb + planevent.lgb (ENpc instances; names from ENpcResident);
/// - the 505 gathering spots (placeholder shared groups gth_f0_m0000.sgb in planlive.lgb — which node type sits
///   there is decided by the game at runtime);
/// - MJIProgress steps with their listed materials;
/// - names the tutorial text can mention: tools (MJIKeyItem), recipe outputs (MJIRecipe), buildings (MJIBuilding).
/// </summary>
public sealed unsafe class IslandLayout
{
    private const string Level = "bg/ffxiv/hou_xx/hou/h1m2/level/";
    private const string GatherPlaceholder = "bgcommon/mji/gath/common/asset/gth_f0_m0000.sgb";
    private const byte NoPouchRow = 255;

    private readonly List<(int Row, string Name)> _keyItems = new();
    private readonly List<(int RecipeRow, int KeyItemRow, string Name, (int PouchRow, int Amount)[] Materials, bool Automatable)> _toolRecipes = new();
    private readonly Dictionary<uint, Vector3> _eobjPositions = new();
    private readonly List<RecipeInfo> _recipes = new();

    /// <summary>
    /// An MJIRecipe as the craft log shows it. <see cref="Tab"/>: 0 Tools (key-item outputs), 1 Feed, 2 Restraints —
    /// probe 2026-10-17 (net: tab u:2). <see cref="Order"/> is MJIRecipe.Order; the log listed Stone Hammer (21) above
    /// Stone Hatchet (20), so entries are taken in DESCENDING Order among unlocked recipes (one observation).
    /// </summary>
    public sealed record RecipeInfo(int Row, string Name, int Tab, int Order, int KeyItemRow, uint OutputItemId, int Yield,
        (int PouchRow, int Amount)[] Materials, bool Automatable);

    public IReadOnlyList<RecipeInfo> Recipes => _recipes;
    private readonly List<(int Building, string Name)> _buildings = new();

    public IslandLayout(IDataManager data, IPluginLog log)
    {
        var residents = data.GetExcelSheet<ENpcResident>();
        var npcs = new List<IslandNpc>();
        var spots = new List<Vector3>();

        foreach (var file in new[] { "planlive", "planevent" })
        {
            LgbFile? lgb = null;
            try
            {
                lgb = data.GetFile<LgbFile>(Level + file + ".lgb");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Could not read {0}.lgb", file);
            }

            if (lgb == null)
                continue;

            foreach (var layer in lgb.Layers)
            {
                foreach (var o in layer.InstanceObjects)
                {
                    var t = o.Transform.Translation;
                    var pos = new Vector3(t.X, t.Y, t.Z);
                    switch (o.Object)
                    {
                        case LayerCommon.ENPCInstanceObject npc:
                        {
                            var id = npc.ParentData.ParentData.BaseId;
                            var name = residents.GetRowOrDefault(id)?.Singular.ExtractText() ?? string.Empty;
                            if (name.Length > 0)
                                npcs.Add(new IslandNpc(id, name, pos));
                            break;
                        }
                        case LayerCommon.SharedGroupInstanceObject sg when sg.AssetPath == GatherPlaceholder:
                            spots.Add(pos);
                            break;
                        case LayerCommon.EventInstanceObject ev:
                            _eobjPositions.TryAdd(ev.ParentData.BaseId, pos);
                            break;
                    }
                }
            }
        }

        Npcs = npcs;
        GatherSpots = spots;

        Steps = data.GetExcelSheet<MJIProgress>().ToDictionary(r => (int)r.RowId, r => new ProgressStep(
            (int)r.RowId,
            r.Objective.ExtractText(),
            new[] { (r.Unknown3, r.Unknown8), (r.Unknown4, r.Unknown9), (r.Unknown5, r.Unknown10), (r.Unknown6, r.Unknown11), (r.Unknown7, r.Unknown12) }
                .Where(m => m.Item1 != NoPouchRow && m.Item2 > 0)
                .Select(m => ((int)m.Item1, (int)m.Item2))
                .ToArray()));

        // Island gathering nodes are EObjs (ObjectKind.ReactionEventObject — probe 2026-10-17, not GatheringPoint as
        // visland's code assumes) whose model is an MJIGatheringObject model. Today that is EObj 2012985, shared by
        // every node type; the type is told apart by the live object's name.
        var nodeModels = data.GetExcelSheet<MJIGatheringObject>().Where(g => g.RowId > 0).Select(g => g.SGB.RowId).ToHashSet();
        NodeBaseIds = data.GetExcelSheet<EObj>().Where(e => nodeModels.Contains(e.SgbPath.RowId)).Select(e => e.RowId).ToHashSet();

        // Cropland plots: EObj 2013159 whatever is planted (probe 2026-10-17 — "Empty Plot", then "Island Pumpkin", same
        // base id). The empty name is EObjName 2013159, so "is it empty" works in every client language.
        if (data.GetExcelSheet<EObj>().GetRowOrDefault(PlotBaseId) == null)
            log.Warning("Cropland plot EObj {0} not in the EObj sheet — sowing/watering will find nothing", PlotBaseId);
        EmptyPlotName = data.GetExcelSheet<EObjName>().GetRowOrDefault(PlotBaseId)?.Singular.ExtractText() ?? string.Empty;

        // Seeds: MJIItemPouch rows with a Crop (MJICropSeed) — the Gardening Starter category.
        var items = data.GetExcelSheet<Item>();
        Seeds = data.GetExcelSheet<MJIItemPouch>()
            .Where(p => p.Crop.RowId != 0 && p.Item.RowId != 0)
            .Select(p => ((int)p.RowId, p.Item.RowId, (uint)(items.GetRowOrDefault(p.Item.RowId)?.Icon ?? 0)))
            .ToList();

        // Island animals: MJIAnimals.BNpcBase (the live BattleNpc's base id — probe: Opo-opo 14908), Size 1/2/3,
        // Unknown0 1 = common / 2 = variant / 3 = rare.
        Animals = data.GetExcelSheet<MJIAnimals>().Where(a => a.BNpcBase.RowId != 0)
            .Select(a => ((uint)a.BNpcBase.RowId, (int)a.Size, (int)a.Unknown0)).ToList();

        // Restraints: MJIItemPouch category 7. Size by order within the category — makeshift net/restraint/soporific,
        // then flawless net/restraint/soporific = small/medium/large (INFERRED from names + the user's guide; no sheet
        // links a restraint to an animal size).
        var restraintRows = data.GetExcelSheet<MJIItemPouch>().Where(p => p.Category.RowId == RestraintCategory && p.Item.RowId != 0)
            .OrderBy(p => p.RowId).ToList();
        Feeds = data.GetExcelSheet<MJIItemPouch>().Where(p => p.Category.RowId == FeedCategory && p.Item.RowId != 0)
            .Select(p => ((int)p.RowId, p.Item.RowId)).ToList();
        Restraints = restraintRows.Select((p, i) => ((int)p.RowId, p.Item.RowId, (uint)(items.GetRowOrDefault(p.Item.RowId)?.Icon ?? 0), i % 3 + 1)).ToList();

        foreach (var k in data.GetExcelSheet<MJIKeyItem>().Where(k => k.RowId > 0 && k.Item.RowId != 0))
            _keyItems.Add(((int)k.RowId, k.Item.Value.Name.ExtractText()));

        // MJIRecipe: KeyItem (tool) or ItemPouch output; Material → MJIRecipeMaterial (ItemPouch + Unknown0 category
        // wildcard, 0 = exact item); Amount 0 = empty slot. Row 0 = stone hatchet (probe-verified materials).
        var recipeMaterials = data.GetExcelSheet<MJIRecipeMaterial>();
        foreach (var r in data.GetExcelSheet<MJIRecipe>().Where(r => r.KeyItem.RowId != 0 && r.KeyItem.IsValid && r.KeyItem.Value.Item.IsValid))
        {
            var mats = r.Material.Zip(r.Amount).Where(m => m.Second > 0)
                .Select(m => recipeMaterials.GetRowOrDefault(m.First.RowId))
                .Zip(r.Amount.Where(a => a > 0))
                .ToList();
            var automatable = mats.All(m => m.First is { } rm && rm.Unknown0 == 0);
            _toolRecipes.Add(((int)r.RowId, (int)r.KeyItem.RowId, r.KeyItem.Value.Item.Value.Name.ExtractText(),
                mats.Select(m => ((int)(m.First?.ItemPouch.RowId ?? 0), (int)m.Second)).ToArray(), automatable));
        }

        foreach (var r in data.GetExcelSheet<MJIRecipe>())
        {
            var mats = r.Material.Zip(r.Amount).Where(m => m.Second > 0)
                .Select(m => (Row: recipeMaterials.GetRowOrDefault(m.First.RowId), Amount: (int)m.Second)).ToList();
            var automatable = mats.All(m => m.Row is { } rm && rm.Unknown0 == 0);
            var materials = mats.Select(m => ((int)(m.Row?.ItemPouch.RowId ?? 0), m.Amount)).ToArray();

            if (r.KeyItem.RowId != 0 && r.KeyItem.IsValid && r.KeyItem.Value.Item.IsValid)
            {
                _recipes.Add(new RecipeInfo((int)r.RowId, r.KeyItem.Value.Item.Value.Name.ExtractText(), 0, r.Order,
                    (int)r.KeyItem.RowId, r.KeyItem.Value.Item.RowId, r.Yield, materials, automatable));
            }
            else if (r.ItemPouch.RowId != 0 && r.ItemPouch.IsValid && r.ItemPouch.Value.Item.IsValid)
            {
                var tab = r.ItemPouch.Value.Category.RowId switch { FeedCategory => 1, RestraintCategory => 2, _ => -1 };
                if (tab >= 0)
                {
                    _recipes.Add(new RecipeInfo((int)r.RowId, r.ItemPouch.Value.Item.Value.Name.ExtractText(), tab, r.Order,
                        0, r.ItemPouch.Value.Item.RowId, r.Yield, materials, automatable));
                }
            }
        }

        RecipeOutputs = data.GetExcelSheet<MJIRecipe>()
            .Where(r => r.ItemPouch.RowId != 0 && r.ItemPouch.IsValid && r.ItemPouch.Value.Item.IsValid)
            .Select(r => r.ItemPouch.Value.Item.Value.Name.ExtractText())
            .Distinct()
            .ToList();

        foreach (var b in data.GetSubrowExcelSheet<MJIBuilding>().SelectMany(s => s).Where(b => b.SubrowId == 0 && b.Name.IsValid))
            _buildings.Add(((int)b.RowId, b.Name.Value.Text.ExtractText()));

        log.Information("Island layout: {0} NPCs, {1} gathering spots, {2} tutorial steps, node EObj ids {3}",
            Npcs.Count, GatherSpots.Count, Steps.Count, string.Join(",", NodeBaseIds));
    }

    public IReadOnlyList<IslandNpc> Npcs { get; }
    public IReadOnlyList<Vector3> GatherSpots { get; }

    /// <summary>MJIItemCategory row "Restraint".</summary>
    private const uint RestraintCategory = 7;

    /// <summary>MJIItemCategory row "Feed" (Island Sweetfeed 37612, Greenfeed 37613, Premium Greenfeed 37614).</summary>
    private const uint FeedCategory = 6;

    public IReadOnlyList<(int PouchRow, uint ItemId)> Feeds { get; } = Array.Empty<(int, uint)>();

    /// <summary>Island animals: live BattleNpc base id, size (1 small, 2 medium, 3 large), rarity (1 common, 2 variant, 3 rare).</summary>
    public IReadOnlyList<(uint BNpcBase, int Size, int Rarity)> Animals { get; } = Array.Empty<(uint, int, int)>();

    /// <summary>Restraints: pouch row, item id, icon, the animal size they catch (inferred).</summary>
    public IReadOnlyList<(int PouchRow, uint ItemId, uint IconId, int Size)> Restraints { get; } = Array.Empty<(int, uint, uint, int)>();

    /// <summary>Cropland plot EObj (probe-verified; validated against the EObj sheet at load).</summary>
    public const uint PlotBaseId = 2013159;

    /// <summary>The plot's name while nothing grows in it (EObjName 2013159, client language).</summary>
    public string EmptyPlotName { get; } = string.Empty;

    /// <summary>Seed pouch rows with their item id and icon (the mode-item menu shows items by icon).</summary>
    public IReadOnlyList<(int PouchRow, uint ItemId, uint IconId)> Seeds { get; } = Array.Empty<(int, uint, uint)>();

    /// <summary>EObj base ids of island gathering nodes, derived from the sheets (see the constructor).</summary>
    public IReadOnlySet<uint> NodeBaseIds { get; }
    public IReadOnlyDictionary<int, ProgressStep> Steps { get; }

    /// <summary>Recipe outputs that are Isleventory items (feed, restraints) — never "done", they're consumables.</summary>
    public IReadOnlyList<string> RecipeOutputs { get; }

    // [REPLACED: craftables now carry their recipe so a craft step can gather and craft]
    // public IReadOnlyList<NamedThing> Craftables()
    // {
    //     var mji = MJIManager.Instance();
    //     var tools = _keyItems.Select(k => new NamedThing(k.Name, mji != null && mji->IsKeyItemUnlocked((ushort)(k.Row - 1))));
    //     return tools.Concat(RecipeOutputs.Select(n => new NamedThing(n, false))).ToList();
    // }

    /// <summary>
    /// Tools the tutorial can ask for, with whether this character holds them (key-item bit row-1 — VERIFIED
    /// 2026-10-17 with the stone hatchet) and their recipe.
    /// </summary>
    public IReadOnlyList<Craftable> Craftables()
    {
        return _toolRecipes.Select(r => new Craftable(r.Name, HasKeyItem(r.KeyItemRow), r.RecipeRow, r.Materials, r.Automatable)).ToList();
    }

    /// <summary>Key item held (MJIKeyItem row N = IslandState bit N-1, verified).</summary>
    public bool HasKeyItem(int keyItemRow)
    {
        var mji = MJIManager.Instance();
        return mji != null && keyItemRow > 0 && mji->IsKeyItemUnlocked((ushort)(keyItemRow - 1));
    }

    /// <summary>The key item a recipe makes, or 0.</summary>
    public int KeyItemOf(int recipeRow) => _toolRecipes.FirstOrDefault(r => r.RecipeRow == recipeRow).KeyItemRow;

    /// <summary>
    /// MJIManager.IsRecipeUnlocked for a recipe row. UNVERIFIED bit index: recipe rows start at 0 (the hatchet), so the
    /// row is passed straight through (key items, which start at 1, needed row-1).
    /// </summary>
    public bool IsRecipeUnlocked(int recipeRow)
    {
        var mji = MJIManager.Instance();
        return mji != null && mji->IsRecipeUnlocked((ushort)recipeRow);
    }

    /// <summary>The craft log's (tab, entry index) for a recipe: its position among the tab's unlocked recipes, highest Order first.</summary>
    public (int Tab, int Index)? CraftLogEntry(int recipeRow)
    {
        var recipe = _recipes.FirstOrDefault(r => r.Row == recipeRow);
        if (recipe == null)
            return null;
        var index = _recipes.Where(r => r.Tab == recipe.Tab && (r.Row == recipeRow || IsRecipeUnlocked(r.Row)))
            .OrderByDescending(r => r.Order).ToList().FindIndex(r => r.Row == recipeRow);
        return index < 0 ? null : (recipe.Tab, index);
    }

    /// <summary>Where an event object (logboard, boat...) stands in the layout, if it's there.</summary>
    public Vector3? EObjPosition(uint baseId) => _eobjPositions.TryGetValue(baseId, out var p) ? p : null;

    public IReadOnlyList<(int Row, string Name, bool ById, bool ByRowMinusOne)> KeyItemReadings()
    {
        var mji = MJIManager.Instance();
        return _keyItems.Select(k => (k.Row, k.Name,
            mji != null && mji->IsKeyItemUnlocked((ushort)k.Row),
            mji != null && mji->IsKeyItemUnlocked((ushort)(k.Row - 1)))).ToList();
    }

    /// <summary>Cabin / workshop / granary names, done when the snapshot shows one built.</summary>
    public IReadOnlyList<NamedThing> Buildings(IslandSnapshot island) => _buildings.Select(b => new NamedThing(b.Name, b.Building switch
    {
        0 => island.CabinLevel > 0,
        1 => island.Workshops.Any(w => w.IsBuilt),
        2 => island.Granaries.Any(g => g.IsBuilt),
        _ => false,
    })).ToList();

    public ProgressStep? Step(int row) => Steps.TryGetValue(row, out var s) && !string.IsNullOrWhiteSpace(s.Objective) ? s : null;
}
