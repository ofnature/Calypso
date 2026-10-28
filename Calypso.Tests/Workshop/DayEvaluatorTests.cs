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

    [Fact]
    public void BaseIsTruncatedBeforePopularityAndSupply()
    {
        // 42 × 1.4 × 1.1 = 64.68 → 64, then × 1.4 × 1.3 = 116.48 → 116. Truncating once at the end would give 117.
        Assert.Equal(116, DayEvaluator.CraftValue(42, 140, 10, 140, 130, efficient: false));
    }

    [Fact]
    public void AnEfficientCraftPaysTwoItemsAtItsOwnGroove()
    {
        var a = Cat.Get(3)!; // Wooden Chair 6 h
        var b = Cat.Crafts.First(c => c.ChainsAfter(a));
        var (value, groove, today) = Run(new[] { new[] { a.Id, b.Id } }, startGroove: 7);

        Assert.Equal(8, groove);
        Assert.Equal(2, today[b.Id]);
        Assert.Equal(DayEvaluator.CraftValue(a.BaseValue, 140, 7, Pop(a.Id), 100, false)
                     + DayEvaluator.CraftValue(b.BaseValue, 140, 8, Pop(b.Id), 100, true), value, 9);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(30, 45)]
    [InlineData(60, 45)] // a start above the cap (landmark count read wrong) still sells at the cap
    public void GrooveNeverExceedsItsCap(int startGroove, int cap)
    {
        // Four workshops of six 4-hour crafts: 20 efficient starts.
        var chain = ChainGenerator.TopChains(Cat, c => c.Hours == 4, id => Cat.Get(id)!.BaseValue, 1).Single();
        Assert.Equal(6, chain.Length);
        var (value, groove, _) = Run(new[] { chain, chain, chain, chain }, startGroove, cap);
        var uncapped = Run(new[] { chain, chain, chain, chain }, Math.Min(startGroove, cap), maxGroove: 99);

        Assert.Equal(cap, groove);
        Assert.True(value <= uncapped.Value);
        Assert.Equal(Oracle(new[] { chain, chain, chain, chain }, startGroove, cap), value, 9);
    }

    [Fact]
    public void ProductionBeforeAnObservedDayIsAlreadyInItsSupply()
    {
        var chain = new[] { 3, 8 }; // Wooden Chair, then a different craft sharing a theme
        Assert.True(Cat.Get(8)!.ChainsAfter(Cat.Get(3)!));
        var before = new int[Cat.MaxCraftId + 1];
        before[3] = 8;
        before[8] = 8;

        var observedDay = Run(new[] { chain }, before: before).Value;
        Assert.Equal(Run(new[] { chain }).Value, observedDay, 9);

        // Day 3 is not observed: last days' 8 items each push the forecast down a whole bucket.
        var unobserved = Run(new[] { chain }, day: 3).Value;
        var afterOurs = Run(new[] { chain }, day: 3, before: before).Value;
        Assert.True(afterOurs < unobserved, $"{afterOurs} !< {unobserved}");
    }

    [Fact]
    public void CraftsFinishingTogetherSellAtTheSameSupply()
    {
        // Two workshops finish a Potion at hour 4: both sell before either item reaches the market.
        var potion = Cat.Get(1)!;
        var (value, _, today) = Run(new[] { new[] { 1 }, new[] { 1 } });
        Assert.Equal(2, today[1]);
        Assert.Equal(2 * DayEvaluator.CraftValue(potion.BaseValue, 140, 0, Pop(1), 100, false), value, 9);
    }

    [Fact]
    public void MatchesANaiveHourByHourSimulationOnRandomDays()
    {
        var pool = ChainGenerator.TopChains(Cat, _ => true, id => Cat.Get(id)!.BaseValue, 400);
        var rng = new Random(20261027);
        for (var trial = 0; trial < 300; trial++)
        {
            var chains = Enumerable.Range(0, rng.Next(1, 5)).Select(_ => pool[rng.Next(pool.Count)]).ToArray();
            if (rng.Next(4) == 0)
                chains[0] = chains[0].Select(_ => pool[rng.Next(pool.Count)][0]).Take(rng.Next(1, 4)).ToArray(); // broken chains too
            var start = rng.Next(0, 50);
            var cap = new[] { 10, 15, 20, 25, 35, 45 }[rng.Next(6)];

            var (value, groove, _) = Run(chains, start, cap);
            Assert.Equal(Oracle(chains, start, cap), value, 6);
            Assert.True(groove <= cap);
        }
    }

    /// <summary>
    /// The game rules written as plainly as possible, for the all-Sufficient day: walk the hours; a craft finishing at
    /// hour h sells at the groove of every efficient craft started before h (capped) and at the supply left by the items
    /// that finished before h; efficient = different craft sharing a theme with the one before (2 items).
    /// </summary>
    private static double Oracle(int[][] chains, int startGroove, int cap)
    {
        var steps = new List<(int Start, int End, int Craft, bool Efficient)>();
        foreach (var chain in chains)
        {
            var t = 0;
            for (var i = 0; i < chain.Length; i++)
            {
                var craft = Cat.Get(chain[i])!;
                steps.Add((t, t + craft.Hours, craft.Id, i > 0 && craft.Id != chain[i - 1] && craft.Themes.Intersect(Cat.Get(chain[i - 1])!.Themes).Any()));
                t += craft.Hours;
            }
        }

        var sold = new Dictionary<int, int>();
        var total = 0.0;
        for (var hour = 1; hour <= 24; hour++)
        {
            var groove = Math.Min(cap, Math.Min(startGroove, cap) + steps.Count(s => s.Efficient && s.Start < hour));
            var finishing = steps.Where(s => s.End == hour).ToList();
            foreach (var s in finishing)
            {
                var weights = new double[5];
                SupplyMath.AddBucketWeights(weights, Supply.Sufficient, sold.GetValueOrDefault(s.Craft), 1.0);
                var sale = weights.Select((w, b) => w * DayEvaluator.CraftValue(Cat.Get(s.Craft)!.BaseValue, 140, groove, Pop(s.Craft), Cat.Rules.SupplyRatio[b], false)).Sum();
                total += sale * (s.Efficient ? 2 : 1);
            }

            foreach (var s in finishing)
                sold[s.Craft] = sold.GetValueOrDefault(s.Craft) + (s.Efficient ? 2 : 1);
        }

        return total;
    }
}
