using System;
using System.Collections.Generic;

namespace Calypso.Core.Workshop;

/// <summary>
/// Simulates one cycle of up to four workshops hour by hour.
///
/// Per craft (Squawkbot's published formula, integers where the game truncates):
///   base  = floor(BaseValue × tier% / 100 × (100 + groove) / 100)
///   value = base × popularity% / 100 × E[supply%] / 100 × items
/// where items = 1 for a chain's first craft and 2 for every efficient craft after it. The supply step stays a
/// real-valued expectation because the bucket itself is a forecast.
///
/// Crafts finishing in the same hour are valued against the same state; then their items are added to that day's
/// supply and each efficient one raises groove by 1 (capped). This matches WorkshopOptimizer's hour loop.
/// </summary>
public sealed class DayEvaluator
{
    private readonly CraftCatalog _catalog;
    private readonly SupplyForecast _forecast;
    private readonly int _popularityRow;
    private readonly int _maxGroove;

    // Reused per call: (hour, workshop, step, craft) of every finishing craft.
    private readonly List<(int Hour, int Workshop, int Step, int Craft)> _events = new(24);

    public DayEvaluator(CraftCatalog catalog, SupplyForecast forecast, int popularityRow, int maxGroove)
    {
        _catalog = catalog;
        _forecast = forecast;
        _popularityRow = popularityRow;
        _maxGroove = maxGroove;
    }

    public int MaxGroove => _maxGroove;

    /// <param name="chains">Chain per workshop (null = idle/unbuilt), parallel to <paramref name="tiers"/>.</param>
    /// <param name="producedBefore">Own production this week before <paramref name="day"/>, by craft id. Not modified.</param>
    /// <param name="producedToday">Zeroed scratch array (length MaxCraftId+1) that receives today's production.</param>
    public (double Value, int EndGroove) Evaluate(
        int day,
        IReadOnlyList<int[]?> chains,
        IReadOnlyList<int> tiers,
        int startGroove,
        int[] producedBefore,
        int[] producedToday)
    {
        _events.Clear();
        for (var w = 0; w < chains.Count; w++)
        {
            if (chains[w] is not { } chain || tiers[w] <= 0)
                continue;

            var hour = 0;
            for (var step = 0; step < chain.Length; step++)
            {
                hour += _catalog.Get(chain[step])!.Hours;
                _events.Add((hour, w, step, chain[step]));
            }
        }

        _events.Sort(static (a, b) => a.Hour != b.Hour ? a.Hour.CompareTo(b.Hour) : a.Workshop.CompareTo(b.Workshop));

        var groove = Math.Min(startGroove, _maxGroove);
        var value = 0.0;
        var i = 0;
        while (i < _events.Count)
        {
            var hour = _events[i].Hour;
            var end = i;
            while (end < _events.Count && _events[end].Hour == hour)
                end++;

            // Value every craft finishing this hour against the state at the start of the hour.
            for (var e = i; e < end; e++)
            {
                var ev = _events[e];
                var craft = _catalog.Get(ev.Craft)!;
                var items = ev.Step == 0 ? 1 : 2;
                var baseValue = (int)(craft.BaseValue * _catalog.Rules.TierRatio(tiers[ev.Workshop]) / 100.0 * (100 + groove) / 100.0);
                var supply = _forecast.ExpectedRatio(ev.Craft, day, producedBefore[ev.Craft], producedToday[ev.Craft]);
                value += baseValue * _catalog.Rules.PopularityRatio(_popularityRow, ev.Craft) / 100.0 * supply / 100.0 * items;
            }

            for (var e = i; e < end; e++)
            {
                var ev = _events[e];
                producedToday[ev.Craft] += ev.Step == 0 ? 1 : 2;
                if (ev.Step > 0 && groove < _maxGroove)
                    groove++;
            }

            i = end;
        }

        return (value, groove);
    }

    /// <summary>The value of one craft in isolation — the formula above with a known supply ratio. For tests and the UI.</summary>
    public static int CraftValue(int baseValue, int tierRatio, int groove, int popularityRatio, int supplyRatio, bool efficient)
    {
        var b = (int)(baseValue * tierRatio / 100.0 * (100 + groove) / 100.0);
        return (int)(b * popularityRatio / 100.0 * supplyRatio / 100.0) * (efficient ? 2 : 1);
    }
}
