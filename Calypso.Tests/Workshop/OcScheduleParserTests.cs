using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

/// <summary>
/// <see cref="OcScheduleParser"/> on the Overseas Casuals' published post format. Sources (the posts as quoted on
/// x.com; the agenda in older posts was an image, so only the text around it is real):
/// - Season 41, Cycle 7, posted 2023-06-02 by PolarParrot - Overseas Casuals Recommendations (@WorkshopParrot),
///   https://x.com/WorkshopParrot — the 6.4 four-workshop format:
///   "Season 41, Cycle 7: Workshops #1-3: ∟ Rope (4h) - Cavalier's Hat (6h) - Bed (8h) - Cavalier's Hat (6h)
///   Workshop #4: ∟ Popoto Salad (4h) - Runner Bean Saute (4h)" (the fourth workshop's line as quoted, possibly cut).
/// - Season 22, Cycle 7, https://twitter.com/WorkshopParrot/status/1616354058746355712 (January 2023).
/// </summary>
public class OcScheduleParserTests
{
    private static readonly CraftCatalog Cat = Fixture.Catalog;

    private static int Id(string name) => Cat.Crafts.Single(c => c.Name == name).Id;

    private static readonly int[] Main41 =
        { Id("Isleworks Rope"), Id("Isleworks Cavalier's Hat"), Id("Isleworks Bed"), Id("Isleworks Cavalier's Hat") };

    private static readonly int[] Fourth41 = { Id("Isleworks Popoto Salad"), Id("Isleworks Runner Bean Saute") };

    private static void AssertSeason41(string text)
    {
        var (plan, unmatched) = OcScheduleParser.Parse(text, Cat, 41);

        Assert.Empty(unmatched);
        var day = Assert.Single(plan.Cycles);
        Assert.Equal(6, day.Day);
        Assert.False(day.Rest);
        Assert.Equal(Main41, day.Main);
        Assert.Equal(3, day.MainCount);
        Assert.Equal(Fourth41, day.Fourth);
    }

    [Fact]
    public void ReadsTheFourWorkshopPostLineByLine()
    {
        AssertSeason41("""
            Season 41, Cycle 7:
            Workshops #1-3:
            ∟ Rope (4h) - Cavalier's Hat (6h) - Bed (8h) - Cavalier's Hat (6h)
            Workshop #4:
            ∟ Popoto Salad (4h) - Runner Bean Saute (4h)
            """);
    }

    [Fact]
    public void ReadsTheFourWorkshopPostFlattenedToOneLine()
    {
        // How the post reads once copied out of an embed or a search result: no line breaks at all.
        AssertSeason41("Season 41, Cycle 7: Workshops #1-3: ∟ Rope (4h) - Cavalier's Hat (6h) - Bed (8h) - Cavalier's Hat (6h) "
                       + "Workshop #4: ∟ Popoto Salad (4h) - Runner Bean Saute (4h)");
    }

    [Fact]
    public void ToleratesTypographicQuotesDashesAndAccents()
    {
        AssertSeason41("""
            Season 41, Cycle 7:
            Workshops #1–3:
            ∟ Rope (4h) – Cavalier’s Hat (6h) – Bed (8h) – Cavalier’s Hat (6h)
            Workshop #4:
            ∟ Popoto Salad (4h) – Runner Bean Sauté (4h)
            """);
    }

    [Fact]
    public void ReadsTheRestAdviceInAPostAndInventsNothing()
    {
        // Verbatim post text (its agenda was an image): the only fact in it is "rest C1".
        const string text = """
            Season 22, Cycle 7:
            Season 22's Total: (Rank 10) 24,426; (Rank 11) 25,107
            As always: rest C1, and remember we're no longer covering Rank 10 recs! If you need lower-rank help, check the discord: https://t.co/bCbIXcPAWZ! #FF14 #FFXIV #FinalFantasyXIV #IslandSanctuary https://t.co/HXnmEzWYFA
            """;

        foreach (var paste in new[] { text, text.Replace("\n", " ") })
        {
            var (plan, _) = OcScheduleParser.Parse(paste, Cat, 22);

            // Day 0 rests; cycle 7 had no agenda in the text, so the plan does not claim anything about it.
            var day = Assert.Single(plan.Cycles);
            Assert.Equal(0, day.Day);
            Assert.True(day.Rest);
        }
    }

    [Fact]
    public void ACraftNameInsideAnotherWordIsNotAMatch()
    {
        // "Bed" and "Pie" (Islefish Pie) are whole craft names; "embedded" and "copied" are not those crafts.
        var (plan, unmatched) = OcScheduleParser.Parse("Cycle 2\nembedded, copied", Cat, 0);
        Assert.Empty(plan.Cycles);
        Assert.Equal(new[] { "embedded", "copied" }, unmatched);
    }
}
