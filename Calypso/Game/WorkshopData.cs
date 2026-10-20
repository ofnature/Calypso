using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Workshop;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>
/// The workshop's game data, read from the sheets once at load (column meanings: claude-memory.md "Sheets").
/// Also answers rank mode's question "what constructions are unlocked but not done", from MJIBuilding / MJILandmark.
/// </summary>
public sealed class WorkshopData
{
    private readonly List<(int Building, int Tier, int RankReq, string Name, MaterialNeed[] Cost)> _buildings = new();
    private readonly List<(int Row, int RankReq, int DevLevel, string Name, MaterialNeed[] Cost)> _landmarks = new();

    public WorkshopData(IDataManager data, IPluginLog log)
    {
        var rareRows = data.GetExcelSheet<MJIStockyardManagementArea>()
            .Select(r => (int)r.RareMaterial.RowId).Where(r => r > 0).ToHashSet();

        var pouch = data.GetExcelSheet<MJIItemPouch>().Select(p => new PouchDef(
            (int)p.RowId,
            p.Item.RowId,
            p.Item.IsValid ? p.Item.Value.Name.ExtractText() : string.Empty,
            CraftCatalog.Classify((int)p.Category.RowId, (int)p.RowId, rareRows))).ToList();

        var crafts = data.GetExcelSheet<MJICraftworksObject>()
            .Where(c => c.Item.RowId != 0)
            .Select(c => new CraftDef(
                (int)c.RowId,
                c.Item.RowId,
                c.Item.IsValid ? c.Item.Value.Name.ExtractText() : $"#{c.RowId}",
                c.Theme.Select(t => (int)t.RowId).Where(t => t != 0).ToArray(),
                c.CraftingTime,
                c.Value,
                c.LevelReq,
                // Pouch row 0 is a real item: empty slots are Amount 0, never "row 0".
                c.Material.Zip(c.Amount).Where(m => m.Second > 0).Select(m => new MaterialNeed((int)m.First.RowId, m.Second)).ToArray()))
            .ToList();

        var popType = data.GetExcelSheet<MJICraftworksPopularityType>();
        var rules = new WorkshopRules(
            Table(popType.Select(r => ((int)r.RowId, (int)r.Ratio))),
            data.GetExcelSheet<MJICraftworksPopularity>().Select(r => r.Popularity.Select(p => (int)p.RowId).ToArray()).ToArray(),
            Table(data.GetExcelSheet<MJICraftworksRankRatio>().Select(r => ((int)r.RowId, (int)r.Ratio))),
            Table(data.GetExcelSheet<MJICraftworksSupplyDefine>().Select(r => ((int)r.RowId, (int)r.Ratio))),
            Table(data.GetExcelSheet<MJICraftworksTension>().Select(r => ((int)r.RowId, (int)r.Unknown0))));

        Catalog = new CraftCatalog(crafts, pouch, rules);

        // MJINekomimiRequest rows 1-3 Unknown0 = 8/6/8 — inferred as the required count for the 4h/6h/8h favor.
        FavorDefaultRequired = data.GetExcelSheet<MJINekomimiRequest>()
            .Where(r => r.RowId is >= 1 and <= 3).OrderBy(r => r.RowId).Select(r => (int)r.Unknown0).ToArray();

        // MJIBuilding: row 0 cabin, 1 workshop, 2 granary; subrow = tier-1; Unknown1 = required rank.
        foreach (var b in data.GetSubrowExcelSheet<MJIBuilding>().SelectMany(s => s))
        {
            var name = b.Name.IsValid ? b.Name.Value.Text.ExtractText() : $"building {b.RowId}";
            _buildings.Add(((int)b.RowId, b.SubrowId + 1, b.Unknown1, name, Cost(b.Material.Select(m => (int)m.RowId), b.Amount)));
        }

        // MJILandmark: Unknown3 = required rank (12, 19 seen), Unknown2 ≈ village development level — both inferred.
        foreach (var l in data.GetExcelSheet<MJILandmark>().Where(l => l.RowId > 0))
        {
            var name = l.Name.IsValid ? l.Name.Value.Text.ExtractText() : $"landmark {l.RowId}";
            _landmarks.Add(((int)l.RowId, l.Unknown3, l.Unknown2, name, Cost(l.Material.Select(m => (int)m.RowId), l.Amount)));
        }

        log.Information("Workshop data: {0} crafts, {1} pouch items, {2} building tiers, {3} landmarks",
            crafts.Count, pouch.Count, _buildings.Count, _landmarks.Count);
    }

    public CraftCatalog Catalog { get; }

    /// <summary>Required count per favor position within a week's group (4h, 6h, 8h). Inferred; the agent's NumRequired wins.</summary>
    public int[] FavorDefaultRequired { get; }

    /// <summary>
    /// Rank mode's reservation: the next tier of the cabin, every built workshop and granary, the first unbuilt
    /// workshop/granary slot, and the first unbuilt landmark — whichever the current rank already allows.
    /// An approximation (decided "never consume what the next construction needs"); farm/pasture upgrades are not
    /// in it because their costs are not in a sheet Calypso has decoded.
    /// </summary>
    public IReadOnlyList<Construction> UnlockedConstructions(IslandSnapshot island)
    {
        var result = new List<Construction>();
        var rank = island.Rank;

        void Next(int building, int currentTier)
        {
            var t = _buildings.FirstOrDefault(b => b.Building == building && b.Tier == currentTier + 1);
            if (t.Cost != null && t.RankReq <= rank)
                result.Add(new Construction($"{t.Name} {Roman(t.Tier)}", t.RankReq, t.Cost));
        }

        Next(0, island.CabinLevel);

        foreach (var (slots, building) in new[] { (island.Workshops, 1), (island.Granaries, 2) })
        {
            foreach (var slot in slots.Where(s => s.IsBuilt))
                Next(building, slot.Level);
            if (slots.Any(s => !s.IsBuilt))
                Next(building, 0);
        }

        if (island.LandmarkIds.Contains((byte)0))
        {
            var built = island.LandmarkIds.Where(id => id != 0).Select(id => (int)id).ToHashSet();
            var next = _landmarks.FirstOrDefault(l => !built.Contains(l.Row) && l.RankReq <= rank && l.DevLevel <= island.VillageDevelopmentLevel);
            if (next.Cost != null)
                result.Add(new Construction(next.Name, next.RankReq, next.Cost));
        }

        return result;
    }

    private static MaterialNeed[] Cost(IEnumerable<int> rows, IEnumerable<byte> amounts) =>
        rows.Zip(amounts).Where(p => p.Second > 0).Select(p => new MaterialNeed(p.First, p.Second)).ToArray();

    private static int[] Table(IEnumerable<(int Row, int Value)> rows)
    {
        var list = rows.ToList();
        var t = new int[list.Count == 0 ? 0 : list.Max(r => r.Row) + 1];
        foreach (var (row, value) in list)
            t[row] = value;
        return t;
    }

    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };
}
