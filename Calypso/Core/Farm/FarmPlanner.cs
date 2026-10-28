using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Granary;

namespace Calypso.Core.Farm;

/// <summary>
/// Decides, for each cropland plot, whether to collect and what it should grow — to feed the workshop's hard-capped
/// produce first (see <see cref="NeedSource"/> for the order), then construction, then a stock floor of every produce.
/// Pure and deterministic: the daily loop calls it once a day after the reset and carries out each decision in order.
///
/// Every plot it plants goes under the gardener's care (Entrust is the one planting call), which harvests every
/// <see cref="FarmRules.GrowDays"/> and replants with a new seed from the pouch — so a plot's harvests are limited by
/// the seeds held, shared with every other plot growing the same crop.
///
/// Empty plots are filled one at a time in slot order, each with the crop whose harvests cover the most still-uncovered
/// need (weighted by source, by each need's due day), so plots spread across needs and only pile onto one produce while
/// it is still the biggest gap. A plot already growing under care changes crop only when another one covers more by
/// <see cref="FarmRules.ReplantThreshold"/> — its growth so far is counted, so a nearly ripe crop is rarely pulled.
/// Ties go to the produce with the least on hand, then the seed with the most on hand, then the lowest crop id.
/// </summary>
public static class FarmPlanner
{
    private const double Epsilon = 1e-6;

    public static FarmPlan Plan(FarmInput input)
    {
        var rules = input.Rules;
        var crops = input.Crops.ToDictionary(c => c.Id);
        var stock = input.OnHand.ToDictionary(kv => kv.Key, kv => kv.Value);
        var slots = rules.Slots(input.Level);
        var usable = input.Level > 0 && !input.UnderConstruction;
        var states = input.Plots.OrderBy(p => p.Slot).Select(p => new PlotState(p)).ToList();

        foreach (var s in states)
            Classify(s, input, crops, slots, usable);

        // 1. Collection, in slot order: each plot sees the room the plots before it used.
        foreach (var s in states.Where(s => s.Kind != Kind.Off))
            DecideCollect(s, input, crops, stock);

        var ctx = new Context(input, crops, AllNeeds(input, crops), states);

        // 2. Empty plots, one at a time, each with what the plots before it left uncovered. Today's fees for the plots
        // already under care come first: the game takes them at the reset whatever the planner says.
        var budget = input.Cowries - states.Count(s => s.Kind == Kind.Keep) * rules.CareFee;
        foreach (var s in states.Where(s => s.Kind == Kind.Free))
            PlantFree(s, ctx, ref budget);

        // 3. Growing plots: replant when another crop covers clearly more, dismiss what nothing wants and would overflow.
        foreach (var s in states.Where(s => s.Kind == Kind.Keep))
            Reconsider(s, ctx);

        if (budget < 0 && states.FirstOrDefault(s => s.Kind == Kind.Keep) is { } first)
        {
            first.Warnings.Add($"{input.Cowries} cowries do not cover {states.Count(s => s.Kind == Kind.Keep)} plots' care " +
                               $"({rules.CareFee} each a day); care halts where the fee goes unpaid");
        }

        // 4. Decisions and their reasons.
        var expected = Expected(ctx);
        var decisions = states.Select(s => Decide(s, ctx, expected)).ToList();
        var waiting = states.Where(s => s.Kind != Kind.Off && s.Plot.Waiting > 0).ToList();
        var collectAll = waiting.Count > 0 && waiting.All(s => s.Collect && s.Discarded.Count == 0);
        var warnings = decisions.SelectMany(d => d.Warnings.Select(w => $"Plot {d.Slot + 1}: {w}")).ToList();
        return new FarmPlan(decisions, collectAll, decisions.Sum(d => d.CowrieCost), warnings);
    }

    private static void Classify(PlotState s, FarmInput input, Dictionary<int, CropSeed> crops, int slots, bool usable)
    {
        var p = s.Plot;
        if (!usable)
            s.Kind = Kind.Off;
        else if (p.Slot >= slots)
        {
            s.Kind = Kind.Off;
            s.Warnings.Add($"beyond the {slots} plots a level {input.Level} cropland has (FarmRules.SlotsByLevel)");
        }
        else if (p.Crop is { } c && !crops.ContainsKey(c))
        {
            s.Kind = Kind.Foreign;
            s.Warnings.Add($"unknown crop {c}");
        }
        else if (p.Crop == null)
            s.Kind = Kind.Free;
        else
            s.Kind = p.UnderCare || p.CareHalted ? Kind.Keep : Kind.Foreign;

        s.Crop = s.Kind is Kind.Keep or Kind.Foreign ? p.Crop : null;
        // Game text: halted care resumes by itself once the fee, a seed and room in the plot are there again.
        if (p.CareHalted && s.Kind == Kind.Keep)
            s.Warnings.Add("care halted (cowries, seeds or a full plot); it resumes by itself once all three are there");
    }

