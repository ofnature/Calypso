using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Calypso.Core.Workshop;

namespace Calypso.Core.Granary;

/// <summary>Turns what the rest of Calypso plans into <see cref="GranaryNeed"/>s.</summary>
public static class GranaryNeeds
{
    /// <summary>What the uncapped plan is offered of every granary rare: the pouch cap, so only value limits it.</summary>
    private const int Unlimited = 999;

    /// <summary>
    /// The granary rares a week's workshop plan would use and the pouch cannot cover. <see cref="WeekPlanner"/> treats
    /// rares as hard caps, so its own plan is never short of one — it just leaves those crafts out. Planning the same
    /// week again with every rare plentiful shows what the workshop would make of them; what that plan uses beyond the
    /// pouch is the need, day by day, due before the day it is crafted (the agenda for a day is set the day before).
    ///
    /// For next week pass that week's input (FirstDay 0), <see cref="NeedSource.WorkshopNextWeek"/> and
    /// <paramref name="dueOffset"/> = 7 − this week's FirstDay.
    /// </summary>
    public static List<GranaryNeed> FromWorkshop(PlannerInput input, NeedSource source = NeedSource.WorkshopThisWeek, int dueOffset = 0,
        CancellationToken cancel = default)
    {
        var rares = input.Catalog.Pouch.Values.Where(p => p.Kind == MaterialKind.GranaryRare).Select(p => p.Row).ToList();
        var lifted = input.Materials.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var row in rares)
            lifted[row] = Unlimited;

        var plan = WeekPlanner.Plan(input with { Materials = lifted }, cancel);
        return Shortfall(plan, input, rares, source, dueOffset);
    }

    /// <summary>Per planned day, what of <paramref name="rows"/> the plan uses beyond <see cref="PlannerInput.Materials"/>.</summary>
    public static List<GranaryNeed> Shortfall(PlanResult plan, PlannerInput input, IEnumerable<int> rows, NeedSource source, int dueOffset = 0)
    {
        var wanted = rows.ToHashSet();
        var used = new Dictionary<int, int>();
        var needs = new List<GranaryNeed>();
        foreach (var day in plan.Days.OrderBy(d => d.Day))
        {
            foreach (var (row, n) in MaterialsUsed(day, input).Where(kv => wanted.Contains(kv.Key)).OrderBy(kv => kv.Key))
            {
                var onHand = input.Materials.GetValueOrDefault(row);
                var before = Math.Max(0, used.GetValueOrDefault(row) - onHand);
                used[row] = used.GetValueOrDefault(row) + n;
                var after = Math.Max(0, used[row] - onHand);
                if (after > before)
                    needs.Add(new GranaryNeed(row, after - before, Math.Max(0, day.Day - input.FirstDay + dueOffset), source));
            }
        }

        return needs;
    }

    /// <summary>
    /// Materials a planned day uses across the input's workshops, the way the planner charges them: the first craft of
    /// a chain once, an efficient craft twice when <see cref="PlannerInput.MaterialsPerItem"/>.
    /// </summary>
    public static Dictionary<int, int> MaterialsUsed(PlannedDay day, PlannerInput input)
    {
        var used = new Dictionary<int, int>();
        if (day.Rest)
            return used;

        var workshops = input.WorkshopTiers.Count(t => t > 0);
        var fourth = day.Fourth == null ? 0 : workshops - day.MainCount;
        Charge(day.Main, day.Fourth == null ? workshops : day.MainCount);
        Charge(day.Fourth, fourth);
        return used;

        void Charge(int[]? chain, int count)
        {
            if (chain == null || count <= 0)
                return;
            CraftDef? previous = null;
            foreach (var id in chain)
            {
                if (input.Catalog.Get(id) is not { } craft)
                    continue;
                var units = input.MaterialsPerItem && previous != null && craft.ChainsAfter(previous) ? 2 : 1;
                foreach (var m in craft.Materials)
                    used[m.PouchRow] = used.GetValueOrDefault(m.PouchRow) + m.Amount * units * count;
                previous = craft;
            }
        }
    }

    /// <summary>Construction costs set aside (rank mode) that the pouch does not cover.</summary>
    public static List<GranaryNeed> FromReservations(IReadOnlyDictionary<int, int> onHand, IEnumerable<Construction> reserved, int dueInDays)
    {
        var cost = new Dictionary<int, int>();
        foreach (var need in reserved.SelectMany(c => c.Cost))
            cost[need.PouchRow] = cost.GetValueOrDefault(need.PouchRow) + need.Amount;

        return cost
            .Select(kv => (Row: kv.Key, Short: kv.Value - onHand.GetValueOrDefault(kv.Key)))
            .Where(x => x.Short > 0)
            .OrderBy(x => x.Row)
            .Select(x => new GranaryNeed(x.Row, x.Short, dueInDays, NeedSource.Construction))
            .ToList();
    }

    /// <summary>The plan's <see cref="PlanResult.GatherDemand"/> on the rows gathering is slow to bring.</summary>
    public static List<GranaryNeed> FromGatherDemand(IReadOnlyDictionary<int, int> gatherDemand, IReadOnlySet<int> slowRows, int dueInDays) =>
        gatherDemand
            .Where(kv => kv.Value > 0 && slowRows.Contains(kv.Key))
            .OrderBy(kv => kv.Key)
            .Select(kv => new GranaryNeed(kv.Key, kv.Value, dueInDays, NeedSource.SlowGather))
            .ToList();
}
