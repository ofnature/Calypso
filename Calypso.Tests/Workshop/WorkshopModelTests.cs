using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

public class WorkshopModelTests
{
    private static readonly int[] SupplyRatio = { 160, 130, 100, 80, 60 };

    [Fact]
    public void FixtureMatchesTheSheetFacts()
    {
        var cat = Fixture.Catalog;
        Assert.Equal(81, cat.Crafts.Count);
        Assert.Equal("Isleworks Potion", cat.Get(1)!.Name);
        Assert.Equal(new[] { 0, 100, 110, 120, 130, 140 }, cat.Rules.RankRatio);
        Assert.Equal(SupplyRatio, cat.Rules.SupplyRatio);
        Assert.Equal(45, cat.Rules.MaxGroove(5));

        // Pouch row 0 is a real item (Island Palm Leaf) — empty material slots are Amount 0, not row 0.
        Assert.Contains(cat.Get(1)!.Materials, m => m.PouchRow == 0 && m.Amount == 2);
        Assert.Equal(MaterialKind.GranaryRare, cat.KindOf(28));
        Assert.Equal(MaterialKind.Gatherable, cat.KindOf(0));
    }

    [Fact]
    public void CraftValueTruncatesLikeTheGame()
    {
        // 28 × 140% × 110% = 43.12 → 43; × 140% pop × 130% supply = 78.26 → 78; efficient → 156.
        Assert.Equal(156, DayEvaluator.CraftValue(28, 140, 10, 140, 130, efficient: true));
        Assert.Equal(28, DayEvaluator.CraftValue(28, 100, 0, 100, 100, efficient: false));
    }

    [Theory]
    [InlineData(Supply.Sufficient, 0, 100)]
    [InlineData(Supply.Sufficient, 4, 90)]       // halfway to Surplus (80)
    [InlineData(Supply.Sufficient, 8, 80)]       // one whole bucket
    [InlineData(Supply.Insufficient, 12, 90)]    // → Sufficient + 4
    [InlineData(Supply.Overflowing, 30, 60)]     // floor of the table
    public void ExpectedSupplyRatioBlendsTowardTheNextBucket(Supply bucket, int produced, double expected)
    {
        Assert.Equal(expected, SupplyMath.ExpectedRatio(SupplyRatio, bucket, produced), 6);
    }

    [Fact]
    public void PackedSupplyAndDemandUnpack()
    {
        Assert.Equal((Supply.Insufficient, DemandShift.Plummeting), SupplyMath.Unpack(0x14));
    }

    [Fact]
    public void PatternsNarrowAsDaysAreObserved()
    {
        var seen = new (Supply, DemandShift)?[7];
        Assert.Equal(12, PeakPatterns.Consistent(seen, _ => false).Count);

        // Day 1 Insufficient + Skyrocketing: only the strong C2 pattern fits (Ins/Non + Sky/Sky on days 0-1).
        seen[0] = (Supply.Insufficient, DemandShift.Skyrocketing);
        var left = PeakPatterns.Consistent(seen, _ => false);
        Assert.Equal(new[] { "C2S" }, left.Select(p => p.Name));
    }

    [Fact]
    public void OwnProductionMakesObservedSupplyIgnoredForMatching()
    {
        var seen = new (Supply, DemandShift)?[7];
        seen[0] = (Supply.Sufficient, DemandShift.None);
        seen[1] = (Supply.Overflowing, DemandShift.Increasing); // we flooded it on day 0

        Assert.Empty(PeakPatterns.Consistent(seen, _ => false));
        Assert.NotEmpty(PeakPatterns.Consistent(seen, day => day >= 1));
    }

    [Fact]
    public void ForecastCountsEarlierProductionOnlyOnUnobservedDays()
    {
        var observed = new byte[]?[7];
        observed[0] = Enumerable.Repeat((byte)0x22, 91).ToArray(); // everything Sufficient / None on day 0
        var f = new SupplyForecast(Fixture.Catalog, observed, new int[]?[7]);

        Assert.Equal(100, f.ExpectedRatio(1, 0, producedBeforeDay: 8, producedEarlierToday: 0), 6); // already inside the observation
        Assert.True(f.ExpectedRatio(1, 3, producedBeforeDay: 8, producedEarlierToday: 0) < f.ExpectedRatio(1, 3, 0, 0));
    }

    [Fact]
    public void ChainsAreEfficientAndFitTheCycle()
    {
        var cat = Fixture.Catalog;
        var chains = ChainGenerator.Generate(cat, c => c.RankReq <= 4);

        Assert.NotEmpty(chains);
        foreach (var chain in chains)
        {
            Assert.True(chain.Sum(id => cat.Get(id)!.Hours) <= ChainGenerator.HoursPerCycle);
            for (var i = 1; i < chain.Length; i++)
                Assert.True(cat.Get(chain[i])!.ChainsAfter(cat.Get(chain[i - 1])!));
        }
    }

    [Fact]
    public void TopChainsMatchExhaustiveSearchOnASmallSet()
    {
        var cat = Fixture.Catalog;
        bool Allowed(CraftDef c) => c.RankReq <= 4;
        double W(int id) => cat.Get(id)!.BaseValue;
        double Score(int[] chain) => W(chain[0]) + chain.Skip(1).Sum(id => 2 * W(id));

        var exhaustive = ChainGenerator.Generate(cat, Allowed).Select(Score).OrderByDescending(v => v).Take(20).ToList();
        var top = ChainGenerator.TopChains(cat, Allowed, W, 20).Select(Score).ToList();

        Assert.Equal(exhaustive, top);
    }

    [Fact]
    public void RankGatesChains()
    {
        var cat = Fixture.Catalog;
        var low = ChainGenerator.Generate(cat, c => c.RankReq <= 1);
        Assert.All(low.SelectMany(c => c), id => Assert.True(cat.Get(id)!.RankReq <= 1));
    }
}