    // ---- collection ----

    private static void DecideCollect(PlotState s, FarmInput input, Dictionary<int, CropSeed> crops, Dictionary<int, int> stock)
    {
        var p = s.Plot;
        if (p.Waiting <= 0 || p.Crop is not { } c || !crops.TryGetValue(c, out var crop))
            return;

        var rules = input.Rules;
        var (collected, discarded) = GranaryPlanner.Collection(new Dictionary<int, int> { [crop.ProducePouchRow] = p.Waiting }, stock, rules.PouchCap);
        if (discarded.Count > 0)
        {
            // Produce waits safely in the plot (game text), so hold it rather than lose it. Once the plot is full the
            // gardener stops — and stops charging — which is all the planner would do with a produce that has no room.
            s.Held = true;
            var full = p.Waiting + rules.YieldPerHarvest >= rules.PlotCap ? $"; the plot holds {p.Waiting} of {rules.PlotCap}, so care halts at its next harvest" : "";
            s.Warnings.Add($"held: collecting now would discard {List(input, crops, discarded)} (pouch cap {rules.PouchCap}); collect once the pouch has room{full}");
            return;
        }

        s.Collect = true;
        s.Collected = collected;
        foreach (var (row, n) in collected)
            stock[row] = stock.GetValueOrDefault(row) + n;
    }

    // ---- needs ----

    /// <summary>The caller's needs on produce rows plus the planner's stock floor for every produce.</summary>
    private static List<GranaryNeed> AllNeeds(FarmInput input, Dictionary<int, CropSeed> crops)
    {
        var produce = crops.Values.Select(c => c.ProducePouchRow).ToHashSet();
        var needs = input.Needs.Where(n => n.Amount > 0 && produce.Contains(n.PouchRow)).ToList();
        foreach (var row in produce.OrderBy(r => r))
        {
            var short_ = input.Rules.ProduceFloor - input.OnHand.GetValueOrDefault(row);
            if (short_ > 0)
                needs.Add(new GranaryNeed(row, short_, input.Rules.HorizonDays, NeedSource.StockFloor));
        }

        return needs;
    }

    // ---- choosing crops ----

    private static void PlantFree(PlotState s, Context ctx, ref int budget)
    {
        var rules = ctx.Input.Rules;
        var fee = s.Plot.WasUnderCare ? 0 : rules.CareFee;
        var (best, gain) = BestCrop(s, ctx, HasSeed(ctx));
        if (best == null || gain <= Epsilon)
        {
            // Would more seeds have helped? Say which.
            CropSeed? wanted;
            double wantedGain;
            using (MoreSeeds(ctx))
                (wanted, wantedGain) = BestCrop(s, ctx, _ => true);
            var held = wanted == null ? 0 : ctx.Seeds.GetValueOrDefault(wanted.SeedPouchRow);
            s.Why = wanted != null && wantedGain > Epsilon
                ? $"{(held <= 0 ? "no" : $"only {held}")} {Name(ctx, wanted.SeedPouchRow)} to plant (gather or buy more): it would cover {Serves(s, ctx, wanted.Id, true)}"
                : "nothing is short";
            return;
        }

        if (budget < fee)
        {
            s.Why = $"not enough cowries for another plot's care ({rules.CareFee} a day)";
            return;
        }

        budget -= fee;
        Commit(s, ctx, Kind.Plant, best);
    }

    private static void Reconsider(PlotState s, Context ctx)
    {
        var rules = ctx.Input.Rules;
        var current = s.Crop!.Value;
        var keepGain = Gain(s, ctx, current, asNew: false);
        var (best, bestGain) = BestCrop(s, ctx, c => c.Id != current && HasSeed(ctx)(c));

        if (best != null && bestGain - keepGain > rules.ReplantThreshold)
        {
            if (s.Held)
            {
                s.Warnings.Add($"would replant with {best.Name}, but its produce cannot be collected without loss yet");
                return;
            }

            s.Previous = current;
            Commit(s, ctx, Kind.Replant, best);
            s.Collect = s.Plot.Waiting > 0;
            return;
        }

        // Nothing wants it, and what it grows would only be thrown away: stop paying for it.
        var row = ctx.Crops[current].ProducePouchRow;
        var onHand = ctx.Input.OnHand.GetValueOrDefault(row) + s.Plot.Waiting;
        var next = Grown(row, rules.HorizonDays, ctx)[rules.HorizonDays];
        if (keepGain <= Epsilon && !s.Held && onHand + next > rules.PouchCap)
        {
            s.Previous = current;
            s.Kind = Kind.Dismiss;
            s.Crop = null;
            s.Collect = s.Plot.Waiting > 0;
        }
    }

