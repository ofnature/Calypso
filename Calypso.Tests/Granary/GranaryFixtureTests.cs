using Calypso.Core.Granary;
using Calypso.Core.Workshop;
using Calypso.Tests.Workshop;

namespace Calypso.Tests.Granary;

public class GranaryFixtureTests
{
    /// <summary>The table from the 2026-10-27 dump, typed out by hand: area → (rare, normal resources in sheet order).</summary>
    private static readonly (string Rare, int RareRow, (string Name, int Row)[] Normal)[] Expected =
    {
        ("Island Alyssum", 27, new[] { ("Island Apple", 1), ("Island Stone", 3), ("Islewort", 7), ("Island Vine", 11), ("Island Log", 9), ("Island Cotton Boll", 17), ("Island Popoto Set", 32), ("Island Parsnip Seeds", 40), ("Island Resin", 67), ("Island Beehive Chip", 69) }),
        ("Raw Island Garnet", 28, new[] { ("Island Palm Leaf", 0), ("Island Stone", 3), ("Islewort", 7), ("Island Sand", 8), ("Island Palm Log", 10), ("Island Sugarcane", 16), ("Island Clay", 19), ("Island Tinsand", 20), ("Island Coconut", 68) }),
        ("Island Spruce Log", 29, new[] { ("Island Branch", 2), ("Island Pumpkin Seeds", 35), ("Island Cabbage Seeds", 33), ("Islewort", 7), ("Island Sap", 12), ("Island Vine", 11), ("Island Apple", 1), ("Island Log", 9), ("Island Hemp", 18), ("Island Resin", 67), ("Island Beehive Chip", 69), ("Island Wood Opal", 70) }),
        ("Island Hammerhead", 30, new[] { ("Island Palm Leaf", 0), ("Island Sand", 8), ("Island Clam", 4), ("Island Laver", 5), ("Island Coral", 6), ("Island Palm Log", 10), ("Island Rock Salt", 15), ("Islefish", 24), ("Island Squid", 25), ("Island Jellyfish", 26), ("Island Coconut", 68) }),
        ("Island Silver Ore", 31, new[] { ("Island Branch", 2), ("Island Stone", 3), ("Island Sand", 8), ("Island Copper Ore", 13), ("Island Limestone", 14), ("Island Iron Ore", 21), ("Island Quartz", 22), ("Island Leucogranite", 23), ("Multicolored Isleblooms", 71), ("Island Marble", 79), ("Island Mythril Ore", 80), ("Island Durium Sand", 92) }),
        ("Island Cave Shrimp", 83, new[] { ("Island Stone", 3), ("Island Sand", 8), ("Island Coal", 76), ("Island Shale", 77), ("Island Glimshroom", 78), ("Island Effervescent Water", 81), ("Island Spectrine", 82), ("Island Yellow Copper Ore", 93), ("Island Gold Ore", 94), ("Island Hawk's Eye Sand", 95), ("Island Crystal Formation", 96) }),
    };

    [Fact]
    public void LoadsSixAreasInOrder()
    {
        Assert.Equal(Enumerable.Range(0, 6), GranaryFixture.Areas.Select(a => a.Id));
    }

    [Fact]
    public void EveryAreaBringsTheRareFromTheDump()
    {
        for (var i = 0; i < Expected.Length; i++)
        {
            var area = GranaryFixture.Areas[i];
            Assert.Equal(Expected[i].RareRow, area.RarePouchRow);
            Assert.Equal(Expected[i].Rare, GranaryFixture.Names[area.RarePouchRow]);
        }
    }

    [Fact]
    public void EveryAreaBringsTheNormalResourcesFromTheDump()
    {
        for (var i = 0; i < Expected.Length; i++)
        {
            var area = GranaryFixture.Areas[i];
            Assert.Equal(Expected[i].Normal.Select(n => n.Row), area.NormalPouchRows);
            Assert.Equal(Expected[i].Normal.Select(n => n.Name), area.NormalPouchRows.Select(r => GranaryFixture.Names[r]));
        }
    }

    [Fact]
    public void TheRaresAreExactlyTheWorkshopsHardCappedGranaryRares()
    {
        var cat = Fixture.Catalog;
        var rares = cat.Pouch.Values.Where(p => p.Kind == MaterialKind.GranaryRare).Select(p => p.Row).OrderBy(r => r);

        Assert.Equal(rares, GranaryFixture.Areas.Select(a => a.RarePouchRow).OrderBy(r => r));

        // The normal resources are gatherables — except four seeds (MJIItemCategory 3), which the meadows and woods
        // also bring.
        var normal = GranaryFixture.Areas.SelectMany(a => a.NormalPouchRows).Distinct().ToList();
        Assert.Equal(new[] { 32, 33, 35, 40 }, normal.Where(r => cat.KindOf(r) != MaterialKind.Gatherable).OrderBy(r => r));
        Assert.All(normal.Where(r => cat.KindOf(r) != MaterialKind.Gatherable), r => Assert.Contains("Seeds", GranaryFixture.Names[r].Replace(" Set", " Seeds")));
    }

    [Fact]
    public void NamesAgreeWithTheCraftworksDump()
    {
        foreach (var (row, name) in GranaryFixture.Names)
            Assert.Equal(Fixture.Catalog.Pouch[row].Name, name);
    }

    [Fact]
    public void AreasHaveTheirKnownNames()
    {
        Assert.Equal(new[] { "Meandering Meadows", "Fatal Falls", "Wild Woods", "Bending Beaches", "Mossy Mountains", "Crumbling Caverns" },
            GranaryFixture.Areas.Select(a => a.Name));
    }

    [Fact]
    public void AreasOpenByRankAndTheCavernsNeedTheirOwnUnlock()
    {
        var rules = new GranaryRules();

        Assert.Empty(rules.AreasForRank(5, cavernsUnlocked: true));
        Assert.Equal(Enumerable.Range(0, 5), rules.AreasForRank(6, cavernsUnlocked: false));
        Assert.Equal(Enumerable.Range(0, 5), rules.AreasForRank(11, cavernsUnlocked: true));
        Assert.Equal(Enumerable.Range(0, 6), rules.AreasForRank(12, cavernsUnlocked: true));
    }
}
