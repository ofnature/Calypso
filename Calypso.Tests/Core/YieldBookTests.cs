using Calypso.Core.Gathering;

namespace Calypso.Tests.Core;

public class YieldBookTests
{
    private const string NoTools = "";
    private const string Scythe = "4";
    private const uint Branch = 37553, Vine = 37562, Sugarcane = 37588, Stone = 37554, PalmLeaf = 37551;

    [Fact]
    public void LearnsRatesPerVisitAndSignature()
    {
        var book = new YieldBook();
        book.Record("Tualong Tree", NoTools, new Dictionary<uint, int> { [Branch] = 2, [PalmLeaf] = 1 });
        book.Record("Tualong Tree", NoTools, new Dictionary<uint, int> { [Branch] = 2 });

        Assert.Equal(2.0, book.Rate("Tualong Tree", NoTools, new[] { Branch }));
        Assert.Equal(2.5, book.Rate("TUALONG TREE", NoTools, new[] { Branch, PalmLeaf })); // names are case-insensitive
        Assert.Equal(0, book.Rate("Tualong Tree", Scythe, new[] { Branch }));
    }

    [Fact]
    public void PrefersProducersThenUnknownsThenDuds()
    {
        var book = new YieldBook();
        book.Record("Tualong Tree", NoTools, new Dictionary<uint, int> { [Branch] = 2 });
        book.Record("Bluish Rock", NoTools, new Dictionary<uint, int> { [Stone] = 3 });
        var wanted = new[] { Branch };

        Assert.Equal(0, book.Priority("Tualong Tree", NoTools, wanted).Tier);
        Assert.Equal(1, book.Priority("Large Shell", NoTools, wanted).Tier);
        Assert.Equal(2, book.Priority("Bluish Rock", NoTools, wanted).Tier);
    }

    [Fact]
    public void UntriedNodesRankWithProducersWhileSomeWantedItemHasNoSource()
    {
        // The 2026-10-17 run: branch has a known source (Tualong Tree), stone and palm leaf don't.
        var book = new YieldBook();
        book.Record("Tualong Tree", NoTools, new Dictionary<uint, int> { [Branch] = 1 });
        var wanted = new[] { Branch, Stone, PalmLeaf };

        Assert.Equal(new[] { Stone, PalmLeaf }, book.Unsourced(wanted));
        Assert.Equal(0, book.Priority("Tualong Tree", NoTools, wanted).Tier);
        Assert.Equal(0, book.Priority("Sugarcane", NoTools, wanted).Tier);
    }

    [Fact]
    public void ANewToolMakesANodeUntriedAgainForWhatTheToolUnlocks()
    {
        // Without the scythe the Sugarcane node gave Island Vine (probe 2026-10-17).
        var book = new YieldBook();
        book.Record("Sugarcane", NoTools, new Dictionary<uint, int> { [Vine] = 1 });

        // Holding the scythe and wanting Island Sugarcane: nothing ever gave it, the node is untried with these tools → explore.
        Assert.Equal(0, book.Priority("Sugarcane", Scythe, new[] { Sugarcane }).Tier);
        Assert.False(book.Known("Sugarcane", Scythe));

        // Old drops stay a hint under new tools — a tool never takes a drop away.
        Assert.Equal((0, 1.0), book.Priority("Sugarcane", Scythe, new[] { Vine }));

        book.Record("Sugarcane", Scythe, new Dictionary<uint, int> { [Vine] = 1, [Sugarcane] = 1 });
        Assert.Equal(1.0, book.Rate("Sugarcane", Scythe, new[] { Sugarcane }));
    }

    [Fact]
    public void LegacyDataMovesIntoTheNoToolsSignature()
    {
        var book = new YieldBook
        {
            Visits = new(StringComparer.OrdinalIgnoreCase) { ["Tualong Tree"] = 6 },
            Yields = new(StringComparer.OrdinalIgnoreCase) { ["Tualong Tree"] = new() { [Branch] = 6 } },
        };

        Assert.True(book.MigrateLegacy());
        Assert.Empty(book.Visits);
        Assert.Equal(1.0, book.Rate("Tualong Tree", NoTools, new[] { Branch }));
        Assert.False(book.MigrateLegacy());
    }

    [Fact]
    public void SignatureIsOrderIndependent()
    {
        Assert.Equal("1,2,4", YieldBook.Signature(new[] { 4, 1, 2, 2 }));
        Assert.Equal(string.Empty, YieldBook.Signature(Array.Empty<int>()));
    }

    [Fact]
    public void EmptyGatherStillCountsAsAVisit()
    {
        var book = new YieldBook();
        book.Record("Large Shell", NoTools, new Dictionary<uint, int>());
        Assert.True(book.Known("Large Shell", NoTools));
        Assert.Equal(2, book.Priority("Large Shell", NoTools, new uint[] { 1 }).Tier);
    }
}