    private static Func<CropSeed, bool> HasSeed(Context ctx) =>
        c => ctx.Seeds.GetValueOrDefault(c.SeedPouchRow) >= ctx.Input.Rules.SeedsPerPlanting;

    /// <summary>A what-if with a week of plantings more of every seed (one every <see cref="FarmRules.GrowDays"/> at most).</summary>
    private static IDisposable MoreSeeds(Context ctx)
    {
        var saved = ctx.Seeds.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var crop in ctx.Crops.Values)
            ctx.Seeds[crop.SeedPouchRow] = Math.Max(0, ctx.Seeds.GetValueOrDefault(crop.SeedPouchRow)) + ctx.Input.Rules.HorizonDays * ctx.Input.Rules.SeedsPerPlanting;
        return new Restore(() =>
        {
            ctx.Seeds.Clear();
            foreach (var (row, n) in saved)
                ctx.Seeds[row] = n;
        });
    }

    private static void Commit(PlotState s, Context ctx, Kind kind, CropSeed crop)
    {
        s.Kind = kind;
        s.Crop = crop.Id;
        s.SeedPaid = true;
        ctx.Seeds[crop.SeedPouchRow] -= ctx.Input.Rules.SeedsPerPlanting;
        // Said now, against the plots chosen so far: a later plot on the same produce does not take this one's reason.
        s.Serving = Serves(s, ctx, crop.Id, false);
    }

    /// <summary>The crop (allowed by <paramref name="can"/>) whose new planting on this plot covers the most.</summary>
    private static (CropSeed? Crop, double Gain) BestCrop(PlotState s, Context ctx, Func<CropSeed, bool> can)
    {
        CropSeed? best = null;
        var bestGain = 0.0;
        foreach (var crop in ctx.Crops.Values.Where(can).OrderBy(c => c.Id))
        {
            var gain = Gain(s, ctx, crop.Id, asNew: true);
            if (best == null || gain > bestGain + Epsilon || (Math.Abs(gain - bestGain) <= Epsilon && Tie(crop, best, ctx)))
            {
                best = crop;
                bestGain = gain;
            }
        }

        return (best, bestGain);
    }

    /// <summary>Equal gain: the produce with less on hand, then the seed with more on hand (lowest id already wins otherwise).</summary>
    private static bool Tie(CropSeed a, CropSeed b, Context ctx)
    {
        var onHand = ctx.Input.OnHand;
        var pa = onHand.GetValueOrDefault(a.ProducePouchRow);
        var pb = onHand.GetValueOrDefault(b.ProducePouchRow);
        if (pa != pb)
            return pa < pb;
        return onHand.GetValueOrDefault(a.SeedPouchRow) > onHand.GetValueOrDefault(b.SeedPouchRow);
    }

    /// <summary>
    /// Score this plot adds by growing <paramref name="crop"/> — newly planted today (<paramref name="asNew"/>), or
    /// going on from its growth level — with the other plots as they stand.
    /// </summary>
    private static double Gain(PlotState s, Context ctx, int crop, bool asNew)
    {
        using var _ = s.Try(null, s.Kind, false);
        var without = Coverage(ctx, out var __);
        using var ___ = s.Try(crop, asNew ? Kind.Plant : Kind.Keep, false);
        return Coverage(ctx, out __) - without;
    }

    // ---- supply ----

    private sealed class Context
    {
        public Context(FarmInput input, Dictionary<int, CropSeed> crops, List<GranaryNeed> needs, List<PlotState> states)
        {
            Input = input;
            Crops = crops;
            ByProduce = crops.Values.GroupBy(c => c.ProducePouchRow).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).First());
            States = states;
            Seeds = input.OnHand.ToDictionary(kv => kv.Key, kv => kv.Value);
            // Strongest need first, then the soonest: supply goes to needs in this order.
            Needs = needs
                .Select((n, i) => (n, i))
                .OrderBy(x => x.n.PouchRow)
                .ThenByDescending(x => input.Rules.Weight(x.n.Source))
                .ThenBy(x => x.n.DueInDays)
                .ThenBy(x => x.i)
                .Select(x => x.n)
                .GroupBy(n => n.PouchRow)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        public FarmInput Input { get; }
        public Dictionary<int, CropSeed> Crops { get; }
        public Dictionary<int, CropSeed> ByProduce { get; }
        public List<PlotState> States { get; }

        /// <summary>Seeds left in the pouch after today's plantings (replants are drawn from it as the days are simulated).</summary>
        public Dictionary<int, int> Seeds { get; }

        public Dictionary<int, List<GranaryNeed>> Needs { get; }
    }

    /// <summary>
    /// Score of the needs the plots as they stand cover: each need takes what has arrived by its due day and what the
    /// stronger needs on the same row left over (the granary planner's rule). <paramref name="taken"/> = units per need.
    /// </summary>
    private static double Coverage(Context ctx, out Dictionary<GranaryNeed, double> taken)
    {
        var rules = ctx.Input.Rules;
        taken = new Dictionary<GranaryNeed, double>(ReferenceEqualityComparer.Instance);
        var score = 0.0;
        foreach (var (row, needs) in ctx.Needs)
        {
            var grown = Grown(row, needs.Max(n => n.DueInDays), ctx);
            var held = Held(row, ctx);
            var used = 0.0;
            foreach (var need in needs)
            {
                var supply = held + grown[need.DueInDays];
                var take = Math.Clamp(Math.Min(need.Amount, supply - used), 0, need.Amount);
                taken[need] = take;
                used += take;
                score += take * rules.Weight(need.Source);
            }
        }

        return score;
    }

    /// <summary>What already waits counts from today: collected, or (held) collectable once there is room.</summary>
    private static int Held(int row, Context ctx)
    {
        var held = 0;
        foreach (var s in ctx.States)
        {
            if (s.Kind != Kind.Off && s.Plot.Crop is { } c && ctx.Crops.TryGetValue(c, out var crop) && crop.ProducePouchRow == row)
                held += s.Plot.Waiting;
        }

        return held;
    }

    /// <summary>
    /// Produce of <paramref name="row"/> the gardener harvests after each of 0..<paramref name="days"/> resets
    /// (cumulative), replanting from the shared seed pool after every harvest; a plot stops when its seed runs out or
    /// it is full. Only the gardener's plots are counted: one tended by hand is the player's to harvest.
    /// <paramref name="perPlot"/> and <paramref name="stopped"/> (reset a plot stopped on) are filled when given.
    /// </summary>
    private static int[] Grown(int row, int days, Context ctx, Dictionary<PlotState, int>? perPlot = null, Dictionary<PlotState, int>? stopped = null)
    {
        var rules = ctx.Input.Rules;
        var cumulative = new int[days + 1];
        if (!ctx.ByProduce.TryGetValue(row, out var crop))
            return cumulative;

        var pool = ctx.Seeds.GetValueOrDefault(crop.SeedPouchRow);
        var plots = new List<(PlotState S, int Next, int Stored)>();
        foreach (var s in ctx.States)
        {
            if (s.Kind is not (Kind.Keep or Kind.Plant or Kind.Replant) || s.Crop != crop.Id)
                continue;
            if (s.Kind != Kind.Keep && !s.SeedPaid)
            {
                // A planting being scored, not yet taken from the pool.
                if (pool < rules.SeedsPerPlanting)
                {
                    stopped?.TryAdd(s, 0);
                    continue;
                }

                pool -= rules.SeedsPerPlanting;
            }

            plots.Add((s, s.DaysToFirst(rules), s.Held ? s.Plot.Waiting : 0));
        }

        var total = 0;
        for (var day = 1; day <= days; day++)
        {
            for (var i = 0; i < plots.Count; i++)
            {
                var (s, next, stored) = plots[i];
                if (next != day)
                    continue;
                if (stored >= rules.PlotCap)
                {
                    stopped?.TryAdd(s, day);
                    plots[i] = (s, -1, stored);
                    continue;
                }

                total += rules.YieldPerHarvest;
                if (perPlot != null)
                    perPlot[s] = perPlot.GetValueOrDefault(s) + rules.YieldPerHarvest;
                // Collected daily unless held: only a held plot fills up.
                stored = s.Held ? stored + rules.YieldPerHarvest : 0;
                if (pool >= rules.SeedsPerPlanting)
                {
                    pool -= rules.SeedsPerPlanting;
                    plots[i] = (s, next + rules.GrowDays, stored);
                }
                else
                {
                    stopped?.TryAdd(s, day);
                    plots[i] = (s, -1, stored);
                }
            }

            cumulative[day] = total;
        }

        return cumulative;
    }

    /// <summary>Each planted plot's harvests over the horizon, and the reset its care stops on (seeds out / plot full).</summary>
    private static (Dictionary<PlotState, int> PerPlot, Dictionary<PlotState, int> Stopped) Expected(Context ctx)
    {
        var perPlot = new Dictionary<PlotState, int>();
        var stopped = new Dictionary<PlotState, int>();
        foreach (var row in ctx.ByProduce.Keys.OrderBy(r => r))
            Grown(row, ctx.Input.Rules.HorizonDays, ctx, perPlot, stopped);
        return (perPlot, stopped);
    }

    // ---- decisions ----

    private static PlotDecision Decide(PlotState s, Context ctx, (Dictionary<PlotState, int> PerPlot, Dictionary<PlotState, int> Stopped) expected)
    {
        var input = ctx.Input;
        var rules = input.Rules;
        var p = s.Plot;
        var empty = new Dictionary<int, int>();

        if (input.Level <= 0)
            return Nothing("not built");
        if (input.UnderConstruction)
            return Nothing("under construction");
        if (s.Kind == Kind.Off)
            return Nothing("not a plot at this cropland level");

        var action = s.Kind switch
        {
            Kind.Plant => FarmAction.Plant,
            Kind.Replant => FarmAction.Replant,
            Kind.Dismiss => FarmAction.Dismiss,
            Kind.Keep => FarmAction.KeepUnderCare,
            _ => FarmAction.Nothing,
        };

        // Replant / dismiss collect on the way out (CollectSingleAndDismiss): DecideCollect already made the room.
        var crop = s.Crop is { } c ? ctx.Crops[c] : null;
        var planted = action is FarmAction.Plant or FarmAction.Replant;
        var underCare = planted || action == FarmAction.KeepUnderCare;
        var cost = underCare && !(planted && p.WasUnderCare) ? rules.CareFee : 0;
        var grown = expected.PerPlot.GetValueOrDefault(s);
        var produce = underCare && crop != null && grown > 0 ? new Dictionary<int, int> { [crop.ProducePouchRow] = grown } : empty;

        if (underCare && crop != null && expected.Stopped.TryGetValue(s, out var day) && !s.Held)
        {
            s.Warnings.Add(day == 0
                ? $"no {Name(ctx, crop.SeedPouchRow)} left for this plot"
                : $"{Name(ctx, crop.SeedPouchRow)} runs out: care halts after the harvest in {day} day{(day == 1 ? "" : "s")} (gather or buy more)");
        }

        var reason = Reason(s, action, ctx);
        if (s.Collect)
            reason = $"collect {List(input, ctx.Crops, s.Collected)}; {reason}";
        return new PlotDecision(p.Slot, s.Collect, action, planted ? crop?.Id : null, planted ? crop?.SeedItemId : null, cost,
            s.Collected, s.Discarded, produce, s.Warnings, reason);

        PlotDecision Nothing(string why) => new(p.Slot, false, FarmAction.Nothing, null, null, 0, empty, empty, empty, s.Warnings, why);
    }

    /// <summary>One line: what this plot's crop covers that it would not without it.</summary>
    private static string Reason(PlotState s, FarmAction action, Context ctx)
    {
        var rules = ctx.Input.Rules;
        string CropName(int? id) => id is { } i && ctx.Crops.TryGetValue(i, out var c) ? c.Name : "?";
        var days = s.DaysToFirst(rules);
        var when = $"first harvest in {days} day{(days == 1 ? "" : "s")}";

        return action switch
        {
            FarmAction.Plant => $"plant {CropName(s.Crop)} ({when}): {s.Serving}",
            FarmAction.Replant => $"replant {CropName(s.Previous)} with {CropName(s.Crop)} ({when}): {s.Serving}",
            FarmAction.Dismiss => $"dismiss {CropName(s.Previous)}: nothing needs it and its harvests would overflow the pouch",
            FarmAction.KeepUnderCare => $"keep {CropName(s.Crop)} under care ({when}, {rules.CareFee} cowries a day): {Serves(s, ctx, s.Crop!.Value, false)}",
            _ when s.Kind == Kind.Foreign => $"{CropName(s.Crop)} is not under the gardener's care: left to the player",
            _ => $"stays empty: {s.Why}",
        };
    }

    /// <summary>
    /// What growing <paramref name="crop"/> on this plot covers, as the reasons say it: the need it adds the most weighted
    /// units to. <paramref name="asNew"/> scores a planting not made (no seed held).
    /// </summary>
    private static string Serves(PlotState s, Context ctx, int crop, bool asNew)
    {
        var input = ctx.Input;
        Dictionary<GranaryNeed, double> without, with;
        using (s.Try(null, s.Kind, s.SeedPaid))
            Coverage(ctx, out without);
        if (asNew)
        {
            // Pretend the seeds are there, so the reason can say what they would bring.
            using (MoreSeeds(ctx))
            using (s.Try(crop, Kind.Plant, false))
                Coverage(ctx, out with);
        }
        else
        {
            Coverage(ctx, out with);
        }

        return FarmNeeds.Strongest(with, without, input.Rules.Weight) is { } gain
            ? $"{gain.Units:0} {Name(ctx, gain.Row)} toward {Describe(gain.Source)} (short {gain.Short})"
            : "nothing is short, so it keeps the stock growing";
    }

    private static string Describe(NeedSource source) => source switch
    {
        NeedSource.WorkshopThisWeek => "this week's workshop",
        NeedSource.WorkshopNextWeek => "next week's workshop",
        NeedSource.Construction => "construction",
        NeedSource.SlowGather => "slow gathering",
        NeedSource.StockFloor => "the stock floor",
        _ => source.ToString(),
    };

    private static string Name(Context ctx, int row) => Name(ctx.Input, ctx.Crops, row);

    private static string Name(FarmInput input, Dictionary<int, CropSeed> crops, int row) =>
        input.MaterialNames != null && input.MaterialNames.TryGetValue(row, out var n) ? n
        : crops.Values.FirstOrDefault(c => c.ProducePouchRow == row)?.Name ?? $"pouch {row}";

    private static string List(FarmInput input, Dictionary<int, CropSeed> crops, IReadOnlyDictionary<int, int> amounts) =>
        string.Join(", ", amounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {Name(input, crops, kv.Key)}"));

    private enum Kind
    {
        /// <summary>Not usable: cropland not built / under construction, or a slot beyond its level.</summary>
        Off,

        /// <summary>Empty, or the gardener's care halted: free to plant.</summary>
        Free,

        /// <summary>Growing a crop without the gardener: the player's; the planner only collects what waits.</summary>
        Foreign,

        Keep,
        Plant,
        Replant,
        Dismiss,
    }

    private sealed class PlotState
    {
        public PlotState(CropPlot plot) => Plot = plot;

        public CropPlot Plot { get; }
        public Kind Kind { get; set; }

        /// <summary>The crop the plot grows after today's decision (null = empty).</summary>
        public int? Crop { get; set; }

        /// <summary>The crop a replant / dismiss removes.</summary>
        public int? Previous { get; set; }

        /// <summary>Today's planting seed is already taken from <see cref="Context.Seeds"/>.</summary>
        public bool SeedPaid { get; set; }

        public bool Collect { get; set; }

        /// <summary>Produce waits that cannot be collected without discarding some.</summary>
        public bool Held { get; set; }

        public string Why { get; set; } = "";

        /// <summary>What a plant / replant covered when it was chosen.</summary>
        public string Serving { get; set; } = "";

        public Dictionary<int, int> Collected { get; set; } = new();

        /// <summary>Always empty: the planner holds produce rather than discard it (kept for the decision's shape).</summary>
        public Dictionary<int, int> Discarded { get; } = new();

        public List<string> Warnings { get; } = new();

        /// <summary>A new planting starts from nothing; a crop kept from before goes on from its growth level.</summary>
        public int DaysToFirst(FarmRules rules) =>
            Kind is Kind.Plant or Kind.Replant ? rules.GrowDays : rules.DaysToHarvest(Plot.GrowthLevel);

        /// <summary>Score a what-if: this plot as <paramref name="crop"/> / <paramref name="kind"/> until disposed.</summary>
        public IDisposable Try(int? crop, Kind kind, bool seedPaid)
        {
            var saved = (Crop, Kind, SeedPaid);
            (Crop, Kind, SeedPaid) = (crop, kind, seedPaid);
            return new Restore(() => (Crop, Kind, SeedPaid) = saved);
        }
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
