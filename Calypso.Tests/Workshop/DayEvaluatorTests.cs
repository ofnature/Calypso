using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

/// <summary>Properties of one simulated day (<see cref="DayEvaluator"/>) against the game rules, on the real catalog.</summary>
public class DayEvaluatorTests
{
    private static readonly CraftCatalog Cat = Fixture.Catalog;

    // Every craft observed Sufficient on day 0, so a sale's supply is certain until our own items move it.
    private static SupplyForecast AllSufficientOnDay0()
    {
        var observed = new byte[]?[7];
        observed[0] = Enumerable.Repeat((byte)0x22, 91).ToArray();
        return new SupplyForecast(Cat, observed, new int[]?[7]);
    }

    private static (double Value, int Groove, int[] Today) Run(int[][] chains, int startGroove = 0, int maxGroove = 45, int tier = 5,
        SupplyForecast? forecast = null, int day = 0, int[]? before = null)
    {
        var evaluator = new DayEvaluator(Cat, forecast ?? AllSufficientOnDay0(), popularityRow: 1, maxGroove);
        var today = new int[Cat.MaxCraftId + 1];
        var (value, groove) = evaluator.Evaluate(day, chains, chains.Select(_ => tier).ToArray(), startGroove,
            before ?? new int[Cat.MaxCraftId + 1], today);
        return (value, groove, today);
    }

    private static int Pop(int craft) => Cat.Rules.PopularityRatio(1, craft);

    [Fact]
    public void ARepeatedCraftIsNotEfficient()
    {
        // The same craft twice in a row breaks the chain: one item, no groove.
        var potion = Cat.Get(1)!; // 4 h
        var (value, groove, today) = Run(new[] { new[] { 1, 1 } }, startGroove: 3);

        Assert.Equal(3, groove);
        Assert.Equal(2, today[1]);
        // The second Potion sells after the first one's item: 1 of the 8 that move a bucket (7/8 Sufficient, 1/8 Surplus).
        double Sale(int supply) => DayEvaluator.CraftValue(potion.BaseValue, 140, 3, Pop(1), supply, false);
        Assert.Equal(Sale(100) + 7 / 8.0 * Sale(100) + 1 / 8.0 * Sale(80), value, 9);
    }

    [Fact]
    public void ACraftSharingNoThemeIsNotEfficient()
    {
        var a = Cat.Get(1)!; // Potion: Concoctions
        var b = Cat.Crafts.First(c => c.Hours == 4 && !c.Themes.Intersect(a.Themes).Any());
        var (value, groove, today) = Run(new[] { new[] { a.Id, b.Id } });

        Assert.Equal(0, groove);
        Assert.Equal(1, today[b.Id]);
        Assert.Equal(DayEvaluator.CraftValue(a.BaseValue, 140, 0, Pop(a.Id), 100, false)
                     + DayEvaluator.CraftValue(b.BaseValue, 140, 0, Pop(b.Id), 100, false), value, 9);
    }
}
