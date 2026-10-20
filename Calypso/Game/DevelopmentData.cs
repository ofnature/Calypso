using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Development;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>
/// The sheets behind the development planner (claude-memory.md "Step 27 explained"; field meanings checked against
/// the community wiki's rank table — see <see cref="DevelopmentPlanner"/>), plus the live state it plans from.
/// </summary>
public sealed class DevelopmentData
{
    private readonly IslandLayout _layout;
    private readonly HashSet<int> _gatheringKeyItems;
    private readonly HashSet<int> _builderKeyItems;
    private readonly List<(int Row, int DevelopmentLevel)> _landmarkPlaces;

    public DevelopmentData(IDataManager data, IslandLayout layout, IPluginLog log)
    {
        _layout = layout;

        Tiers = data.GetSubrowExcelSheet<MJIBuilding>().SelectMany(s => s)
            .Where(b => b.Name.IsValid)
            .Select(b => new BuildingTier((int)b.RowId, b.SubrowId, b.Name.Value.Text.ExtractText(), b.Unknown1, Materials(b.Material.Select(m => m.RowId), b.Amount)))
            .ToList();

        Landmarks = data.GetExcelSheet<MJILandmark>().Where(l => l.RowId > 0 && l.Name.IsValid)
            .Select(l => new LandmarkInfo((int)l.RowId, l.Name.Value.Text.ExtractText(), l.Unknown2, l.Unknown3, l.UnlockLink,
                Materials(l.Material.Select(m => m.RowId), l.Amount)))
            .ToList();

        // MJILandmarkPlace.Unknown3 = development level the place needs (same byte as MJIBuildingPlace, +0x10).
        _landmarkPlaces = data.GetExcelSheet<MJILandmarkPlace>().Select(p => ((int)p.RowId, (int)p.Unknown3)).ToList();

        var residents = data.GetExcelSheet<ENpcResident>();
        Pathfinders = data.GetExcelSheet<MJIVillageDevelopment>().Where(v => v.RowId > 0 && v.ENPC.RowId != 0)
            .Select(v => new PathfinderStep((int)v.RowId, v.ENPC.RowId, residents.GetRowOrDefault(v.ENPC.RowId)?.Singular.ExtractText() ?? $"npc {v.ENPC.RowId}",
                v.Unknown5, v.Unknown6))
            .ToList();
        _builderKeyItems = Pathfinders.Select(p => p.ToolsKeyItem).Where(k => k > 0).ToHashSet();

        // MJIGatheringTool.Unknown0 = MJIKeyItem row (GatherTools).
        _gatheringKeyItems = data.GetExcelSheet<MJIGatheringTool>().Where(t => t.RowId > 0).Select(t => (int)t.Unknown0).ToHashSet();

        log.Information("Development data: {0} building tiers, {1} landmarks, {2} landmark places, pathfinders {3}",
            Tiers.Count, Landmarks.Count, _landmarkPlaces.Count,
            string.Join(", ", Pathfinders.Select(p => $"{p.Level}:{p.NpcName} r{p.Rank} tools {p.ToolsKeyItem}")));
    }

    public IReadOnlyList<BuildingTier> Tiers { get; }
    public IReadOnlyList<LandmarkInfo> Landmarks { get; }
    public IReadOnlyList<PathfinderStep> Pathfinders { get; }

    public IReadOnlyList<ToolRecipe> Tools() => _layout.Recipes.Where(r => r.KeyItemRow > 0)
        .Select(r => new ToolRecipe(r.Row, r.Name, r.KeyItemRow, _layout.IsRecipeUnlocked(r.Row), _layout.HasKeyItem(r.KeyItemRow), r.Automatable,
            _gatheringKeyItems.Contains(r.KeyItemRow) ? ToolPurpose.Gathering : _builderKeyItems.Contains(r.KeyItemRow) ? ToolPurpose.Builders : ToolPurpose.HandIn,
            r.Materials))
        .ToList();

    public DevelopmentInput Input(IslandSnapshot island, Func<int, int> pouchCount) => new(
        island.Rank,
        island.VillageDevelopmentLevel,
        island.CabinLevel,
        island.Workshops,
        island.Granaries,
        island.LandmarkIds,
        _layout.FreePlaces(1, island).Count(),
        _landmarkPlaces.Count(p => p.DevelopmentLevel <= island.VillageDevelopmentLevel && p.Row < island.LandmarkIds.Count && island.LandmarkIds[p.Row] == 0),
        pouchCount);

    public IReadOnlyList<DevelopmentTask> Plan(IslandSnapshot island, Func<int, int> pouchCount) =>
        DevelopmentPlanner.Plan(Input(island, pouchCount), Tiers, Landmarks, Pathfinders, Tools());

    private static IReadOnlyList<(int, int)> Materials(IEnumerable<uint> rows, IEnumerable<byte> amounts) =>
        rows.Zip(amounts).Where(m => m.Second > 0 && m.First != 255).Select(m => ((int)m.First, (int)m.Second)).ToList();
}
