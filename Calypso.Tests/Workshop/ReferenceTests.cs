using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

/// <summary>
/// Worked examples from independent, licence-compatible workshop solvers, checked against our evaluator.
///
/// mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork, Apache-2.0, Jim Jiang): the numbers
/// below are quoted from its tests/simulate.rs. Commits 39ac819 and 5b473aa (January 2024) changed its rounding "to match
/// the game" and "to match measured results" and added the "margin value" cases, so those are in-game measurements.
/// Only numbers are taken from it; no code is ported.
///
/// WorkshopOptimizer (https://github.com/belzaru17/WorkshopOptimizerPlugin, MIT) values crafts in floating point with
/// no truncation, so it cannot serve as a whole-cowrie reference; where it disagrees, the test says which side is right.
/// </summary>
public class ReferenceTests
{
    // tier% / groove / base value / supply% / popularity% → cowries for one item (not efficient).
    [Theory]
    [InlineData(130, 25, 136, 160, 120, 422)] // exact integers give 424: 136 × 1.3 × 1.25 = 221.0 floors to 220 in float
    [InlineData(130, 12, 52, 130, 120, 116)]  // exact integers give 117: 75 × 1.56 = 117.0 floors to 116 in float
    [InlineData(130, 11, 52, 130, 120, 116)]
    [InlineData(130, 7, 54, 160, 80, 96)]
    [InlineData(130, 7, 54, 130, 80, 78)]
    [InlineData(120, 10, 32, 130, 120, 65)]  // the formula case: floor(floor(32 × 1.2 × 1.1) × 1.3 × 1.2)
    public void CraftValueMatchesTheValuesMeasuredInGame(int tier, int groove, int baseValue, int supply, int popularity, int expected)
    {
        Assert.Equal(expected, DayEvaluator.CraftValue(baseValue, tier, groove, popularity, supply, efficient: false));
        Assert.Equal(2 * expected, DayEvaluator.CraftValue(baseValue, tier, groove, popularity, supply, efficient: true));
    }

    [Fact]
    public void DayEvaluatorPaysWholeCowriesWhenSupplyIsKnown()
    {
        // Every craft observed Sufficient on day 0 and nothing made before: the supply ratio is certain (100%), so the
        // day's value is a sum of whole-cowrie sales, exactly what CraftValue says for each craft.
        var cat = Fixture.Catalog;
        var observed = new byte[]?[7];
        observed[0] = Enumerable.Repeat((byte)0x22, 91).ToArray();
        var forecast = new SupplyForecast(cat, observed, new int[]?[7]);
        var evaluator = new DayEvaluator(cat, forecast, popularityRow: 1, maxGroove: 45);

        // Two workshops, one craft each (no efficiency, so groove stays put): two whole-cowrie sales.
        var first = cat.Get(3)!;  // Wooden Chair, base 42
        var second = cat.Get(4)!; // Grilled Clam, base 28
        var (value, groove) = evaluator.Evaluate(0, new[] { new[] { first.Id }, new[] { second.Id } }, new[] { 5, 5 }, 10,
            new int[cat.MaxCraftId + 1], new int[cat.MaxCraftId + 1]);

        var expected = DayEvaluator.CraftValue(first.BaseValue, 140, 10, cat.Rules.PopularityRatio(1, first.Id), 100, false)
                       + DayEvaluator.CraftValue(second.BaseValue, 140, 10, cat.Rules.PopularityRatio(1, second.Id), 100, false);
        Assert.Equal(expected, value, 9);
        Assert.Equal(10, groove);
    }
}
