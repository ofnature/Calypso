using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Granary;

/// <summary>
/// Decides, for each granary, whether to collect and where to send the next expedition for how long — to feed the
/// workshop's hard-capped rares first (see <see cref="NeedSource"/> for the order). Pure and deterministic: the daily
/// loop calls it once a day after the reset, before gathering (so a collection gets the pouch room the gathering
/// routine would otherwise fill), and carries out each decision in order.
///
/// Both granaries are planned together: every combination of their options (at most ~43 each) is scored by how much of
/// each need its deliveries cover by the need's due day, so two granaries only chase one rare when that rare's need is
/// bigger than one granary brings. Ties go to the longest expeditions (fewest trips), then to different areas, then to
/// less overflow, fewer cowries, the scarcer rare and the lowest area id.
/// </summary>
public static class GranaryPlanner
{
    private const double Epsilon = 1e-6;

    public static GranaryPlan Plan(GranaryInput input)
    {
        var areas = input.Areas.ToDictionary(a => a.Id);
        var unlocked = input.Areas.Where(a => input.UnlockedAreas.Contains(a.Id)).Select(a => a.Id).OrderBy(id => id).ToList();
        var stock = input.OnHand.ToDictionary(kv => kv.Key, kv => kv.Value);
        var slots = input.Granaries.OrderBy(g => g.Index).ToList();
        var states = new List<SlotState>();

        // 1. Collection, in granary order: the second granary sees the room the first one used.
        foreach (var slot in slots)
        {
            var s = new SlotState(slot);
            states.Add(s);
            if (!slot.IsUsable())
                continue;
            DecideCollect(s, input, areas, stock);
        }

        var needs = AllNeeds(input, unlocked);
        var context = new Context(input, areas, needs, states);

        // 2. Options per granary, 3. the best combination within the purse.
        var options = states.Select(s => Options(s, input, unlocked)).ToList();
        var best = Search(options, context);

        // 4. Decisions and their reasons.
        var decisions = new List<GranaryDecision>();
        for (var i = 0; i < states.Count; i++)
            decisions.Add(Decide(states[i], best, i, context));

        var warnings = decisions.SelectMany(d => d.Warnings.Select(w => $"Granary {d.Index + 1}: {w}")).ToList();
        return new GranaryPlan(decisions, decisions.Sum(d => d.CowrieCost), warnings);
    }

    /// <summary>
    /// What one collection would add to the pouch and throw away, given <paramref name="stock"/> (pouch rows → count).
    /// The Addon: "Those exceeding the limit will be discarded."
    /// </summary>
    public static (Dictionary<int, int> Collected, Dictionary<int, int> Discarded) Collection(
        IReadOnlyDictionary<int, int> waiting, IReadOnlyDictionary<int, int> stock, int cap)
    {
        var collected = new Dictionary<int, int>();
        var discarded = new Dictionary<int, int>();
        foreach (var (row, n) in waiting.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key))
        {
            var room = Math.Max(0, cap - stock.GetValueOrDefault(row));
            collected[row] = Math.Min(n, room);
            if (n > room)
                discarded[row] = n - room;
        }

