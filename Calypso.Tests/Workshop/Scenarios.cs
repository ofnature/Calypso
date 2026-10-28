using Calypso.Core.Workshop;

namespace Calypso.Tests.Workshop;

/// <summary>
/// Seeded weeks on the real catalog: a popularity row, one peak pattern per craft, and day 0 observed the way the game
/// would show it. The usual community routine: rest on day 0, plan days 1-6 with one more rest.
/// </summary>
internal static class Scenarios
{
    public static PlannerInput Week(int seed, int firstDay = 1, int restsTaken = 1, PlannerLimits? limits = null,
        IReadOnlyList<FavorRequirement>? favors = null, IReadOnlyDictionary<int, int>? materials = null, int rank = 20)
    {
        var cat = Fixture.Catalog;
        var rng = new Random(seed);
        var popularityRow = rng.Next(cat.Rules.Popularity.Length);
        var patterns = Enumerable.Range(0, cat.MaxCraftId + 1).Select(_ => PeakPatterns.All[rng.Next(PeakPatterns.All.Count)]).ToArray();

        var observed = new byte[]?[7];
        for (var day = 0; day < firstDay; day++)
            observed[day] = patterns.Select(p => Pack(p, day)).ToArray();

        return new PlannerInput
        {
            Catalog = cat,
            Forecast = new SupplyForecast(cat, observed, new int[]?[7]),
            Rank = rank,
            WorkshopTiers = new[] { 5, 5, 5, 5 },
            PopularityRow = popularityRow,
            MaxGroove = 45,
            StartGroove = 0,
            FirstDay = firstDay,
            RestsTaken = restsTaken,
            ProducedBefore = new int[cat.MaxCraftId + 1],
            Materials = materials ?? Fixture.AllMaterials(),
            Favors = favors ?? Array.Empty<FavorRequirement>(),
            Limits = limits ?? new PlannerLimits(),
        };
    }

    /// <summary>The packed supply/demand byte a pattern shows on a day (a free day-0 shift shown as one it allows).</summary>
    public static byte Pack(PeakPattern p, int day)
    {
        var demand = p.Demand[day] ?? (p.FirstDayDemandNever == DemandShift.None ? DemandShift.Increasing : DemandShift.None);
        return (byte)(((int)p.Supply[day] << 4) | (int)demand);
    }

    /// <summary>Items per craft a planned day makes in <paramref name="workshops"/> workshops.</summary>
    public static int[] Items(PlannedDay day, int workshops)
    {
        var items = new int[Fixture.Catalog.MaxCraftId + 1];
        if (day.Rest)
            return items;
        for (var w = 0; w < workshops; w++)
        {
            var chain = w < day.MainCount ? day.Main : day.Fourth;
            for (var i = 0; chain != null && i < chain.Length; i++)
                items[chain[i]] += i == 0 ? 1 : 2;
        }

        return items;
    }
}
