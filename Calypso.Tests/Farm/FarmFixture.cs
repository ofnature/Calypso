using System.Text.Json;
using Calypso.Core.Farm;
using Calypso.Core.Granary;

namespace Calypso.Tests.Farm;

/// <summary>The datamined crops (Fixtures/cropland.json: MJICropSeed joined with MJIItemPouch) as <see cref="CropSeed"/>s.</summary>
internal static class FarmFixture
{
    private static readonly Lazy<(IReadOnlyList<CropSeed> Crops, IReadOnlyDictionary<int, string> Names)> Lazy = new(Load);

    public static IReadOnlyList<CropSeed> Crops => Lazy.Value.Crops;

    /// <summary>Seed and produce names by pouch row, as the fixture spells them.</summary>
    public static IReadOnlyDictionary<int, string> Names => Lazy.Value.Names;

    private static (IReadOnlyList<CropSeed>, IReadOnlyDictionary<int, string>) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cropland.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var names = new Dictionary<int, string>();
        var crops = doc.RootElement.GetProperty("crops").EnumerateArray().Select(c =>
        {
            var seed = c.GetProperty("seed");
            var produce = c.GetProperty("produce");
            names[seed.GetProperty("pouch").GetInt32()] = seed.GetProperty("name").GetString()!;
            names[produce.GetProperty("pouch").GetInt32()] = produce.GetProperty("name").GetString()!;
            return new CropSeed(c.GetProperty("crop").GetInt32(), seed.GetProperty("pouch").GetInt32(), seed.GetProperty("itemId").GetUInt32(),
                produce.GetProperty("pouch").GetInt32(), produce.GetProperty("itemId").GetUInt32(), produce.GetProperty("name").GetString()!);
        }).ToList();
        return (crops, names);
    }

    /// <summary>MJIFarmPastureRank.RankData[level].Unknown7 for the cropland (row 0) and pasture (row 1).</summary>
    public static (int[] Cropland, int[] Pasture) RankUnknown7()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cropland.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement.GetProperty("rankUnknown7");
        return (r.GetProperty("row0").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
            r.GetProperty("row1").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }
}

/// <summary>Planner inputs on the real crops: level 3, 100 of every produce and 50 of every seed on hand, nothing needed.</summary>
internal static class Farms
{
    // MJICropSeed rows.
    public const int Popoto = 1, Cabbage = 2, Isleberry = 3, Pumpkin = 4, Onion = 5, Tomato = 6, Radish = 10;

    public static CropSeed Crop(int id) => FarmFixture.Crops.First(c => c.Id == id);

    public static int Produce(int crop) => Crop(crop).ProducePouchRow;

    public static int Seed(int crop) => Crop(crop).SeedPouchRow;

    public static CropPlot Empty(int slot) => new() { Slot = slot };

    public static CropPlot Growing(int slot, int crop, int growth = 0, int waiting = 0) =>
        new() { Slot = slot, Crop = crop, GrowthLevel = growth, Waiting = waiting, UnderCare = true, WasUnderCare = true };

    public static CropPlot[] EmptyPlots(int count) => Enumerable.Range(0, count).Select(Empty).ToArray();

    public static Dictionary<int, int> OnHand(int produce = 100, int seeds = 50, params (int Row, int Count)[] except)
    {
        var onHand = new Dictionary<int, int>();
        foreach (var c in FarmFixture.Crops)
        {
            onHand[c.ProducePouchRow] = produce;
            onHand[c.SeedPouchRow] = seeds;
        }

        foreach (var (row, count) in except)
            onHand[row] = count;
        return onHand;
    }

    public static GranaryNeed Short(int row, int amount, int due = 6) => new(row, amount, due, NeedSource.WorkshopThisWeek);

    public static FarmInput Input(IReadOnlyList<CropPlot> plots, IReadOnlyList<GranaryNeed>? needs = null,
        IReadOnlyDictionary<int, int>? onHand = null, int cowries = 10_000, int level = 3, FarmRules? rules = null) => new()
    {
        Level = level,
        Plots = plots,
        Crops = FarmFixture.Crops,
        OnHand = onHand ?? OnHand(),
        Cowries = cowries,
        Needs = needs ?? Array.Empty<GranaryNeed>(),
        MaterialNames = FarmFixture.Names,
        Rules = rules ?? new FarmRules(),
    };

    public static FarmPlan Plan(IReadOnlyList<CropPlot> plots, IReadOnlyList<GranaryNeed>? needs = null, IReadOnlyDictionary<int, int>? onHand = null) =>
        FarmPlanner.Plan(Input(plots, needs, onHand));

    /// <summary>Crop id each plot plants or replants today (null = none), in slot order.</summary>
    public static int?[] Planted(FarmPlan plan) =>
        plan.Plots.Select(p => p.Action is FarmAction.Plant or FarmAction.Replant ? p.Crop : null).ToArray();
}
