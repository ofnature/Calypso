using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Workshop;

/// <summary>Where an Isleventory material comes from — decides whether the solver may plan to gather more.</summary>
public enum MaterialKind
{
    /// <summary>MJIItemCategory 1 and not a granary rare: gathered on the island, so a shortfall is a gather job.</summary>
    Gatherable,

    /// <summary>Granary-only rare (MJIStockyardManagementArea.RareMaterial). Hard cap: only what is on hand.</summary>
    GranaryRare,

    /// <summary>MJIItemCategory 4 (cropland). Hard cap.</summary>
    Produce,

    /// <summary>MJIItemCategory 5 (pasture). Hard cap.</summary>
    Leavings,

    /// <summary>Anything else a recipe might name. Hard cap.</summary>
    Other,
}

public sealed record MaterialNeed(int PouchRow, int Amount);

/// <summary>One MJICraftworksObject row. <see cref="Id"/> is the row id — the id ScheduleCraft takes.</summary>
public sealed record CraftDef(
    int Id,
    uint ItemId,
    string Name,
    IReadOnlyList<int> Themes,
    int Hours,
    int BaseValue,
    int RankReq,
    IReadOnlyList<MaterialNeed> Materials)
{
    /// <summary>The efficiency bonus: a different craft sharing at least one theme with the previous one.</summary>
    public bool ChainsAfter(CraftDef previous) =>
        previous.Id != Id && Themes.Any(t => previous.Themes.Contains(t));
}

public sealed record PouchDef(int Row, uint ItemId, string Name, MaterialKind Kind);

/// <summary>
/// The workshop's tables from the sheets (claude-memory.md "Sheets"): popularity per week row, the popularity,
/// workshop tier and supply ratios, and the groove cap per landmark count.
/// </summary>
public sealed class WorkshopRules
{
    /// <param name="popularityTypeRatio">MJICraftworksPopularityType row → ratio %.</param>
    /// <param name="popularity">MJICraftworksPopularity row → per craft row, a PopularityType row.</param>
    /// <param name="rankRatio">MJICraftworksRankRatio row (workshop tier 1-5) → ratio %.</param>
    /// <param name="supplyRatio">MJICraftworksSupplyDefine row (= <see cref="Supply"/>) → ratio %.</param>
    /// <param name="maxGroove">MJICraftworksTension row (landmarks built, inferred) → groove cap.</param>
    public WorkshopRules(int[] popularityTypeRatio, int[][] popularity, int[] rankRatio, int[] supplyRatio, int[] maxGroove)
    {
        PopularityTypeRatio = popularityTypeRatio;
        Popularity = popularity;
        RankRatio = rankRatio;
        SupplyRatio = supplyRatio;
        MaxGrooveByLandmarks = maxGroove;
    }

    public int[] PopularityTypeRatio { get; }
    public int[][] Popularity { get; }
    public int[] RankRatio { get; }
    public int[] SupplyRatio { get; }
    public int[] MaxGrooveByLandmarks { get; }

    /// <summary>Popularity ratio % for a craft in the given week's popularity row. Unknown = 100 (neutral).</summary>
    public int PopularityRatio(int popularityRow, int craftId)
    {
        if (popularityRow < 0 || popularityRow >= Popularity.Length)
            return 100;
        var row = Popularity[popularityRow];
        if (craftId < 0 || craftId >= row.Length)
            return 100;
        var type = row[craftId];
        return type > 0 && type < PopularityTypeRatio.Length ? PopularityTypeRatio[type] : 100;
    }

    /// <summary>Workshop tier (1-5) → value ratio %; unbuilt/unknown tiers produce nothing.</summary>
    public int TierRatio(int tier) => tier > 0 && tier < RankRatio.Length ? RankRatio[tier] : 0;

    public int MaxGroove(int landmarksBuilt) =>
        MaxGrooveByLandmarks.Length == 0 ? 0 : MaxGrooveByLandmarks[Math.Clamp(landmarksBuilt, 0, MaxGrooveByLandmarks.Length - 1)];
}

public sealed class CraftCatalog
{
    private readonly CraftDef?[] _byId;

    public CraftCatalog(IEnumerable<CraftDef> crafts, IEnumerable<PouchDef> pouch, WorkshopRules rules)
    {
        Crafts = crafts.OrderBy(c => c.Id).ToList();
        MaxCraftId = Crafts.Count == 0 ? 0 : Crafts.Max(c => c.Id);
        _byId = new CraftDef?[MaxCraftId + 1];
        foreach (var c in Crafts)
            _byId[c.Id] = c;

        Pouch = pouch.ToDictionary(p => p.Row);
        Rules = rules;
    }

    public IReadOnlyList<CraftDef> Crafts { get; }
    public int MaxCraftId { get; }
    public IReadOnlyDictionary<int, PouchDef> Pouch { get; }
    public WorkshopRules Rules { get; }

    public CraftDef? Get(int id) => id >= 0 && id < _byId.Length ? _byId[id] : null;

    public MaterialKind KindOf(int pouchRow) => Pouch.TryGetValue(pouchRow, out var p) ? p.Kind : MaterialKind.Other;

    /// <summary>Classify an MJIItemPouch row from its MJIItemCategory and the granary rare list.</summary>
    public static MaterialKind Classify(int category, int pouchRow, ISet<int> granaryRareRows) =>
        granaryRareRows.Contains(pouchRow) ? MaterialKind.GranaryRare
        : category switch
        {
            1 => MaterialKind.Gatherable,
            4 => MaterialKind.Produce,
            5 => MaterialKind.Leavings,
            _ => MaterialKind.Other,
        };
}