        return (collected, discarded);
    }

    // ---- collection ----

    private static void DecideCollect(SlotState s, GranaryInput input, Dictionary<int, ExpeditionArea> areas, Dictionary<int, int> stock)
    {
        var rules = input.Rules;
        var slot = s.Slot;
        var waiting = slot.Waiting.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (waiting.Count == 0)
            return;

        var (collected, discarded) = Collection(waiting, stock, rules.PouchCap);
        // Extend (and, assumed, Change) need everything claimed; a collection that loses nothing can always be added.
        s.Claimable = rules.CollectAnyTime && discarded.Count == 0;
        string? why = null;

        if (!slot.Away)
            why = "the expedition is home";
        else if (!rules.CollectAnyTime)
            s.Warnings.Add("materials wait until the expedition returns (collection mid-expedition is off)");
        else if (input.Needs.FirstOrDefault(n => n.Amount > 0 && n.Source <= NeedSource.Construction && waiting.ContainsKey(n.PouchRow)) is { } need)
            why = $"{Name(input, need.PouchRow)} is needed ({Describe(need.Source)})";
        else if (discarded.Count == 0 && NextPayoutOverflows(slot, waiting, input, areas, stock) is { } row)
            why = $"collect while it fits: the next payout could take {Name(input, row)} past {rules.PouchCap}";
        else if (discarded.Count > 0)
            s.Warnings.Add($"held: collecting now would discard {List(input, discarded)}; collect once the pouch has room");

        if (why == null)
            return;

        s.Collect = true;
        s.CollectWhy = why;
        s.Collected = collected;
        s.Discarded = discarded;
        foreach (var (row, n) in collected)
            stock[row] = stock.GetValueOrDefault(row) + n;
        if (discarded.Count > 0)
            s.Warnings.Add($"collecting discards {List(input, discarded)} (pouch cap {rules.PouchCap})");
    }

    /// <summary>The first row (lowest id) a worst-case payout tomorrow would take past the cap once collected.</summary>
    private static int? NextPayoutOverflows(GranarySlot slot, Dictionary<int, int> waiting, GranaryInput input,
        Dictionary<int, ExpeditionArea> areas, Dictionary<int, int> stock)
    {
        if (slot.Area is not { } id || !areas.TryGetValue(id, out var area))
            return null;
        foreach (var row in area.NormalPouchRows.Prepend(area.RarePouchRow).Distinct().OrderBy(r => r))
        {
            var payout = row == area.RarePouchRow ? input.Rules.RarePerDay(slot.Level) : input.Rules.NormalMaxPerDay;
            if (stock.GetValueOrDefault(row) + waiting.GetValueOrDefault(row) + payout > input.Rules.PouchCap)
                return row;
        }

        return null;
    }

    // ---- needs ----

    /// <summary>The caller's needs plus the planner's stock floor for every rare an unlocked area brings.</summary>
    private static List<GranaryNeed> AllNeeds(GranaryInput input, List<int> unlocked)
    {
        var needs = input.Needs.Where(n => n.Amount > 0).ToList();
        foreach (var rare in unlocked.Select(id => input.Areas.First(a => a.Id == id).RarePouchRow).Distinct().OrderBy(r => r))
        {
            var short_ = input.Rules.RareFloor - input.OnHand.GetValueOrDefault(rare);
            if (short_ > 0)
                needs.Add(new GranaryNeed(rare, short_, input.Rules.MaxDays, NeedSource.StockFloor));
        }

        return needs;
    }

    // ---- options ----

    private enum Kind
    {
        Nothing,
        Dispatch,
        Extend,
        Change,
    }

    /// <summary>
    /// One thing a granary could do today, and what it would bring: <see cref="DeliverDays"/> payouts from
    /// <see cref="DeliverArea"/>. <see cref="Cost"/> is spent today; <see cref="Price"/> is what the choice costs in
    /// all — a dispatch after tomorrow's return is paid tomorrow, but it is no cheaper than extending today.
    /// </summary>
    private sealed record Option(Kind Kind, int? Area, int Days, int Cost, int? DeliverArea, int DeliverDays, bool Commits)
    {
        public int Price { get; init; } = Cost;
    }

    private static List<Option> Options(SlotState s, GranaryInput input, List<int> unlocked)
    {
        var rules = input.Rules;
        var slot = s.Slot;
        if (!slot.IsUsable())
            return new List<Option> { new(Kind.Nothing, null, 0, 0, null, 0, false) };

        var claimed = !slot.Waiting.Any(kv => kv.Value > 0) || s.Collect || s.Claimable;

        // Away and staying out: carry on, or change area for the days left.
        if (slot.Away && (slot.DaysRemaining > rules.ExtendWhenDaysLeft || !claimed))
        {
            var keep = new List<Option> { new(Kind.Nothing, null, 0, 0, slot.Area, slot.DaysRemaining, false) };
            if (claimed)
            {
                keep.AddRange(unlocked.Where(a => a != slot.Area)
                    .Select(a => new Option(Kind.Change, a, slot.DaysRemaining, rules.ChangeCost, a, slot.DaysRemaining, true)));
            }

            return keep;
        }

        // Home, or on its last day(s) with everything claimed: pick as if home. The current area can only be had by
        // extending (the length goes back to the maximum, paying for the days added); another area waits for the
        // return, so it is scored as the dispatch it will be.
        var list = new List<Option>();
        list.Add(slot.Away
            ? new Option(Kind.Nothing, null, 0, 0, slot.Area, slot.DaysRemaining, false)
            : new Option(Kind.Nothing, null, 0, 0, null, 0, false));
        foreach (var area in unlocked)
        {
            if (slot.Away && area == slot.Area)
            {
                list.Add(new Option(Kind.Extend, area, rules.MaxDays, rules.CostPerDay * (rules.MaxDays - slot.DaysRemaining), area, rules.MaxDays, true));
                continue;
            }

            for (var days = rules.MinDays; days <= rules.MaxDays; days++)
                list.Add(new Option(Kind.Dispatch, area, days, slot.Away ? 0 : rules.CostPerDay * days, area, days, true) { Price = rules.CostPerDay * days });
        }

        return list;
    }

    // ---- search ----

    private sealed class Context
    {
        public Context(GranaryInput input, Dictionary<int, ExpeditionArea> areas, List<GranaryNeed> needs, List<SlotState> states)
        {
            Input = input;
            Areas = areas;
            States = states;
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

        public GranaryInput Input { get; }
        public Dictionary<int, ExpeditionArea> Areas { get; }
        public Dictionary<int, List<GranaryNeed>> Needs { get; }
        public List<SlotState> States { get; }
    }

    private sealed record Scored(Option[] Choice, double Score, int Changes, int Days, int Distinct, double Waste, int Cost, int RareStock);

    private static Option[] Search(List<List<Option>> options, Context ctx)
    {
        var purse = ctx.Input.Cowries;
        Scored? best = null;
        var choice = new Option[options.Count];

        void Walk(int i, int spent)
        {
            if (i == options.Count)
            {
                var scored = Evaluate((Option[])choice.Clone(), ctx);
                if (best == null || Better(scored, best))
                    best = scored;
                return;
            }

            foreach (var o in options[i])
            {
                if (spent + o.Cost > purse)
                    continue;
                choice[i] = o;
                Walk(i + 1, spent + o.Cost);
            }
        }

        Walk(0, 0);
        return best!.Choice; // Nothing is always an option and always affordable
    }

    private static bool Better(Scored a, Scored b)
    {
        if (Math.Abs(a.Score - b.Score) > Epsilon)
            return a.Score > b.Score;
        if (a.Changes != b.Changes)
            return a.Changes < b.Changes;
        if (a.Days != b.Days)
            return a.Days > b.Days;
        if (a.Distinct != b.Distinct)
            return a.Distinct > b.Distinct;
        if (Math.Abs(a.Waste - b.Waste) > Epsilon)
            return a.Waste < b.Waste;
        if (a.Cost != b.Cost)
            return a.Cost < b.Cost;
        if (a.RareStock != b.RareStock)
            return a.RareStock < b.RareStock;
        for (var i = 0; i < a.Choice.Length; i++)
        {
            var x = a.Choice[i].Area ?? -1;
            var y = b.Choice[i].Area ?? -1;
            if (x != y)
                return x < y;
        }

        return false; // first found wins: options are generated in a fixed order
    }

    private static Scored Evaluate(Option[] choice, Context ctx)
    {
        var input = ctx.Input;
        var rules = input.Rules;
        var score = Coverage(choice, ctx, out _) - choice.Count(o => o.Kind == Kind.Change) * rules.ChangeThreshold;

        var days = choice.Where(o => o.Commits && o.Kind != Kind.Change).Sum(o => o.Days);
        var distinct = choice.Where(o => o.DeliverArea != null).Select(o => o.DeliverArea).Distinct().Count();
        var rareStock = choice.Where(o => o.Commits && o.Area is { } a && ctx.Areas.ContainsKey(a))
            .Sum(o => input.OnHand.GetValueOrDefault(ctx.Areas[o.Area!.Value].RarePouchRow));

        // Expected units past the cap if nothing were used up meanwhile.
        var total = new Dictionary<int, double>();
        for (var i = 0; i < choice.Length; i++)
        {
            foreach (var (row, rate) in Rates(ctx.States[i].Slot, choice[i].DeliverArea, ctx))
                total[row] = total.GetValueOrDefault(row) + rate * choice[i].DeliverDays;
        }

        var waste = total.Sum(kv => Math.Max(0, input.OnHand.GetValueOrDefault(kv.Key) + Held(kv.Key, ctx) + kv.Value - rules.PouchCap));
        return new Scored(choice, score, choice.Count(o => o.Kind == Kind.Change), days, distinct, waste, choice.Sum(o => o.Price), rareStock);
    }

    /// <summary>
    /// Score of the needs a combination covers. Each need takes what has arrived by its due day and what the stronger
    /// needs on the same row left over. <paramref name="taken"/> = units per need, for the reasons.
    /// </summary>
    private static double Coverage(Option[] choice, Context ctx, out Dictionary<GranaryNeed, double> taken)
    {
        var rules = ctx.Input.Rules;
        taken = new Dictionary<GranaryNeed, double>(ReferenceEqualityComparer.Instance);
        var score = 0.0;
        foreach (var (row, needs) in ctx.Needs)
        {
            var used = 0.0;
            foreach (var need in needs)
            {
                var supply = Supply(row, need.DueInDays, choice, ctx);
                var take = Math.Clamp(Math.Min(need.Amount, supply - used), 0, need.Amount);
                taken[need] = take;
                used += take;
                score += take * rules.Weight(need.Source);
            }
        }

        return score;
    }

    /// <summary>Units of <paramref name="row"/> in hand (beyond the pouch's count) after <paramref name="day"/> payouts.</summary>
    private static double Supply(int row, int day, Option[] choice, Context ctx)
    {
        var rules = ctx.Input.Rules;
        var supply = 0.0;
        for (var i = 0; i < choice.Length; i++)
        {
            var s = ctx.States[i];
            // What already waits: collected today, or collectable — when collection waits for the return, only then.
            var held = s.Collect ? s.Collected.GetValueOrDefault(row) : s.Slot.Waiting.GetValueOrDefault(row);
            if (s.Collect || rules.CollectAnyTime || !s.Slot.Away || day >= s.Slot.DaysRemaining)
                supply += held;

            var o = choice[i];
            var rate = Rates(s.Slot, o.DeliverArea, ctx).GetValueOrDefault(row);
            if (rate <= 0)
                continue;
            supply += rules.CollectAnyTime
                ? rate * Math.Min(o.DeliverDays, day)
                : day >= o.DeliverDays ? rate * o.DeliverDays : 0;
        }

        return supply;
    }

    private static int Held(int row, Context ctx) =>
        ctx.States.Sum(s => s.Collect ? s.Collected.GetValueOrDefault(row) : s.Slot.Waiting.GetValueOrDefault(row));

    /// <summary>Expected units a day of an expedition to <paramref name="areaId"/> brings, by pouch row.</summary>
    private static Dictionary<int, double> Rates(GranarySlot slot, int? areaId, Context ctx)
    {
        var rates = new Dictionary<int, double>();
        if (areaId is not { } id || !ctx.Areas.TryGetValue(id, out var area))
            return rates;
        var rules = ctx.Input.Rules;
        foreach (var row in area.NormalPouchRows)
        {
            if (ctx.Input.GatheredBefore == null || ctx.Input.GatheredBefore.Contains(row))
                rates[row] = rates.GetValueOrDefault(row) + rules.NormalPerDay;
        }

        rates[area.RarePouchRow] = rates.GetValueOrDefault(area.RarePouchRow) + rules.RarePerDay(slot.Level);
        return rates;
    }

    // ---- decisions ----

    private static GranaryDecision Decide(SlotState s, Option[] best, int i, Context ctx)
    {
        var input = ctx.Input;
        var rules = input.Rules;
        var slot = s.Slot;
        var o = best[i];
        var empty = new Dictionary<int, int>();

        if (slot.Level <= 0)
            return new GranaryDecision(slot.Index, false, GranaryAction.Nothing, null, 0, 0, empty, empty, empty, s.Warnings, "not built");
        if (slot.UnderConstruction)
            return new GranaryDecision(slot.Index, false, GranaryAction.Nothing, null, 0, 0, empty, empty, empty, s.Warnings, "under construction");

        var action = o.Kind switch
        {
            Kind.Dispatch when !slot.Away => GranaryAction.Dispatch,
            Kind.Extend => GranaryAction.Extend,
            Kind.Change => GranaryAction.Change,
            _ => GranaryAction.Nothing,
        };

        // Extend and Change need every material claimed (Addon, for Extend; assumed for Change), so collect first.
        if (action is GranaryAction.Extend or GranaryAction.Change && !s.Collect && slot.Waiting.Any(kv => kv.Value > 0))
        {
            var stock = input.OnHand.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var other in ctx.States.Where(x => x != s && x.Collect))
            {
                foreach (var (row, n) in other.Collected)
                    stock[row] = stock.GetValueOrDefault(row) + n;
            }

            (s.Collected, s.Discarded) = Collection(slot.Waiting, stock, rules.PouchCap);
            s.Collect = true;
            s.CollectWhy = action == GranaryAction.Extend ? "extending needs every material claimed" : "claim before changing area";
            if (s.Discarded.Count > 0)
                s.Warnings.Add($"collecting discards {List(input, s.Discarded)} (pouch cap {rules.PouchCap})");
        }

        var expected = action == GranaryAction.Nothing
            ? empty
            : Rates(slot, o.DeliverArea, ctx).ToDictionary(kv => kv.Key, kv => (int)Math.Round(kv.Value * (action == GranaryAction.Extend ? o.Days - slot.DaysRemaining : o.DeliverDays)));

        var reason = Reason(s, best, i, action, ctx);
        if (s.Collect)
            reason = $"collect ({s.CollectWhy}); {reason}";
        if (!slot.Away && action == GranaryAction.Nothing)
            Warn(s, input, ctx);

        var area = action == GranaryAction.Nothing ? (int?)null : o.Area;
        var days = action == GranaryAction.Nothing ? 0 : o.Days;
        return new GranaryDecision(slot.Index, s.Collect, action, area, days, action == GranaryAction.Nothing ? 0 : o.Cost,
            s.Collected, s.Discarded, expected, s.Warnings, reason);
    }

    private static void Warn(SlotState s, GranaryInput input, Context ctx)
    {
        if (!input.UnlockedAreas.Any(a => ctx.Areas.ContainsKey(a)))
            s.Warnings.Add("no expedition area is unlocked");
        else if (input.Cowries < input.Rules.CostPerDay * input.Rules.MinDays)
            s.Warnings.Add($"not enough cowries to dispatch ({input.Rules.CostPerDay} a day, {input.Cowries} on hand)");
        else
            s.Warnings.Add("left home: the cowries went to the other granary");
    }

    /// <summary>One line: what this granary's choice covers that it would not without it.</summary>
    private static string Reason(SlotState s, Option[] best, int i, GranaryAction action, Context ctx)
    {
        var input = ctx.Input;
        var o = best[i];
        var slot = s.Slot;
        var areaName = o.DeliverArea is { } id && ctx.Areas.TryGetValue(id, out var a) ? a.Name : "?";

        // The same combination with this granary bringing nothing new: what it adds is what it is for.
        var without = (Option[])best.Clone();
        without[i] = slot.Away
            ? new Option(Kind.Nothing, null, 0, 0, slot.Area, slot.DaysRemaining, false)
            : new Option(Kind.Nothing, null, 0, 0, null, 0, false);
        Coverage(best, ctx, out var with);
        Coverage(without, ctx, out var withoutTaken);
        var gain = with
            .Select(kv => (Need: kv.Key, Units: kv.Value - withoutTaken.GetValueOrDefault(kv.Key)))
            .Where(x => x.Units > Epsilon)
            .OrderByDescending(x => x.Units * input.Rules.Weight(x.Need.Source))
            .ThenBy(x => x.Need.PouchRow)
            .FirstOrDefault();
        var serves = gain.Need == null
            ? "nothing is short, so it keeps the granary busy"
            : $"{gain.Units:0} {Name(input, gain.Need.PouchRow)} toward {Describe(gain.Need.Source)} (short {gain.Need.Amount})";

        return action switch
        {
            GranaryAction.Dispatch => $"send to {areaName} for {o.Days} day{(o.Days == 1 ? "" : "s")}: {serves}",
            GranaryAction.Extend => $"extend {areaName} to {o.Days} days, still the pick: {serves}",
            GranaryAction.Change => $"change to {areaName} for the {o.Days} days left: {serves}",
            _ when slot.Away && o.Kind == Kind.Dispatch => $"home in {slot.DaysRemaining} day{(slot.DaysRemaining == 1 ? "" : "s")}, then {areaName}",
            _ when slot.Away => $"keep {(slot.Area is { } cur && ctx.Areas.TryGetValue(cur, out var c) ? c.Name : "?")} ({slot.DaysRemaining} day{(slot.DaysRemaining == 1 ? "" : "s")} left)",
            _ => "stays home",
        };
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

    private static string Name(GranaryInput input, int row) =>
        input.MaterialNames != null && input.MaterialNames.TryGetValue(row, out var n) ? n : $"pouch {row}";

    private static string List(GranaryInput input, Dictionary<int, int> amounts) =>
        string.Join(", ", amounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {Name(input, kv.Key)}"));

    private static bool IsUsable(this GranarySlot slot) => slot.Level > 0 && !slot.UnderConstruction;

    private sealed class SlotState
    {
        public SlotState(GranarySlot slot) => Slot = slot;

        public GranarySlot Slot { get; }
        public bool Collect { get; set; }

        /// <summary>Waiting materials could be collected today without discarding any.</summary>
        public bool Claimable { get; set; }
        public string CollectWhy { get; set; } = "";
        public Dictionary<int, int> Collected { get; set; } = new();
        public Dictionary<int, int> Discarded { get; set; } = new();
        public List<string> Warnings { get; } = new();
    }
}
