using System.Text.Json;
using Calypso.Core.Granary;
using Calypso.Core.Pasture;

namespace Calypso.Tests.Pasture;

/// <summary>The datamined animals (Fixtures/pasture.json: MJIAnimals with its rewards as pouch rows) as <see cref="AnimalDef"/>s.</summary>
internal static class PastureFixture
{
    private static readonly Lazy<(IReadOnlyList<AnimalDef> Animals, IReadOnlyList<int> Leavings, IReadOnlyDictionary<int, string> Names)> Lazy = new(Load);

    public static IReadOnlyList<AnimalDef> Animals => Lazy.Value.Animals;

    /// <summary>The nine leavings' pouch rows (MJIItemPouch category 5).</summary>
    public static IReadOnlyList<int> Leavings => Lazy.Value.Leavings;

    public static IReadOnlyDictionary<int, string> Names => Lazy.Value.Names;

    public static AnimalDef Animal(int row) => Animals.First(a => a.Row == row);

    private static (IReadOnlyList<AnimalDef>, IReadOnlyList<int>, IReadOnlyDictionary<int, string>) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pasture.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var names = new Dictionary<int, string>();
        var leavings = root.GetProperty("leavings").EnumerateArray().Select(l =>
        {
            names[l.GetProperty("pouch").GetInt32()] = l.GetProperty("name").GetString()!;
            return l.GetProperty("pouch").GetInt32();
        }).ToList();
        var animals = root.GetProperty("animals").EnumerateArray().Select(a => new AnimalDef(
            a.GetProperty("row").GetInt32(),
            a.GetProperty("bnpcBase").GetUInt32(),
            a.GetProperty("leavings").EnumerateArray().Select(l => l.GetProperty("pouch").GetInt32()).ToArray(),
            a.GetProperty("size").GetInt32(),
            (AnimalRarity)a.GetProperty("rarity").GetInt32())).ToList();
        return (animals, leavings, names);
    }
}

/// <summary>Planner inputs on the real species: level 3, 100 of every leaving on hand, nothing needed.</summary>
internal static class Pastures
{
    public const int Fleece = 52, Claw = 53, Fur = 54, Feather = 55, Egg = 56, Carapace = 57, Fang = 58, Horn = 59, Milk = 60;

    // MJIAnimals rows: guaranteed leaving / second leaving, size, rarity.
    public const int FleeceMilk = 1;  // size 1, common
    public const int ClawFur = 3;     // size 1, common
    public const int EggFeather = 11; // size 2, common
    public const int EggFeatherLarge = 23; // size 3, common

    public const uint Sweetfeed = 37612, Greenfeed = 37613, Premium = 37614;

    public static PenAnimal In(int slot, int animal, int mood = 2, uint feed = 0, int waiting = 0) =>
        new() { Slot = slot, Animal = animal, Mood = mood, UnderCare = feed != 0, FeedItemId = feed, Waiting = waiting };

    public static Dictionary<int, int> OnHand(int each = 100, params (int Row, int Count)[] except)
    {
        var onHand = PastureFixture.Leavings.ToDictionary(r => r, _ => each);
        foreach (var (row, count) in except)
            onHand[row] = count;
        return onHand;
    }

    public static GranaryNeed Short(int row, int amount, int due = 6) => new(row, amount, due, NeedSource.WorkshopThisWeek);

    public static PastureInput Input(IReadOnlyList<PenAnimal>? animals = null, IReadOnlyList<GranaryNeed>? needs = null,
        IReadOnlyDictionary<int, int>? waiting = null, IReadOnlyDictionary<int, int>? onHand = null, int level = 3,
        int cowries = 10_000, IReadOnlySet<uint>? sighted = null) => new()
    {
        Level = level,
        Animals = animals ?? Array.Empty<PenAnimal>(),
        Species = PastureFixture.Animals,
        Waiting = waiting ?? new Dictionary<int, int>(),
        OnHand = onHand ?? OnHand(),
        Cowries = cowries,
        Needs = needs ?? Array.Empty<GranaryNeed>(),
        Sighted = sighted,
        MaterialNames = PastureFixture.Names,
    };

    public static List<PastureAdvice> Of(PasturePlan plan, AdviceKind kind) => plan.Advice.Where(a => a.Kind == kind).ToList();
}
