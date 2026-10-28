using Calypso.Core.Farm;
using Calypso.Core.Pasture;
using Calypso.Core.Workshop;
using Calypso.Tests.Granary;
using Calypso.Tests.Pasture;
using Calypso.Tests.Workshop;

namespace Calypso.Tests.Farm;

/// <summary>The cropland and pasture fixtures against each other and against the craftworks and granary fixtures.</summary>
public class FarmFixtureTests
{
    [Fact]
    public void TwentyCropsEachWithItsOwnSeedAndProduce()
    {
        var crops = FarmFixture.Crops;

        Assert.Equal(20, crops.Count);
        Assert.Equal(Enumerable.Range(1, 20), crops.Select(c => c.Id));
        Assert.Equal(20, crops.Select(c => c.SeedPouchRow).Distinct().Count());
        Assert.Equal(20, crops.Select(c => c.ProducePouchRow).Distinct().Count());
        Assert.Equal(20, crops.Select(c => c.SeedItemId).Distinct().Count());
    }

    [Fact]
    public void TheProduceIsExactlyTheWorkshopsProduceAndTheNamesAgree()
    {
        var produce = Fixture.Catalog.Pouch.Values.Where(p => p.Kind == MaterialKind.Produce).ToDictionary(p => p.Row);

        Assert.Equal(produce.Keys.OrderBy(r => r), FarmFixture.Crops.Select(c => c.ProducePouchRow).OrderBy(r => r));
        Assert.All(FarmFixture.Crops, c =>
        {
            Assert.Equal(produce[c.ProducePouchRow].ItemId, c.ProduceItemId);
            Assert.Equal(produce[c.ProducePouchRow].Name, c.Name);
        });
        Assert.Equal(produce.Keys.OrderBy(r => r), FarmNeeds.Rows(Fixture.Catalog).Where(produce.ContainsKey));
    }

    [Fact]
    public void SeedsAreGardeningStartersAndTheGranaryBringsSomeOfThem()
    {
        var seeds = FarmFixture.Crops.Select(c => c.SeedPouchRow).ToHashSet();
        var catalogSeeds = Fixture.Catalog.Pouch.Values.Where(p => p.Name.EndsWith(" Seeds") || p.Name.EndsWith(" Set")).Select(p => p.Row).ToHashSet();

        Assert.Equal(catalogSeeds, seeds);
        // Popoto sets and parsnip seeds come home from Meandering Meadows (granary.json).
        Assert.Contains(32, GranaryFixture.Areas[0].NormalPouchRows);
        Assert.Contains(40, GranaryFixture.Areas[0].NormalPouchRows);
        Assert.Subset(seeds, new HashSet<int> { 32, 40 });
    }

    [Fact]
    public void FortyThreeAnimalsWithTwoLeavingsEach()
    {
        var animals = PastureFixture.Animals;
        var leavings = Fixture.Catalog.Pouch.Values.Where(p => p.Kind == MaterialKind.Leavings).Select(p => p.Row).OrderBy(r => r);

        Assert.Equal(43, animals.Count);
        Assert.Equal(Enumerable.Range(1, 43), animals.Select(a => a.Row));
        Assert.Equal(43, animals.Select(a => a.BNpcBase).Distinct().Count());
        Assert.All(animals, a =>
        {
            Assert.Equal(2, a.Leavings.Count);
            Assert.NotEqual(a.Leavings[0], a.Leavings[1]);
            Assert.InRange(a.Size, 1, 3);
            Assert.True(Enum.IsDefined(a.Rarity));
        });
        Assert.Equal(leavings, PastureFixture.Leavings);
        Assert.Equal(leavings, animals.SelectMany(a => a.Leavings).Distinct().OrderBy(r => r));
        Assert.Equal(new[] { 14, 14, 15 }, animals.GroupBy(a => a.Rarity).OrderBy(g => g.Key).Select(g => g.Count()));
    }

    [Fact]
    public void SlotCountsMatchTheRankSheet()
    {
        var (cropland, pasture) = FarmFixture.RankUnknown7();

        Assert.Equal(cropland, new FarmRules().SlotsByLevel);
        Assert.Equal(pasture, new PastureRules().CapacityByLevel);
    }
}
