using Calypso.Core.Farm;
using Calypso.Core.Granary;
using Calypso.Core.Pasture;
using Calypso.Core.Workshop;
using Calypso.Tests.Pasture;
using Calypso.Tests.Workshop;
using Xunit.Abstractions;

namespace Calypso.Tests.Farm;

/// <summary>A real <see cref="WeekPlanner"/> week on the craftworks fixture, its produce and leavings shortfall, and both planners on it.</summary>
public class WorkshopIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public WorkshopIntegrationTests(ITestOutputHelper output) => _out = output;

    private static readonly HashSet<int> Produce = Fixture.Catalog.Pouch.Values.Where(p => p.Kind == MaterialKind.Produce).Select(p => p.Row).ToHashSet();
    private static readonly HashSet<int> Leavings = Fixture.Catalog.Pouch.Values.Where(p => p.Kind == MaterialKind.Leavings).Select(p => p.Row).ToHashSet();

    /// <summary>Plenty of everything but produce and leavings, of which there are <paramref name="each"/>.</summary>
    private static Dictionary<int, int> NoFarmGoods(int each = 0) =>
        Fixture.Catalog.Pouch.Keys.ToDictionary(k => k, k => Produce.Contains(k) || Leavings.Contains(k) ? each : 999);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TheShortfallIsWhatThePlanWouldUseIfProduceAndLeavingsWerePlentiful(int seed)
    {
        var input = Scenarios.Week(seed, materials: NoFarmGoods());
        var needs = FarmNeeds.FromWorkshop(input);
        var uncapped = WeekPlanner.Plan(input with { Materials = NoFarmGoods(999) });

        _out.WriteLine(string.Join(", ", needs.Select(n => $"{n.Amount}×{n.PouchRow}@{n.DueInDays}")));
        Assert.NotEmpty(needs);
        Assert.All(needs, n => Assert.True(Produce.Contains(n.PouchRow) || Leavings.Contains(n.PouchRow)));
        var used = uncapped.Days.SelectMany(d => GranaryNeeds.MaterialsUsed(d, input)).Where(kv => Produce.Contains(kv.Key) || Leavings.Contains(kv.Key))
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        Assert.Equal(used, needs.GroupBy(n => n.PouchRow).ToDictionary(g => g.Key, g => g.Sum(n => n.Amount)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TheCroplandPlantsThePlansProduceShortfall(int seed)
    {
        var input = Scenarios.Week(seed, materials: NoFarmGoods());
        var needs = FarmNeeds.FromWorkshop(input);
        var onHand = input.Materials.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var crop in FarmFixture.Crops)
            onHand[crop.SeedPouchRow] = 50;

        var plan = FarmPlanner.Plan(new FarmInput
        {
            Level = 3,
            Plots = Farms.EmptyPlots(20),
            Crops = FarmFixture.Crops,
            OnHand = onHand,
            Cowries = 10_000,
            Needs = needs,
            MaterialNames = FarmFixture.Names,
        });

        foreach (var d in plan.Plots)
            _out.WriteLine($"{d.Slot}: {d.Reason}");

        // Every produce the plan is short of, in time to grow (two days), gets a plot for the workshop — and only those.
        var growable = needs.Where(n => Produce.Contains(n.PouchRow) && n.DueInDays >= new FarmRules().GrowDays).Select(n => n.PouchRow).ToHashSet();
        Assert.NotEmpty(growable);
        var forWorkshop = plan.Plots.Where(d => d.Action == FarmAction.Plant && d.Reason.Contains("this week's workshop"))
            .Select(d => FarmFixture.Crops.First(c => c.Id == d.Crop).ProducePouchRow).ToHashSet();
        Assert.Equal(growable, forWorkshop);

        // The workshop's produce is planted before the stock floor's.
        var firstFloor = plan.Plots.ToList().FindIndex(d => d.Reason.Contains("the stock floor"));
        var lastWorkshop = plan.Plots.ToList().FindLastIndex(d => d.Reason.Contains("this week's workshop"));
        Assert.True(firstFloor < 0 || lastWorkshop < firstFloor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ThePastureAdviceTargetsThePlansLeavingsShortfall(int seed)
    {
        var input = Scenarios.Week(seed, materials: NoFarmGoods(each: 100));
        var needs = FarmNeeds.FromWorkshop(input with { Materials = NoFarmGoods() })
            .Where(n => Leavings.Contains(n.PouchRow)).ToList();
        Assert.NotEmpty(needs);

        var plan = PasturePlanner.Plan(Pastures.Input(needs: needs, onHand: NoFarmGoods(each: 100).Where(kv => Leavings.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)));
        foreach (var a in plan.Advice)
            _out.WriteLine($"{a.Kind}: {a.Text}");

        var captures = plan.Advice.Where(a => a.Kind == AdviceKind.Capture).ToList();
        Assert.NotEmpty(captures);
        var needed = needs.Select(n => n.PouchRow).ToHashSet();
        Assert.All(captures, c => Assert.Contains(PastureFixture.Animal(c.Animal).Leavings, needed.Contains));
        Assert.All(captures, c => Assert.Contains("this week's workshop", c.Text));
    }
}
