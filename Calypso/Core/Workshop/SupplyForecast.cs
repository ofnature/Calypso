using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Workshop;

/// <summary>
/// What each craft's supply bucket is (or is likely to be) on each day of the week, built from this character's
/// observations and the <see cref="PeakPatterns"/> table.
///
/// Observed day: the bucket seen, which already includes our own production from earlier days.
/// Unobserved day: the natural bucket of every pattern still consistent with the observations, equally weighted
/// (the decided "expected value over consistent patterns"); no consistent pattern → Sufficient, as WorkshopOptimizer does.
/// </summary>
public sealed class SupplyForecast
{
    private const int Days = 7;
    private const int Buckets = 5;

    private readonly int[] _supplyRatio;
    private readonly int _maxCraftId;

    // [craft][day][bucket] probability, and whether that day's bucket was observed.
    private readonly double[][][] _dist;
    private readonly bool[][] _observed;
    private readonly int[] _patternCount;

    /// <param name="observed">Per day 0-6: the 91 packed MJIManager.SupplyAndDemandShifts bytes seen that day, or null.</param>
    /// <param name="producedOnDay">Our own production per day (craft id → items), used to tell when a day's observed
    /// supply was already moved by us and must not be matched against the natural table.</param>
    public SupplyForecast(CraftCatalog catalog, IReadOnlyList<byte[]?> observed, IReadOnlyList<int[]?> producedOnDay)
    {
        _supplyRatio = catalog.Rules.SupplyRatio;
        _maxCraftId = catalog.MaxCraftId;
        _dist = new double[_maxCraftId + 1][][];
        _observed = new bool[_maxCraftId + 1][];
        _patternCount = new int[_maxCraftId + 1];

        for (var c = 0; c <= _maxCraftId; c++)
        {
            _dist[c] = new double[Days][];
            _observed[c] = new bool[Days];

            var seen = new (Supply, DemandShift)?[Days];
            for (var d = 0; d < Days && d < observed.Count; d++)
            {
                if (observed[d] is { } bytes && c < bytes.Length)
                    seen[d] = SupplyMath.Unpack(bytes[c]);
            }

            var craft = c;
            var patterns = PeakPatterns.Consistent(seen, day => ProducedBefore(producedOnDay, craft, day) > 0);
            _patternCount[c] = patterns.Count;

            for (var d = 0; d < Days; d++)
            {
                var p = new double[Buckets];
                if (seen[d] is { } s)
                {
                    p[(int)s.Item1] = 1;
                    _observed[c][d] = true;
                }
                else if (patterns.Count == 0)
                {
                    p[(int)Supply.Sufficient] = 1;
                }
                else
                {
                    foreach (var pattern in patterns)
                        p[(int)pattern.Supply[d]] += 1.0 / patterns.Count;
                }

                _dist[c][d] = p;
            }
        }
    }

    /// <summary>Patterns still consistent for a craft; 1 = its peak is pinned down.</summary>
    public int PatternCount(int craftId) => craftId >= 0 && craftId <= _maxCraftId ? _patternCount[craftId] : 0;

    /// <summary>Probability of the most likely bucket — 1 when observed or pinned to one pattern.</summary>
    public double Confidence(int craftId, int day) =>
        craftId < 0 || craftId > _maxCraftId || day < 0 || day >= Days ? 0 : _dist[craftId][day].Max();

    /// <summary>
    /// Expected supply ratio % for a sale on <paramref name="day"/>.
    /// <paramref name="producedBeforeDay"/> = our production this week on earlier days; it is already inside an observed
    /// bucket, so it only counts on unobserved days. <paramref name="producedEarlierToday"/> always counts.
    /// </summary>
    public double ExpectedRatio(int craftId, int day, int producedBeforeDay, int producedEarlierToday)
    {
        if (craftId < 0 || craftId > _maxCraftId || day < 0 || day >= Days)
            return _supplyRatio[(int)Supply.Sufficient];

        var extra = producedEarlierToday + (_observed[craftId][day] ? 0 : producedBeforeDay);
        var dist = _dist[craftId][day];
        var sum = 0.0;
        for (var b = 0; b < Buckets; b++)
        {
            if (dist[b] > 0)
                sum += dist[b] * SupplyMath.ExpectedRatio(_supplyRatio, (Supply)b, extra);
        }

        return sum;
    }

    /// <summary>
    /// Probability of each supply bucket (index = <see cref="Supply"/>) for a sale on <paramref name="day"/>, under the
    /// same rules as <see cref="ExpectedRatio"/> (whose value is the mean ratio of this distribution).
    /// </summary>
    public void SupplyWeights(int craftId, int day, int producedBeforeDay, int producedEarlierToday, Span<double> weights)
    {
        weights.Clear();
        if (craftId < 0 || craftId > _maxCraftId || day < 0 || day >= Days)
        {
            weights[(int)Supply.Sufficient] = 1;
            return;
        }

        var extra = producedEarlierToday + (_observed[craftId][day] ? 0 : producedBeforeDay);
        var dist = _dist[craftId][day];
        for (var b = 0; b < Buckets; b++)
        {
            if (dist[b] > 0)
                SupplyMath.AddBucketWeights(weights, (Supply)b, extra, dist[b]);
        }
    }

    private static int ProducedBefore(IReadOnlyList<int[]?> producedOnDay, int craft, int day)
    {
        var n = 0;
        for (var d = 0; d < day && d < producedOnDay.Count; d++)
        {
            if (producedOnDay[d] is { } p && craft < p.Length)
                n += p[craft];
        }

        return n;
    }
}
