using System;
using System.Collections.Generic;

namespace Calypso.Core.Workshop;

/// <summary>
/// Simulates one cycle of up to four workshops hour by hour.
///
/// Per craft (see <see cref="CraftValue"/>; the game truncates twice, in 32-bit float):
///   base  = floor(BaseValue × tier × groove)
///   value = floor(base × popularity% × supply% / 10000) × items
/// where items = 1 for a chain's first craft and 2 for every efficient craft after it. The supply bucket is a forecast,
/// so a sale is the expectation of that whole-cowrie value over the buckets it may land in.
///
/// Groove rises by 1 (capped) when a workshop STARTS an efficient craft, so that craft already sells with its own point.
/// A craft finishing at hour h sells at the groove from every efficient craft started before h; crafts finishing in the
/// same hour are valued against the same state, and only then are their items added to that day's supply. This is what
/// mji-craftwork and Beachcomber (Squawkbot's model) do; WorkshopOptimizer counts groove on completion, which the
/// reference example in ReferenceTests shows to be wrong.
/// </summary>
public sealed class DayEvaluator
{
    private readonly CraftCatalog _catalog;
    private readonly SupplyForecast _forecast;
    private readonly int _popularityRow;
    private readonly int _maxGroove;

    // Reused per call: (hour, workshop, step, craft) of every finishing craft.
    private readonly List<(int Hour, int Workshop, int Step, int Craft)> _events = new(24);

    // Reused per call: efficient crafts started at each hour of the cycle.
    private readonly int[] _efficientStarts = new int[ChainGenerator.HoursPerCycle + 1];

    // Reused per craft: probability of selling at each supply bucket.
    private readonly double[] _supplyWeights;

    public DayEvaluator(CraftCatalog catalog, SupplyForecast forecast, int popularityRow, int maxGroove)
    {
        _catalog = catalog;
        _forecast = forecast;
        _popularityRow = popularityRow;
        _maxGroove = maxGroove;
        _supplyWeights = new double[catalog.Rules.SupplyRatio.Length];
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
        Array.Clear(_efficientStarts);
        var efficientTotal = 0;
        for (var w = 0; w < chains.Count; w++)
        {
            if (chains[w] is not { } chain || tiers[w] <= 0)
                continue;

            var hour = 0;
            for (var step = 0; step < chain.Length; step++)
            {
                if (step > 0 && hour < _efficientStarts.Length)
                {
                    _efficientStarts[hour]++;
                    efficientTotal++;
                }

                hour += _catalog.Get(chain[step])!.Hours;
                _events.Add((hour, w, step, chain[step]));
            }
        }

        _events.Sort(static (a, b) => a.Hour != b.Hour ? a.Hour.CompareTo(b.Hour) : a.Workshop.CompareTo(b.Workshop));

        var startingGroove = Math.Min(startGroove, _maxGroove);
        var startedBefore = 0;
        var countedHours = 0;
        var value = 0.0;
        var i = 0;
        while (i < _events.Count)
        {
            var hour = _events[i].Hour;
            var end = i;
            while (end < _events.Count && _events[end].Hour == hour)
                end++;

            while (countedHours < hour && countedHours < _efficientStarts.Length)
                startedBefore += _efficientStarts[countedHours++];
            var groove = Math.Min(startingGroove + startedBefore, _maxGroove);

            // Value every craft finishing this hour against the state at the start of the hour.
            for (var e = i; e < end; e++)
            {
                var ev = _events[e];
                var craft = _catalog.Get(ev.Craft)!;
                var items = ev.Step == 0 ? 1 : 2;
                var grooveBase = GrooveBase(craft.BaseValue, _catalog.Rules.TierRatio(tiers[ev.Workshop]), groove);
                var popularity = _catalog.Rules.PopularityRatio(_popularityRow, ev.Craft);
                _forecast.SupplyWeights(ev.Craft, day, producedBefore[ev.Craft], producedToday[ev.Craft], _supplyWeights);
                var sale = 0.0;
                for (var b = 0; b < _supplyWeights.Length; b++)
                {
                    if (_supplyWeights[b] > 0)
                        sale += _supplyWeights[b] * Sale(grooveBase, popularity, _catalog.Rules.SupplyRatio[b]);
                }

                value += sale * items;
            }

            for (var e = i; e < end; e++)
            {
                var ev = _events[e];
                producedToday[ev.Craft] += ev.Step == 0 ? 1 : 2;
            }

            i = end;
        }

        return (value, Math.Min(startingGroove + efficientTotal, _maxGroove));
    }

    /// <summary>
    /// The value of one craft in isolation with a known supply ratio. For tests and the UI.
    ///
    /// The game computes in 32-bit float: floor(floor(base × (1 + rank bonus) × (1 + groove/100)) × (popularity% ×
    /// supply%)/10000). Exact integer arithmetic differs at the edges: base 136, tier IV, groove 25 is 221.0 exactly but
    /// 220.99998 in float, so a Nonexistent/High sale pays 422, not 424. The float form and its order are the ones
    /// mji-craftwork settled on from in-game measurements (see ReferenceTests).
    /// </summary>
    public static int CraftValue(int baseValue, int tierRatio, int groove, int popularityRatio, int supplyRatio, bool efficient) =>
        Sale(GrooveBase(baseValue, tierRatio, groove), popularityRatio, supplyRatio) * (efficient ? 2 : 1);

    // The (float) casts pin every product to single precision, so no wider intermediate can change a truncation.
    private static float GrooveBase(int baseValue, int tierRatio, int groove) =>
        tierRatio <= 0 ? 0 : MathF.Floor((float)((float)(baseValue * (1f + (tierRatio - 100) * 0.01f)) * (1f + groove * 0.01f)));

    private static int Sale(float grooveBase, int popularityRatio, int supplyRatio) =>
        (int)MathF.Floor((float)(grooveBase * (float)(popularityRatio * supplyRatio / 10000f)));
}
