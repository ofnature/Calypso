using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Calypso.Core.Granary;
using Calypso.Core.Workshop;

namespace Calypso.Core.Farm;

/// <summary>
/// The workshop's need for produce and leavings, as <see cref="GranaryNeed"/>s — the same record the granary planner
/// takes, so the daily loop builds one need list (rares, produce, leavings) and hands it to all three planners; each
/// one only looks at the rows it can supply. Construction reservations use <see cref="GranaryNeeds.FromReservations"/>
/// unchanged.
/// </summary>
public static class FarmNeeds
{
    /// <summary>What the uncapped plan is offered of every produce and leaving: the pouch cap, so only value limits it.</summary>
    private const int Unlimited = 999;

    /// <summary>
    /// The produce and leavings a week's workshop plan would use and the pouch cannot cover. <see cref="WeekPlanner"/>
    /// treats both as hard caps, so its own plan is never short — it leaves those crafts out. Planning the same week
    /// again with both plentiful (granary rares stay as they are) shows what the workshop would make of them; what that
    /// plan uses beyond the pouch is the need, due before the day it is crafted. Same method as
    /// <see cref="GranaryNeeds.FromWorkshop"/>, on the other two hard-capped kinds.
    ///
    /// For next week pass that week's input (FirstDay 0), <see cref="NeedSource.WorkshopNextWeek"/> and
    /// <paramref name="dueOffset"/> = 7 − this week's FirstDay.
    /// </summary>
    public static List<GranaryNeed> FromWorkshop(PlannerInput input, NeedSource source = NeedSource.WorkshopThisWeek, int dueOffset = 0,
        CancellationToken cancel = default)
    {
        var rows = Rows(input.Catalog).ToList();
        var lifted = input.Materials.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var row in rows)
            lifted[row] = Unlimited;

        var plan = WeekPlanner.Plan(input with { Materials = lifted }, cancel);
        return GranaryNeeds.Shortfall(plan, input, rows, source, dueOffset);
    }

    /// <summary>Produce (MJIItemCategory 4) and leavings (5) pouch rows, in row order.</summary>
    public static IEnumerable<int> Rows(CraftCatalog catalog) =>
        catalog.Pouch.Values.Where(p => p.Kind is MaterialKind.Produce or MaterialKind.Leavings).Select(p => p.Row).OrderBy(r => r);

    /// <summary>
    /// The row and source a change adds the most weighted units to, summed over that row's needs from that source — so
    /// a plot feeding several days of one produce says so once. Short = all of that row's need from that source.
    /// </summary>
    internal static (int Row, NeedSource Source, double Units, int Short)? Strongest(
        Dictionary<GranaryNeed, double> with, Dictionary<GranaryNeed, double> without, Func<NeedSource, double> weight)
    {
        var best = with
            .Select(kv => (Need: kv.Key, Units: kv.Value - without.GetValueOrDefault(kv.Key)))
            .Where(x => x.Units > 1e-6)
            .GroupBy(x => (x.Need.PouchRow, x.Need.Source))
            .Select(g => (g.Key.PouchRow, g.Key.Source, Units: g.Sum(x => x.Units),
                Short: with.Keys.Where(n => n.PouchRow == g.Key.PouchRow && n.Source == g.Key.Source).Sum(n => n.Amount)))
            .OrderByDescending(x => x.Units * weight(x.Source))
            .ThenBy(x => x.PouchRow)
            .ThenBy(x => x.Source)
            .ToList();
        return best.Count == 0 ? null : best[0];
    }
}
