using System.Text.Json;
using Calypso.Core.Granary;

namespace Calypso.Tests.Granary;

/// <summary>The real expedition tables (Fixtures/granary.json, dumped from the game 2026-10-27) as expedition areas.</summary>
internal static class GranaryFixture
{
    private static readonly Lazy<IReadOnlyList<ExpeditionArea>> Lazy = new(Load);

    public static IReadOnlyList<ExpeditionArea> Areas => Lazy.Value;

    /// <summary>Item names by pouch row, as the fixture spells them.</summary>
    public static IReadOnlyDictionary<int, string> Names
    {
        get
        {
            _ = Lazy.Value;
            return _names;
        }
    }

    private static IReadOnlyDictionary<int, string> _names = new Dictionary<int, string>();

    private static IReadOnlyList<ExpeditionArea> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "granary.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var names = new Dictionary<int, string>();
        var areas = doc.RootElement.GetProperty("areas").EnumerateArray().Select(a =>
        {
            var id = a.GetProperty("area").GetInt32();
            var rare = a.GetProperty("rare");
            names[rare.GetProperty("pouch").GetInt32()] = rare.GetProperty("name").GetString()!;
            var normal = a.GetProperty("normal").EnumerateArray().Select(n =>
            {
                names[n.GetProperty("pouch").GetInt32()] = n.GetProperty("name").GetString()!;
                return n.GetProperty("pouch").GetInt32();
            }).ToArray();
            return new ExpeditionArea(id, ExpeditionArea.KnownName(id), rare.GetProperty("pouch").GetInt32(), normal);
        }).ToList();
        _names = names;
        return areas;
    }
}
