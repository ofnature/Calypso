using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

public class PlanModelTests
{
    [Fact]
    public void NormalizeStripsPrefixesAndPunctuation()
    {
        Assert.Equal("garnet rapier", OcScheduleParser.Normalize("Isleworks Garnet Rapier"));
        Assert.Equal("potion", OcScheduleParser.Normalize("  :Potion:  "));
    }

    [Fact]
    public void ParsesCyclesRestAndFourthWorkshop()
    {
        var cat = Fixture.Catalog;
        var potion = cat.Crafts.First(c => c.Name == "Isleworks Potion");
        var firesand = cat.Crafts.First(c => c.Name == "Isleworks Firesand"); // shares theme 8 with Potion

        const string text = """
            Season 12, Cycle 1
            Rest
            Cycle 2
            First 3 Workshops
            Potion → Firesand → Potion
            4th Workshop
            Isleworks Firesand, Potion
            Cycle 3: Rest
            """;

        var (plan, unmatched) = OcScheduleParser.Parse(text, cat, 7);

        Assert.Empty(unmatched);
        Assert.True(plan.Day(0)!.Rest);
        var d1 = plan.Day(1)!;
        Assert.False(d1.Rest);
        Assert.Equal(new[] { potion.Id, firesand.Id, potion.Id }, d1.Main);
        Assert.Equal(new[] { firesand.Id, potion.Id }, d1.Fourth);
        Assert.Equal(3, d1.MainCount);
        Assert.True(plan.Day(2)!.Rest);
    }

    [Fact]
    public void UnknownNamesAreReportedNotGuessed()
    {
        var (_, unmatched) = OcScheduleParser.Parse("Cycle 2\nPotion, Definitely Not An Item", Fixture.Catalog, 0);
        Assert.Equal(new[] { "Definitely Not An Item" }, unmatched);
    }

    [Fact]
    public void ItemsCountFirstCraftOnceAndEfficientCraftsTwice()
    {
        var cycle = new CyclePlan { Main = new List<int> { 1, 2, 1 }, MainCount = 3, Fourth = new List<int> { 2 } };
        var items = cycle.Items(4, Fixture.Catalog.MaxCraftId);

        Assert.Equal(3 * (1 + 2), items[1]); // three main workshops: potion first (1) + potion third (2)
        Assert.Equal(3 * 2 + 1, items[2]);   // main: efficient (2) ×3, fourth: first (1)
    }

    [Fact]
    public void OwnPlanEvaluatesToWhatTheSearchReported()
    {
        var input = new PlannerInput
        {
            Catalog = Fixture.Catalog,
            Forecast = Fixture.NoObservations(),
            Rank = 20,
            WorkshopTiers = new[] { 5, 5, 5, 4 },
            PopularityRow = 3,
            MaxGroove = 35,
            StartGroove = 5,
            FirstDay = 2,
            RestsTaken = 1,
            ProducedBefore = new int[Fixture.Catalog.MaxCraftId + 1],
            Materials = Fixture.AllMaterials(),
        };

        var result = WeekPlanner.Plan(input);
        var plan = WeekPlan.FromResult(result, 1, DateTime.UtcNow);

        Assert.Equal(result.ExpectedValue, WeekPlanner.Evaluate(input, plan), 3);
    }

    [Fact]
    public void ReservationSetsConstructionCostsAside()
    {
        var onHand = new Dictionary<int, int> { [9] = 20, [14] = 5 };
        var left = MaterialReservation.Available(onHand, new[]
        {
            new Construction("Workshop II", 6, new[] { new MaterialNeed(9, 15), new MaterialNeed(14, 15) }),
        });

        Assert.Equal(5, left[9]);
        Assert.Equal(0, left[14]);
        Assert.Equal(20, onHand[9]);
    }

    [Fact]
    public void WeekRollClearsEverything()
    {
        var week = new WorkshopWeek();
        Assert.True(week.Roll(5));
        week.Observed[0] = new byte[91];
        week.Produced[0] = new int[91];
        Assert.False(week.Roll(5));
        Assert.True(week.Roll(6));
        Assert.Empty(week.Observed);
        Assert.Empty(week.Produced);
    }
}
