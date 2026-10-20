using System.Text.Json;
using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

/// <summary>The real craftworks tables (Fixtures/craftworks.json, dumped from the game 2026-10-17) as a <see cref="CraftCatalog"/>.</summary>
internal static class Fixture
{
    private static readonly Lazy<CraftCatalog> Lazy = new(Load);

    public static CraftCatalog Catalog => Lazy.Value;

    private static CraftCatalog Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "craftworks.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var rare = root.GetProperty("granaryRarePouchRows").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
        var pouch = root.GetProperty("pouch").EnumerateArray().Select(p =>
        {
            var row = p.GetProperty("row").GetInt32();
            return new PouchDef(row, p.GetProperty("itemId").GetUInt32(), p.GetProperty("name").GetString()!,
                CraftCatalog.Classify(p.GetProperty("category").GetInt32(), row, rare));
        });

        var crafts = root.GetProperty("crafts").EnumerateArray().Select(c => new CraftDef(
            c.GetProperty("id").GetInt32(),
            c.GetProperty("itemId").GetUInt32(),
            c.GetProperty("name").GetString()!,
            c.GetProperty("themes").EnumerateArray().Select(t => t.GetInt32()).ToArray(),
            c.GetProperty("hours").GetInt32(),
            c.GetProperty("value").GetInt32(),
            c.GetProperty("rank").GetInt32(),
            c.GetProperty("materials").EnumerateArray()
                .Select(m => new MaterialNeed(m.GetProperty("pouch").GetInt32(), m.GetProperty("amount").GetInt32())).ToArray()));

        var rules = new WorkshopRules(
            Table(root.GetProperty("popularityTypeRatio")),
            root.GetProperty("popularity").EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray(),
            Table(root.GetProperty("rankRatio")),
            Table(root.GetProperty("supplyRatio")),
            Table(root.GetProperty("maxGroove")));

        return new CraftCatalog(crafts, pouch, rules);
    }

    private static int[] Table(JsonElement obj)
    {
        var pairs = obj.EnumerateObject().Select(p => (int.Parse(p.Name), p.Value.GetInt32())).ToList();
        var t = new int[pairs.Max(p => p.Item1) + 1];
        foreach (var (k, v) in pairs)
            t[k] = v;
        return t;
    }

    /// <summary>Plenty of every material — for tests that are not about materials.</summary>
    public static Dictionary<int, int> AllMaterials(int each = 999) => Catalog.Pouch.Keys.ToDictionary(k => k, _ => each);

    public static SupplyForecast NoObservations() =>
        new(Catalog, new byte[]?[7], new int[]?[7]);
}
