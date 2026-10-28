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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 0)] // four days left, both rests still owed
    [InlineData(4, 2)] // no rest left: every day works
    [InlineData(5, 1)]
    public void RestDaysCarryNoWorkAndWorkDaysDo(int firstDay, int restsTaken)
    {
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var result = WeekPlanner.Plan(Scenarios.Week(seed, firstDay, restsTaken));

            Assert.Equal(Enumerable.Range(firstDay, 7 - firstDay), result.Days.Select(d => d.Day));
            Assert.Equal(Math.Min(2 - restsTaken, 7 - firstDay), result.Days.Count(d => d.Rest));
            Assert.All(result.Days.Where(d => d.Rest), d =>
            {
                Assert.Null(d.Main);
                Assert.Null(d.Fourth);
                Assert.Equal(0, d.Value);
            });
            Assert.All(result.Days.Where(d => !d.Rest), d => Assert.True(d.Main is { Length: > 0 } && d.Value > 0));
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void NeverUsesMoreOfAHardCappedMaterialThanIsOnHand(int seed)
    {
        // A few of each granary rare, produce and leavings; gatherables are free.
        var cat = Fixture.Catalog;
        var rng = new Random(seed);
        var mats = cat.Pouch.Values.ToDictionary(p => p.Row, p => p.Kind == MaterialKind.Gatherable ? 999 : rng.Next(0, 13));
        var result = WeekPlanner.Plan(Scenarios.Week(seed, materials: mats));

        var used = new Dictionary<int, int>();
        foreach (var day in result.Days)
        {
            var items = Scenarios.Items(day, 4);
            for (var c = 0; c < items.Length; c++)
            {
                foreach (var m in items[c] == 0 ? Array.Empty<MaterialNeed>() : cat.Get(c)!.Materials)
                    used[m.PouchRow] = used.GetValueOrDefault(m.PouchRow) + m.Amount * items[c];
            }
        }

        // Before the fix the ten best chains per state all overdrew something, every work day was rejected and the
        // plan came back empty (no days, no warning).
        Assert.Equal(6, result.Days.Count);
        Assert.NotEmpty(used);
        Assert.All(used.Where(u => cat.KindOf(u.Key) != MaterialKind.Gatherable),
            u => Assert.True(u.Value <= mats[u.Key], $"{cat.Pouch[u.Key].Name}: {u.Value} > {mats[u.Key]}"));
    }

    [Fact]
    public void FavorsAreFilledInFullWhenAPlanCanFillThem()
    {
        // Three favors as the game hands them out, on crafts the value search alone would not pick much of.
        var favors = new[] { new FavorRequirement(1, 8), new FavorRequirement(4, 6), new FavorRequirement(12, 6) };
        var withFavors = WeekPlanner.Plan(Scenarios.Week(21, favors: favors));
        var without = WeekPlanner.Plan(Scenarios.Week(21));

        Assert.True(withFavors.FavorsMet);
        foreach (var f in favors)
        {
            var made = withFavors.Days.Sum(d => Scenarios.Items(d, 4)[f.CraftId]);
            Assert.True(made >= f.Required, $"craft {f.CraftId}: made {made} of {f.Required}");
        }

        Assert.True(withFavors.ExpectedValue <= without.ExpectedValue + 1e-6);
    }

    [Fact]
    public void AFavorNoPlanCanFillIsReportedNotForced()
    {
        var locked = Fixture.Catalog.Crafts.First(c => c.RankReq > 5);
        var result = WeekPlanner.Plan(Scenarios.Week(22, rank: 5, favors: new[] { new FavorRequirement(locked.Id, 4) }));

        Assert.False(result.FavorsMet);
        Assert.Contains(result.Warnings, w => w.Contains("favor"));
        Assert.True(result.ExpectedValue > 0);
        Assert.All(result.Days.Where(d => !d.Rest).SelectMany(d => d.Main!), id => Assert.True(Fixture.Catalog.Get(id)!.RankReq <= 5));
    }

    [Fact]
    public void TheSameInputGivesTheSamePlan()
    {
        var a = WeekPlanner.Plan(Scenarios.Week(31));
        var b = WeekPlanner.Plan(Scenarios.Week(31));

        Assert.Equal(a.ExpectedValue, b.ExpectedValue);
        Assert.Equal(a.Days.Count, b.Days.Count);
        for (var i = 0; i < a.Days.Count; i++)
        {
            Assert.Equal(a.Days[i].Rest, b.Days[i].Rest);
            Assert.Equal(a.Days[i].Main, b.Days[i].Main);
            Assert.Equal(a.Days[i].MainCount, b.Days[i].MainCount);
            Assert.Equal(a.Days[i].Fourth, b.Days[i].Fourth);
        }
    }

    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    public void TheReportedValueIsWhatThePlanEvaluatesTo(int seed)
    {
        var input = Scenarios.Week(seed);
        var result = WeekPlanner.Plan(input);
        Assert.Equal(result.ExpectedValue, WeekPlanner.Evaluate(input, WeekPlan.FromResult(result, 1, DateTime.UtcNow)), 6);
    }

    [Fact]
    public void StaysInsideItsTimeBudget()
    {
        // Warm up once (JIT, fixture), then time the shapes the daily loop runs.
        WeekPlanner.Plan(Scenarios.Week(50));
        var favors = new[] { new FavorRequirement(1, 8), new FavorRequirement(4, 6), new FavorRequirement(12, 6) };
        var cases = new (string Name, PlannerInput Input)[]
        {
            ("full week from day 0, nothing observed", Input(firstDay: 0, restsTaken: 0)),
            ("days 1-6, day 0 observed", Scenarios.Week(51)),
            ("days 1-6 with three favors", Scenarios.Week(52, favors: favors)),
            ("days 3-6", Scenarios.Week(53, firstDay: 3, restsTaken: 1)),
        };

        foreach (var (name, input) in cases)
        {
            var watch = Stopwatch.StartNew();
            var result = WeekPlanner.Plan(input);
            watch.Stop();
            _out.WriteLine($"{name}: {watch.ElapsedMilliseconds} ms, {result.ExpectedValue:0} cowries");

            Assert.True(watch.Elapsed < input.Limits.TimeBudget, $"{name} took {watch.ElapsedMilliseconds} ms");
            Assert.DoesNotContain(result.Warnings, w => w.Contains("Time budget"));
        }
    }
}
