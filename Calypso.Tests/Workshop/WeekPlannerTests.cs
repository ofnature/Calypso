using System.Diagnostics;
using Calypso.Core.Workshop;
using Xunit.Abstractions;

namespace Calypso.Tests.Workshop;

public class WeekPlannerTests
{
    private readonly ITestOutputHelper _out;

    public WeekPlannerTests(ITestOutputHelper output) => _out = output;

    private static PlannerInput Input(int firstDay = 1, int rank = 20, IReadOnlyDictionary<int, int>? materials = null,
        IReadOnlyList<FavorRequirement>? favors = null, int restsTaken = 1) => new()
    {
        Catalog = Fixture.Catalog,
        Forecast = Fixture.NoObservations(),
        Rank = rank,
        WorkshopTiers = new[] { 5, 5, 5, 5 },
        PopularityRow = 1,
        MaxGroove = 45,
        StartGroove = 0,
        FirstDay = firstDay,
        RestsTaken = restsTaken,
        ProducedBefore = new int[Fixture.Catalog.MaxCraftId + 1],
        Materials = materials ?? Fixture.AllMaterials(),
        Favors = favors ?? Array.Empty<FavorRequirement>(),
    };

    [Fact]
    public void PlansEveryRemainingDayWithExactlyTheRestDaysLeft()
    {
        var result = WeekPlanner.Plan(Input(firstDay: 1, restsTaken: 1));

        Assert.Equal(Enumerable.Range(1, 6), result.Days.Select(d => d.Day));
        Assert.Equal(1, result.Days.Count(d => d.Rest)); // 2 per week, one already taken
        Assert.True(result.ExpectedValue > 0);
        Assert.All(result.Days.Where(d => !d.Rest), d => Assert.NotNull(d.Main));
    }

    [Fact]
    public void FinishesWithinTheTimeBudgetOnTheRealCatalog()
    {
        var watch = Stopwatch.StartNew();
        var result = WeekPlanner.Plan(Input(firstDay: 0, restsTaken: 0));
        watch.Stop();

        _out.WriteLine($"plan {result.ExpectedValue:0} cowries, {watch.ElapsedMilliseconds} ms, warnings: {string.Join("; ", result.Warnings)}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(2, result.Days.Count(d => d.Rest));
    }

    [Fact]
    public void NeverOverdrawsAHardCappedMaterial()
    {
        // No granary rares, produce or leavings at all; plenty of gatherables.
        var cat = Fixture.Catalog;
        var mats = cat.Pouch.Values.ToDictionary(p => p.Row, p => p.Kind == MaterialKind.Gatherable ? 999 : 0);
        var result = WeekPlanner.Plan(Input(materials: mats));

        var used = result.Days.Where(d => !d.Rest).SelectMany(d => d.Main!.Concat(d.Fourth ?? Array.Empty<int>()));
        Assert.All(used, id => Assert.All(cat.Get(id)!.Materials, m => Assert.Equal(MaterialKind.Gatherable, cat.KindOf(m.PouchRow))));
    }

    [Fact]
    public void GatherableShortfallIsReportedAsDemand()
    {
        var mats = Fixture.Catalog.Pouch.Keys.ToDictionary(k => k, _ => 0);
        var result = WeekPlanner.Plan(Input(materials: mats));

        Assert.NotEmpty(result.GatherDemand);
        Assert.All(result.GatherDemand.Keys, row => Assert.Equal(MaterialKind.Gatherable, Fixture.Catalog.KindOf(row)));
    }

    [Fact]
    public void FavorsAreFilledWhenPossible()
    {
        // A cheap rank-1 craft that the value search would not naturally pick much of.
        var favor = new FavorRequirement(1, 8);
        var result = WeekPlanner.Plan(Input(favors: new[] { favor }));

        var made = result.Days.Where(d => !d.Rest).Sum(d =>
            Count(d.Main!, 1) * d.MainCount + (d.Fourth == null ? 0 : Count(d.Fourth, 1)));
        Assert.True(result.FavorsMet);
        Assert.True(made >= 1, $"made {made} chain slots of craft 1");
    }

    [Fact]
    public void LowRankOnlyUsesUnlockedCrafts()
    {
        var result = WeekPlanner.Plan(Input(rank: 3));
        Assert.All(result.Days.Where(d => !d.Rest).SelectMany(d => d.Main!), id => Assert.True(Fixture.Catalog.Get(id)!.RankReq <= 3));
    }

    [Fact]
    public void LastDayOnlyWithARestLeftMustRest()
    {
        var result = WeekPlanner.Plan(Input(firstDay: 6, restsTaken: 1));
        Assert.Single(result.Days);
        Assert.True(result.Days[0].Rest);
    }

    private static int Count(int[] chain, int id) => chain.Count(c => c == id);
}
