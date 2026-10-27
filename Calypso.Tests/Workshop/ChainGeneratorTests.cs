using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

/// <summary><see cref="ChainGenerator"/> against a brute force written independently, on a catalog small enough to enumerate.</summary>
public class ChainGeneratorTests
{
    // Ten crafts on two themes (1 and 2; ids 8 and 9 carry both), hours 4/6/8 like the game.
    private static readonly CraftCatalog Small = new(
        new[]
        {
            Craft(1, 4, 30, 1), Craft(2, 4, 33, 1), Craft(3, 6, 47, 1), Craft(4, 8, 70, 1),
            Craft(5, 4, 29, 2), Craft(6, 6, 50, 2), Craft(7, 8, 73, 2), Craft(10, 4, 31, 2),
            Craft(8, 4, 35, 1, 2), Craft(9, 6, 52, 1, 2),
        },
        Array.Empty<PouchDef>(),
        Fixture.Catalog.Rules);

    private static CraftDef Craft(int id, int hours, int value, params int[] themes) =>
        new(id, 0, $"Craft {id}", themes, hours, value, 1, Array.Empty<MaterialNeed>());

    /// <summary>Every sequence of crafts in which each step is a different craft sharing a theme with the one before,
    /// within 24 hours and with no craft left that would still fit after it.</summary>
    private static List<int[]> BruteForce(CraftCatalog cat)
    {
        var all = new List<int[]>();
        void Walk(List<CraftDef> path, int hours)
        {
            var extended = false;
            foreach (var next in cat.Crafts)
            {
                var last = path[^1];
                if (next.Id == last.Id || !next.Themes.Intersect(last.Themes).Any() || hours + next.Hours > 24)
                    continue;
                extended = true;
                path.Add(next);
                Walk(path, hours + next.Hours);
                path.RemoveAt(path.Count - 1);
            }

            if (!extended)
                all.Add(path.Select(c => c.Id).ToArray());
        }

        foreach (var first in cat.Crafts)
            Walk(new List<CraftDef> { first }, first.Hours);
        return all;
    }

    private static double Score(int[] chain, Func<int, double> w) => w(chain[0]) + chain.Skip(1).Sum(id => 2 * w(id));

    public static IEnumerable<object[]> Weights() => new[]
    {
        new object[] { "base value" },
        new object[] { "seeded random" },
        new object[] { "some zero" },
    };

    private static Func<int, double> Weight(string kind)
    {
        var rng = new Random(7);
        var random = Small.Crafts.ToDictionary(c => c.Id, _ => (double)rng.Next(1, 200));
        return kind switch
        {
            "base value" => id => Small.Get(id)!.BaseValue,
            "seeded random" => id => random[id],
            _ => id => id % 3 == 0 ? 0 : Small.Get(id)!.BaseValue,
        };
    }

    [Theory]
    [MemberData(nameof(Weights))]
    public void TopChainsAreTheBestKOfABruteForceEnumeration(string kind)
    {
        var w = Weight(kind);
        var brute = BruteForce(Small);
        var bruteKeys = brute.Select(c => string.Join(",", c)).ToHashSet();

        foreach (var keep in new[] { 1, 5, 25, 200, brute.Count + 10 })
        {
            var top = ChainGenerator.TopChains(Small, _ => true, w, keep);
            var expected = brute.Select(c => Score(c, w)).OrderByDescending(v => v).Take(keep).ToList();

            Assert.Equal(expected, top.Select(c => Score(c, w)).ToList());     // same scores, best first
            Assert.All(top, c => Assert.Contains(string.Join(",", c), bruteKeys)); // and every one a real chain
            Assert.Equal(top.Count, top.Select(c => string.Join(",", c)).Distinct().Count());
        }
    }

    [Fact]
    public void GenerateListsExactlyTheBruteForceChains()
    {
        var brute = BruteForce(Small).Select(c => string.Join(",", c)).OrderBy(k => k).ToList();
        var generated = ChainGenerator.Generate(Small, _ => true).Select(c => string.Join(",", c)).OrderBy(k => k).ToList();
        Assert.Equal(brute, generated);
    }

    [Fact]
    public void EveryChainIsEfficientFitsTheCycleAndCannotBeExtended()
    {
        foreach (var chain in ChainGenerator.Generate(Small, _ => true))
        {
            var hours = chain.Sum(id => Small.Get(id)!.Hours);
            Assert.True(hours <= ChainGenerator.HoursPerCycle);
            for (var i = 1; i < chain.Length; i++)
                Assert.True(Small.Get(chain[i])!.ChainsAfter(Small.Get(chain[i - 1])!), string.Join(",", chain));
            Assert.DoesNotContain(Small.Crafts, c => c.ChainsAfter(Small.Get(chain[^1])!) && hours + c.Hours <= ChainGenerator.HoursPerCycle);
        }
    }

    [Fact]
    public void AChainCanEndBeforeHour24WhenNothingShorterFits()
    {
        // "Every chain fills 24 hours" is not a rule: crafts are 4, 6 or 8 hours, so 6 + 8 + 8 = 22 leaves 2 idle hours
        // that nothing fills. Such a chain is legal in game; it just never wins against a full one of equal crafts.
        var chains = ChainGenerator.Generate(Small, _ => true);
        Assert.Contains(chains, c => c.Sum(id => Small.Get(id)!.Hours) == 22);
        Assert.All(chains, c => Assert.True(c.Sum(id => Small.Get(id)!.Hours) >= 20));
    }

    [Fact]
    public void TheBestChainsOnTheRealCatalogFillTheWholeCycle()
    {
        var cat = Fixture.Catalog;
        var top = ChainGenerator.TopChains(cat, c => c.RankReq <= 20, id => cat.Get(id)!.BaseValue, 150);
        Assert.Equal(150, top.Count);
        Assert.All(top, c => Assert.Equal(ChainGenerator.HoursPerCycle, c.Sum(id => cat.Get(id)!.Hours)));
    }

    [Theory]
    [InlineData(1, 1, false)] // the same craft twice breaks the chain
    [InlineData(1, 2, true)]  // shares theme 1
    [InlineData(1, 5, false)] // no shared theme
    [InlineData(5, 8, true)]  // 8 carries both themes
    [InlineData(8, 1, true)]
    public void EfficiencyNeedsADifferentCraftSharingATheme(int previous, int next, bool efficient)
    {
        Assert.Equal(efficient, Small.Get(next)!.ChainsAfter(Small.Get(previous)!));
    }
}
