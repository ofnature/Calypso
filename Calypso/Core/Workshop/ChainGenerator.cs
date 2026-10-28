using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Calypso.Core.Workshop;

/// <summary>
/// Every efficiency chain one workshop could run in a day: consecutive crafts that differ and share a theme, total
/// time within the cycle, and maximal (nothing more fits after it). A non-maximal chain is never better — the extra
/// craft only adds value — so prefixes are not listed.
/// </summary>
public static class ChainGenerator
{
    public const int HoursPerCycle = 24;

    /// <summary>
    /// The <paramref name="keep"/> best maximal chains by an additive per-craft weight (first craft ×1, each
    /// efficient craft after it ×2 — the item counts). Branch and bound: a branch is dropped when even filling its
    /// remaining hours at the best weight-per-hour could not beat the current worst kept chain. The full chain space
    /// on the real catalog is ~1.4M chains (measured 2026-10-17), so the planner never enumerates it.
    /// </summary>
    public static List<int[]> TopChains(CraftCatalog catalog, Func<CraftDef, bool> allowed, Func<int, double> weight, int keep)
    {
        var crafts = catalog.Crafts.Where(allowed).ToList();
        if (crafts.Count == 0 || keep <= 0)
            return new List<int[]>();

        var w = crafts.ToDictionary(c => c.Id, c => Math.Max(0, weight(c.Id)));
        var bestRate = crafts.Max(c => w[c.Id] / c.Hours);
        var followers = crafts.ToDictionary(
            c => c.Id,
            c => crafts.Where(n => n.ChainsAfter(c)).OrderByDescending(n => w[n.Id]).ToArray());

        var heap = new PriorityQueue<int[], double>();
        var path = new List<CraftDef>(6);

        foreach (var first in crafts.OrderByDescending(c => w[c.Id]))
        {
            path.Clear();
            path.Add(first);
            Search(first.Hours, w[first.Id]);
        }

        var result = new List<(int[] Chain, double Score)>();
        while (heap.TryDequeue(out var chain, out var score))
            result.Add((chain, score));
        result.Reverse();
        return result.Select(r => r.Chain).ToList();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // hot recursion; see DayEvaluator.Evaluate
        void Search(int hours, double score)
        {
            if (heap.Count >= keep && heap.TryPeek(out _, out var worst) && score + 2 * bestRate * (HoursPerCycle - hours) <= worst)
                return;

            var extended = false;
            foreach (var next in followers[path[^1].Id])
            {
                if (hours + next.Hours > HoursPerCycle)
                    continue;

                extended = true;
                path.Add(next);
                Search(hours + next.Hours, score + 2 * w[next.Id]);
                path.RemoveAt(path.Count - 1);
            }

            if (extended)
                return;

            if (heap.Count < keep)
            {
                heap.Enqueue(path.Select(c => c.Id).ToArray(), score);
            }
            else if (heap.TryPeek(out _, out var min) && score > min)
            {
                heap.Dequeue();
                heap.Enqueue(path.Select(c => c.Id).ToArray(), score);
            }
        }
    }

    /// <summary>Every maximal chain — exhaustive, for tests on small rank-limited sets only (the full space is ~1.4M).</summary>
    public static List<int[]> Generate(CraftCatalog catalog, Func<CraftDef, bool> allowed)
    {
        var crafts = catalog.Crafts.Where(allowed).ToList();
        var followers = crafts.ToDictionary(c => c.Id, c => crafts.Where(n => n.ChainsAfter(c)).ToArray());
        var result = new List<int[]>();
        var path = new List<CraftDef>(6);

        foreach (var first in crafts)
        {
            path.Clear();
            path.Add(first);
            Extend(first.Hours);
        }

        return result;

        void Extend(int hours)
        {
            var extended = false;
            foreach (var next in followers[path[^1].Id])
            {
                if (hours + next.Hours > HoursPerCycle)
                    continue;

                extended = true;
                path.Add(next);
                Extend(hours + next.Hours);
                path.RemoveAt(path.Count - 1);
            }

            if (!extended)
                result.Add(path.Select(c => c.Id).ToArray());
        }
    }
}
