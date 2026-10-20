using Calypso.Core.Market;

namespace Calypso.Tests.Core;

public class MarketPlannerTests
{
    private const uint Seafarers = 37549, Islanders = 37550;

    private static ExportItem Item(uint shopRow, int held, int category = 0, int each = 3, bool islanders = true, int pouch = -1) =>
        new(shopRow, pouch < 0 ? (int)shopRow : pouch, 37000 + shopRow, $"item {shopRow}", category, each, islanders, held, 999);

    [Fact]
    public void SellsOnlyItemsAtTheCapDownToKeep()
    {
        var plan = ExportPlanner.Plan(new[] { Item(1, 999), Item(2, 998), Item(3, 999) }, keep: 100, _ => 0, (0, 0), (10000, 10000));

        var batch = Assert.Single(plan.Batches);
        Assert.Equal(100, batch.Keep);
        Assert.Equal(new[] { (1u, 899), (3u, 899) }, batch.Items);
        Assert.Equal(2 * 899 * 3, plan.IslandersGained);
    }

    [Fact]
    public void ReservationsAboveKeepAreHonouredInTheirOwnBatch()
    {
        var plan = ExportPlanner.Plan(new[] { Item(1, 999, pouch: 7), Item(2, 999) }, 100, row => row == 7 ? 250 : 0, (0, 0), (10000, 10000));

        Assert.Equal(2, plan.Batches.Count);
        Assert.Contains(plan.Batches, b => b.Keep == 250 && b.Items.Single() == (1u, 749));
        Assert.Contains(plan.Batches, b => b.Keep == 100 && b.Items.Single() == (2u, 899));
    }

    [Fact]
    public void BatchesSplitByCategoryAndSkipCowrieOverflow()
    {
        var items = new[] { Item(1, 999, category: 0), Item(2, 999, category: 2, each: 5, islanders: false), Item(3, 999, category: 0, each: 50) };
        var plan = ExportPlanner.Plan(items, 100, _ => 0, (0, 9000), (10000, 10000));

        // item 1: 899 × 3 = 2697 Islander's → 9000 + 2697 overflows 10000 — skipped; item 3 likewise.
        Assert.Equal(2, plan.Skipped.Count);
        var batch = Assert.Single(plan.Batches);
        Assert.Equal(2, batch.Category);
        Assert.Equal(899 * 5, plan.SeafarersGained);
    }

    [Fact]
    public void BestValueIsGilPerCowrieAmongAffordableItemsThatSell()
    {
        var offers = new[]
        {
            new ShopOffer(1, 0, 33938, "Craftsman's Competence Materia X", 1, Islanders, 3500),
            new ShopOffer(1, 1, 21800, "Glamour Prism", 1, Islanders, 150),
            new ShopOffer(1, 2, 39425, "Lemonade Stand", 1, Islanders, 2500),
        };
        (long, double)? Price(uint id) => id switch
        {
            33938 => (70000, 5.0),   // 20 gil / cowrie
            21800 => (2400, 30.0),   // 16 gil / cowrie
            39425 => (500000, 0.0),  // 200 gil / cowrie, but nobody buys it
            _ => null,
        };

        var ranked = ShoppingPlanner.Rank(offers, Price, cur => cur == Islanders ? 4000 : 0);

        Assert.Equal(33938u, ranked[0].Offer.ItemId);
        Assert.Equal(20.0, ranked[0].GilPerCowrie, 3);
        Assert.Equal(1, ranked[0].Affordable);
        Assert.Equal(39425u, ranked[^1].Offer.ItemId);
        Assert.Equal(33938u, ShoppingPlanner.Best(ranked)!.Offer.ItemId);
    }

    [Fact]
    public void NothingIsBoughtWithACurrencyThatMayNotBeSpent()
    {
        var offers = new[] { new ShopOffer(1, 0, 33938, "Materia X", 1, Seafarers, 1500) };
        var ranked = ShoppingPlanner.Rank(offers, _ => (70000, 5.0), _ => 0);
        Assert.Null(ShoppingPlanner.Best(ranked));
    }
}
