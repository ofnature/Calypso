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

/// <summary>Planner inputs on the real areas: level-5 granaries, 100 of everything on hand, every area open.</summary>
internal static class Granaries
{
    public const int Alyssum = 27, Garnet = 28, Spruce = 29, Hammerhead = 30, Silver = 31, CaveShrimp = 83;
    public const int Sand = 8, Coal = 76;

    public static GranarySlot Home(int index, int level = 5, IReadOnlyDictionary<int, int>? waiting = null) =>
        new() { Index = index, Level = level, Waiting = waiting ?? new Dictionary<int, int>() };

    public static GranarySlot Away(int index, int area, int days, IReadOnlyDictionary<int, int>? waiting = null, int level = 5) =>
        new() { Index = index, Level = level, Area = area, DaysRemaining = days, Waiting = waiting ?? new Dictionary<int, int>() };

    public static Dictionary<int, int> OnHand(int each = 100, params (int Row, int Count)[] except)
    {
        var onHand = Enumerable.Range(0, 109).ToDictionary(r => r, _ => each);
        foreach (var (row, count) in except)
            onHand[row] = count;
        return onHand;
    }

    public static GranaryNeed Short(int row, int amount, int due = 6) => new(row, amount, due, NeedSource.WorkshopThisWeek);

    public static GranaryInput Input(IReadOnlyList<GranarySlot> slots, IReadOnlyList<GranaryNeed>? needs = null,
        IReadOnlyDictionary<int, int>? onHand = null, int cowries = 10_000, GranaryRules? rules = null,
        IEnumerable<int>? unlocked = null, IReadOnlySet<int>? gatheredBefore = null) => new()
    {
        Granaries = slots,
        Areas = GranaryFixture.Areas,
        UnlockedAreas = (unlocked ?? Enumerable.Range(0, 6)).ToHashSet(),
        OnHand = onHand ?? OnHand(),
        Cowries = cowries,
        Needs = needs ?? Array.Empty<GranaryNeed>(),
        GatheredBefore = gatheredBefore,
        MaterialNames = GranaryFixture.Names,
        Rules = rules ?? new GranaryRules(),
    };

    public static GranaryPlan Plan(params GranarySlot[] slots) => GranaryPlanner.Plan(Input(slots));
}
