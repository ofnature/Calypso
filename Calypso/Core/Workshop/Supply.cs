using System;

namespace Calypso.Core.Workshop;

/// <summary>Supply bucket. Values equal ClientStructs CraftworkSupply and the MJICraftworksSupplyDefine row index.</summary>
public enum Supply : byte
{
    Nonexistent = 0,
    Insufficient = 1,
    Sufficient = 2,
    Surplus = 3,
    Overflowing = 4,
}

/// <summary>Demand shift. Values equal ClientStructs CraftworkDemandShift.</summary>
public enum DemandShift : byte
{
    Skyrocketing = 0,
    Increasing = 1,
    None = 2,
    Decreasing = 3,
    Plummeting = 4,
}

public static class SupplyMath
{
    /// <summary>Own production that moves an item one supply bucket (community model; WorkshopOptimizer uses the same 8).</summary>
    public const int ItemsPerBucket = 8;

    /// <summary>
    /// Expected supply ratio % after <paramref name="produced"/> more items of an item currently in
    /// <paramref name="bucket"/>. The game shows only the bucket, not where in it the item sits; treating that position
    /// as uniform makes the chance of having crossed into the next bucket produced/8 — which is exactly the linear
    /// blend WorkshopOptimizer uses. Each further 8 items is one whole bucket.
    /// </summary>
    public static double ExpectedRatio(int[] supplyRatio, Supply bucket, int produced)
    {
        var b = (int)bucket;
        var last = supplyRatio.Length - 1;
        if (produced < 0)
            produced = 0;

        while (produced >= ItemsPerBucket && b < last)
        {
            b++;
            produced -= ItemsPerBucket;
        }

        if (b >= last)
            return supplyRatio[last];

        return (supplyRatio[b] * (ItemsPerBucket - produced) + supplyRatio[b + 1] * (double)produced) / ItemsPerBucket;
    }

    /// <summary>
    /// The same model as <see cref="ExpectedRatio"/>, as a distribution: adds <paramref name="weight"/> to
    /// <paramref name="into"/> (indexed by bucket) split between the buckets the item may be in after
    /// <paramref name="produced"/> more. The game truncates each sale, so a value has to be averaged over these buckets;
    /// truncating the value at the averaged ratio would be wrong.
    /// </summary>
    public static void AddBucketWeights(Span<double> into, Supply bucket, int produced, double weight)
    {
        var b = (int)bucket;
        var last = into.Length - 1;
        if (produced < 0)
            produced = 0;

        while (produced >= ItemsPerBucket && b < last)
        {
            b++;
            produced -= ItemsPerBucket;
        }

        if (b >= last)
        {
            into[last] += weight;
            return;
        }

        into[b] += weight * (ItemsPerBucket - produced) / ItemsPerBucket;
        into[b + 1] += weight * produced / ItemsPerBucket;
    }

    /// <summary>MJIManager.SupplyAndDemandShifts byte: supply in the high nibble, demand shift in the low nibble.</summary>
    public static (Supply Supply, DemandShift Demand) Unpack(byte packed) =>
        ((Supply)Math.Min(packed >> 4, 4), (DemandShift)Math.Min(packed & 0xF, 4));
}
