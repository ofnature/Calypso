using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Workshop;

public enum WorkshopMode
{
    /// <summary>Calypso leaves the workshop alone.</summary>
    Off,

    /// <summary>Plans every day and shows it; never touches the in-game agenda.</summary>
    Suggest,

    /// <summary>Plans and sets tomorrow's agenda (and rest days) in game.</summary>
    Auto,
}

public enum WorkshopObjective
{
    /// <summary>Maximise Seafarer's Cowries this week.</summary>
    Cowries,

    /// <summary>Maximise cowries without touching materials reserved for the constructions currently unlocked.</summary>
    RankProgress,
}

/// <summary>Per-character workshop settings (lives in the character's profile).</summary>
public sealed class WorkshopSettings
{
    /// <summary>Suggest by default: nothing is written into a character's agenda until someone turns Auto on.</summary>
    public WorkshopMode Mode { get; set; } = WorkshopMode.Suggest;

    public WorkshopObjective Objective { get; set; } = WorkshopObjective.Cowries;

    /// <summary>Planner score per gatherable unit it would have to gather. Higher = prefer what is on hand.</summary>
    public double GatherCostPerUnit { get; set; } = 1.0;

    /// <summary>Week index (<see cref="IslandCycle.Week"/>) whose favors to ignore; -1 = none.</summary>
    public long SkipFavorsWeek { get; set; } = -1;
}

/// <summary>
/// One week of workshop facts for one character: what was observed each day, what Calypso scheduled, and the
/// current plan. Wiped when the week changes (<see cref="Roll"/>).
/// </summary>
public sealed class WorkshopWeek
{
    public long Week { get; set; } = long.MinValue;

    /// <summary>Day (0-6) → the 91 packed supply/demand bytes seen that day.</summary>
    public Dictionary<int, byte[]> Observed { get; set; } = new();

    /// <summary>Day (0-6) → items per craft id that this character's schedule makes that day (what Calypso committed or read).</summary>
    public Dictionary<int, int[]> Produced { get; set; } = new();

    /// <summary>Last groove read from the agenda agent, and when — the planner's start groove when the agent is closed.</summary>
    public int LastGroove { get; set; }

    public WeekPlan? Plan { get; set; }

    /// <summary>A pasted community schedule, kept for comparison.</summary>
    public WeekPlan? Pasted { get; set; }

    public bool Roll(long week)
    {
        if (Week == week)
            return false;

        Week = week;
        Observed.Clear();
        Produced.Clear();
        LastGroove = 0;
        Plan = null;
        Pasted = null;
        return true;
    }

    public byte[]?[] ObservedByDay() => Enumerable.Range(0, 7).Select(d => Observed.GetValueOrDefault(d)).ToArray();

    public int[]?[] ProducedByDay() => Enumerable.Range(0, 7).Select(d => Produced.GetValueOrDefault(d)).ToArray();

    /// <summary>Own production on days before <paramref name="day"/>, by craft id.</summary>
    public int[] ProducedBefore(int day, int maxCraftId)
    {
        var total = new int[maxCraftId + 1];
        foreach (var (d, items) in Produced)
        {
            if (d >= day)
                continue;
            for (var c = 0; c < items.Length && c < total.Length; c++)
                total[c] += items[c];
        }

        return total;
    }
}

/// <summary>A week's agenda as Calypso shows and commits it. Days the plan doesn't cover are absent.</summary>
public sealed class WeekPlan
{
    public long Week { get; set; }

    /// <summary>solver | pasted | manual</summary>
    public string Source { get; set; } = "solver";

    public DateTime CreatedUtc { get; set; }

    /// <summary>The island day (0-6) this plan was made on — the daily re-plan replaces older ones.</summary>
    public int PlannedOnDay { get; set; }

    public double ExpectedValue { get; set; }
    public bool FavorsMet { get; set; } = true;
    public List<CyclePlan> Cycles { get; set; } = new();

    /// <summary>Pouch row → units of gatherable material the plan needs beyond what was on hand.</summary>
    public Dictionary<int, int> GatherDemand { get; set; } = new();

    /// <summary>Pouch row → how many to HAVE (on hand when planned + shortfall) — the gather task's absolute goal, so it
    /// doesn't keep moving as materials come in.</summary>
    public Dictionary<int, int> GatherTargets { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public CyclePlan? Day(int day) => Cycles.FirstOrDefault(c => c.Day == day);

    public static WeekPlan FromResult(PlanResult result, long week, DateTime utc, int plannedOnDay = 0) => new()
    {
        Week = week,
        Source = "solver",
        CreatedUtc = utc,
        PlannedOnDay = plannedOnDay,
        ExpectedValue = result.ExpectedValue,
        FavorsMet = result.FavorsMet,
        Cycles = result.Days.Select(d => new CyclePlan
        {
            Day = d.Day,
            Rest = d.Rest,
            Main = d.Main?.ToList(),
            MainCount = d.MainCount,
            Fourth = d.Fourth?.ToList(),
            Value = d.Value,
            Confidence = d.Confidence,
        }).ToList(),
        GatherDemand = result.GatherDemand.ToDictionary(kv => kv.Key, kv => kv.Value),
        Warnings = result.Warnings.ToList(),
    };
}

public sealed class CyclePlan
{
    public int Day { get; set; }
    public bool Rest { get; set; }

    /// <summary>Chain run in workshops [0, MainCount).</summary>
    public List<int>? Main { get; set; }

    public int MainCount { get; set; }

    /// <summary>Chain run in the remaining workshop(s), or null when every workshop runs <see cref="Main"/>.</summary>
    public List<int>? Fourth { get; set; }

    public double Value { get; set; }
    public double Confidence { get; set; }

    /// <summary>Set once this day was written into the in-game agenda.</summary>
    public bool Committed { get; set; }

    /// <summary>
    /// The chain each of <paramref name="workshopCount"/> workshops runs: the first MainCount get Main, the rest
    /// Fourth. Workshops beyond what the plan covers get nothing.
    /// </summary>
    public IReadOnlyList<int>?[] ChainsFor(int workshopCount)
    {
        var chains = new IReadOnlyList<int>?[workshopCount];
        if (Rest)
            return chains;
        for (var w = 0; w < workshopCount; w++)
            chains[w] = w < MainCount ? Main : Fourth;
        return chains;
    }

    /// <summary>Items per craft id this day makes across the given workshops (first craft 1, efficient crafts 2).</summary>
    public int[] Items(int workshopCount, int maxCraftId)
    {
        var items = new int[maxCraftId + 1];
        foreach (var chain in ChainsFor(workshopCount))
        {
            if (chain == null)
                continue;
            for (var i = 0; i < chain.Count; i++)
            {
                if (chain[i] >= 0 && chain[i] <= maxCraftId)
                    items[chain[i]] += i == 0 ? 1 : 2;
            }
        }

        return items;
    }
}

/// <summary>A construction Calypso can see is unlocked but not done: what it costs, for rank mode's reservation.</summary>
public sealed record Construction(string What, int RankReq, IReadOnlyList<MaterialNeed> Cost);

public static class MaterialReservation
{
    /// <summary>
    /// Rank mode: what is left to plan with once every unlocked construction's cost is set aside.
    /// <paramref name="onHand"/> is not modified.
    /// </summary>
    public static Dictionary<int, int> Available(IReadOnlyDictionary<int, int> onHand, IEnumerable<Construction> reserved)
    {
        var left = onHand.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var need in reserved.SelectMany(c => c.Cost))
            left[need.PouchRow] = Math.Max(0, left.GetValueOrDefault(need.PouchRow) - need.Amount);
        return left;
    }
}
