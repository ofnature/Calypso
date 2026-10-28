using Calypso.Core.Granary;
using Calypso.Core.Workshop;
using Calypso.Tests.Workshop;
using Xunit.Abstractions;
using static Calypso.Tests.Granary.Granaries;

namespace Calypso.Tests.Granary;

public class GranaryNeedsTests
{
    private readonly ITestOutputHelper _out;

    public GranaryNeedsTests(ITestOutputHelper output) => _out = output;

    private static readonly int[] Rares = Fixture.Catalog.Pouch.Values.Where(p => p.Kind == MaterialKind.GranaryRare).Select(p => p.Row).ToArray();

    /// <summary>Plenty of everything but the granary rares, of which there are <paramref name="rares"/> each.</summary>
    private static Dictionary<int, int> NoRares(int rares = 0) =>
        Fixture.Catalog.Pouch.Keys.ToDictionary(k => k, k => Rares.Contains(k) ? rares : 999);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MaterialsUsedChargesEveryWorkshopTheWayThePlanCountsItems(int seed)
    {
        var input = Scenarios.Week(seed);
        var plan = WeekPlanner.Plan(input);

        foreach (var day in plan.Days)
        {
            var expected = new Dictionary<int, int>();
            var items = Scenarios.Items(day, 4);
            for (var id = 0; id < items.Length; id++)
            {
                foreach (var m in items[id] == 0 ? Array.Empty<MaterialNeed>() : Fixture.Catalog.Get(id)!.Materials)
                    expected[m.PouchRow] = expected.GetValueOrDefault(m.PouchRow) + m.Amount * items[id];
            }

            Assert.Equal(expected, GranaryNeeds.MaterialsUsed(day, input));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TheWorkshopShortfallIsWhatThePlanWouldUseIfRaresWerePlentiful(int seed)
    {
        var input = Scenarios.Week(seed, materials: NoRares());
        var needs = GranaryNeeds.FromWorkshop(input);
        var capped = WeekPlanner.Plan(input);
        var uncapped = WeekPlanner.Plan(input with { Materials = NoRares(999) });

        _out.WriteLine(string.Join(", ", needs.Select(n => $"{n.Amount}×{n.PouchRow}@{n.DueInDays}")));
        Assert.NotEmpty(needs);
        Assert.All(needs, n => Assert.Contains(n.PouchRow, Rares));
        Assert.All(needs, n => Assert.Equal(NeedSource.WorkshopThisWeek, n.Source));

        // The real plan has no rares at all, so it uses none; the shortfall is everything the plentiful plan uses.
        Assert.All(capped.Days, d => Assert.DoesNotContain(GranaryNeeds.MaterialsUsed(d, input).Keys, r => Rares.Contains(r)));
        var used = uncapped.Days.SelectMany(d => GranaryNeeds.MaterialsUsed(d, input)).Where(kv => Rares.Contains(kv.Key))
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        Assert.Equal(used, needs.GroupBy(n => n.PouchRow).ToDictionary(g => g.Key, g => g.Sum(n => n.Amount)));

        // Due before the day it is crafted: a need for tomorrow (FirstDay) can only be met from what is already waiting.
        Assert.All(needs, n => Assert.InRange(n.DueInDays, 0, 6 - input.FirstDay));
    }

    [Fact]
    public void RaresOnHandAreUsedBeforeAnythingIsNeeded()
    {
        var input = Scenarios.Week(1, materials: NoRares());
        var none = GranaryNeeds.FromWorkshop(input);
        var some = GranaryNeeds.FromWorkshop(input with { Materials = NoRares(10) });

        foreach (var row in none.Select(n => n.PouchRow).Distinct())
        {
            var short0 = none.Where(n => n.PouchRow == row).Sum(n => n.Amount);
            var short10 = some.Where(n => n.PouchRow == row).Sum(n => n.Amount);
            Assert.Equal(Math.Max(0, short0 - 10), short10);
        }
    }

    [Fact]
    public void NextWeeksNeedsAreDueAfterThisWeeks()
    {
        var thisWeek = Scenarios.Week(1, materials: NoRares());
        var next = GranaryNeeds.FromWorkshop(thisWeek with { FirstDay = 0, RestsTaken = 0 }, NeedSource.WorkshopNextWeek, dueOffset: 7 - thisWeek.FirstDay);

        Assert.NotEmpty(next);
        Assert.All(next, n => Assert.Equal(NeedSource.WorkshopNextWeek, n.Source));
        Assert.All(next, n => Assert.True(n.DueInDays >= 7 - thisWeek.FirstDay));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TheGranariesTargetTheWorkshopPlansShortfall(int seed)
    {
        var week = Scenarios.Week(seed, materials: NoRares());
        var needs = GranaryNeeds.FromWorkshop(week);
        var onHand = week.Materials.ToDictionary(kv => kv.Key, kv => kv.Value);

        var plan = GranaryPlanner.Plan(Input(new[] { Home(0), Home(1) }, needs, onHand: onHand));
        foreach (var d in plan.Granaries)
            _out.WriteLine($"granary {d.Index}: {d.Action} {d.Area} {d.Days}d — {d.Reason}");

        // Every granary goes after a rare the plan is short of, and the most needed one that can still arrive in time
        // gets a granary.
        var areas = GranaryFixture.Areas;
        var shortRows = needs.Select(n => n.PouchRow).ToHashSet();
        Assert.All(plan.Granaries, d =>
        {
            Assert.Equal(GranaryAction.Dispatch, d.Action);
            Assert.Contains(areas[d.Area!.Value].RarePouchRow, shortRows);
            Assert.Contains("this week's workshop", d.Reason);
        });
        var top = needs.Where(n => n.DueInDays > 0).GroupBy(n => n.PouchRow).OrderByDescending(g => g.Sum(n => n.Amount)).ThenBy(g => g.Key).First().Key;
        Assert.Contains(plan.Granaries, d => areas[d.Area!.Value].RarePouchRow == top);
    }

    [Fact]
    public void ReservationsAreNeedsOnlyWhereThePouchFallsShort()
    {
        var reserved = new[]
        {
            new Construction("Granary II", 6, new[] { new MaterialNeed(Garnet, 10), new MaterialNeed(Sand, 50) }),
            new Construction("Landmark", 8, new[] { new MaterialNeed(Garnet, 5) }),
        };
        var needs = GranaryNeeds.FromReservations(new Dictionary<int, int> { [Garnet] = 4, [Sand] = 100 }, reserved, 7);

        var n = Assert.Single(needs);
        Assert.Equal(new GranaryNeed(Garnet, 11, 7, NeedSource.Construction), n);
    }

    [Fact]
    public void OnlySlowGatheringBecomesANeed()
    {
        var demand = new Dictionary<int, int> { [Coal] = 40, [Sand] = 20, [3] = 0 };
        var needs = GranaryNeeds.FromGatherDemand(demand, new HashSet<int> { Coal, 3 }, 5);

        Assert.Equal(new[] { new GranaryNeed(Coal, 40, 5, NeedSource.SlowGather) }, needs);
    }

    [Fact]
    public void TheWorkshopOutranksConstructionInOneInput()
    {
        var needs = new List<GranaryNeed>(GranaryNeeds.FromReservations(new Dictionary<int, int>(),
            new[] { new Construction("Granary II", 6, new[] { new MaterialNeed(Silver, 20) }) }, 7)) { Short(Hammerhead, 20) };
        var plan = GranaryPlanner.Plan(Input(new[] { Home(0) }, needs));

        Assert.Equal(3, plan.Granaries[0].Area);
    }
}
