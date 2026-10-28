using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Farm;
using Calypso.Core.Granary;

namespace Calypso.Core.Pasture;

/// <summary>
/// Decides whether to collect the caretaker's leavings, and advises the player which animals to keep, capture or
/// release and which feed pays — so the pen's leavings match what the workshop is short of (see
/// <see cref="NeedSource"/> for the order), then construction, then a stock floor of every leaving. Pure and
/// deterministic. Collecting (CollectLeavingsAll) is the only action: everything else the game does in the pasture is not
/// reverse-engineered, so it is <see cref="PastureAdvice"/> for the player.
///
/// An animal is worth what the pen would lose without it: its leavings over <see cref="PastureRules.HorizonDays"/>
/// (one guaranteed, the second by mood) against the needs still uncovered. Free room is filled with the species that
/// add the most; a full pen trades its least useful animal only when the capture adds
/// <see cref="PastureRules.SwapThreshold"/> more. Ties go to common over variant over rare, a species with a known spot,
/// the smaller size, then the lowest MJIAnimals row.
/// </summary>
public static class PasturePlanner
{
    private const double Epsilon = 1e-6;

    public static PasturePlan Plan(PastureInput input)
    {
        var rules = input.Rules;
        var empty = new Dictionary<int, int>();
        var warnings = new List<string>();
        if (input.Level <= 0)
            return Off("not built");
        if (input.UnderConstruction)
            return Off("under construction");

        var species = input.Species.ToDictionary(a => a.Row);
        var pen = new List<Member>();
        foreach (var a in input.Animals.OrderBy(a => a.Slot))
        {
            if (species.TryGetValue(a.Animal, out var def))
                pen.Add(new Member(def, a, MoodOf(a, rules)));
            else
                warnings.Add($"slot {a.Slot + 1}: unknown animal {a.Animal}");
        }

        var capacity = rules.Capacity(input.Level);
        if (pen.Sum(m => rules.SlotsFor(m.Def.Size)) > capacity)
            warnings.Add($"{pen.Count} animals in a pasture that holds {capacity} (PastureRules.CapacityByLevel)");

        // 1. Collection: CollectLeavingsAll takes everything at once.
        var (collect, collected, discarded, why) = DecideCollect(input, pen, warnings);

        // 2. Advice, against the needs on leavings rows.
        var ctx = new Context(input, AllNeeds(input, species));
        var advice = Advise(pen, ctx, capacity);

        var underCare = pen.Count(m => m.Pen!.UnderCare && !m.Pen.CareHalted);
        var cost = underCare * rules.CareFee;
        if (cost > input.Cowries)
            warnings.Add($"{input.Cowries} cowries do not cover {underCare} animals' care ({rules.CareFee} each a day); care halts where the fee goes unpaid");
        foreach (var m in pen.Where(m => m.Pen!.CareHalted))
            warnings.Add($"slot {m.Pen!.Slot + 1}: care halted (cowries short or {rules.AnimalCap} leavings waiting); it resumes by itself");

        var expected = new Dictionary<int, int>();
        foreach (var (row, rate) in Rates(pen, rules).OrderBy(kv => kv.Key))
        {
            var n = (int)Math.Round(rate * rules.HorizonDays);
            if (n > 0)
                expected[row] = n;
        }

        var todo = advice.FirstOrDefault(a => a.Kind != AdviceKind.Keep);
        var action = collect ? PastureAction.CollectLeavings : todo != null ? PastureAction.Advise : PastureAction.Nothing;
        var keeps = advice.Count(a => a.Kind == AdviceKind.Keep);
        var summary = todo != null ? $"advice: {todo.Text}"
            : keeps > 0 ? $"{keeps} animal{(keeps == 1 ? "" : "s")} give{(keeps == 1 ? "s" : "")} what is short; keep {(keeps == 1 ? "it" : "them")}"
            : pen.Count == 0 ? "the pen is empty and nothing is short"
            : "nothing is short";
        var reason = collect ? $"collect {List(input, collected)} ({why}); {summary}" : summary;
        return new PasturePlan(action, collect ? collected : empty, collect ? discarded : empty, expected, cost, advice, warnings, reason);

        PasturePlan Off(string r) => new(PastureAction.Nothing, empty, empty, empty, 0, Array.Empty<PastureAdvice>(), warnings, r);
    }

    /// <summary>Mood an animal is counted at: its feed's cap while the caretaker feeds it, else as it is now.</summary>
    private static int MoodOf(PenAnimal a, PastureRules rules) =>
        a.UnderCare && !a.CareHalted && rules.Feed(a.FeedItemId) is { } feed ? feed.MoodCap : a.Mood;

    // ---- collection ----

    private static (bool Collect, Dictionary<int, int> Collected, Dictionary<int, int> Discarded, string Why) DecideCollect(
        PastureInput input, List<Member> pen, List<string> warnings)
    {
        var rules = input.Rules;
        var waiting = input.Waiting.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (waiting.Count == 0)
            return (false, new(), new(), "");

        var (collected, discarded) = GranaryPlanner.Collection(waiting, input.OnHand, rules.PouchCap);
        if (discarded.Count == 0)
            return (true, collected, discarded, "leavings are waiting");

        // One call takes every row: collect anyway when a needed leaving comes with it, else hold until there is room.
        if (input.Needs.FirstOrDefault(n => n.Amount > 0 && collected.GetValueOrDefault(n.PouchRow) > 0) is { } need)
        {
            warnings.Add($"collecting discards {List(input, discarded)} (pouch cap {rules.PouchCap})");
            return (true, collected, discarded, $"{Name(input, need.PouchRow)} is needed");
        }

        var full = pen.Count(m => m.Pen!.UnderCare && m.Pen.Waiting + rules.GuaranteedPerDay + rules.SecondPerDay >= rules.AnimalCap);
        warnings.Add($"held: collecting now would discard {List(input, discarded)} (pouch cap {rules.PouchCap}); collect once the pouch has room" +
                     (full > 0 ? $"; {full} animal{(full == 1 ? "" : "s")} near {rules.AnimalCap} leavings, where care halts" : ""));
        return (false, new(), new(), "");
    }

    // ---- needs ----

    /// <summary>The caller's needs on leavings rows plus the planner's stock floor for every leaving.</summary>
    private static List<GranaryNeed> AllNeeds(PastureInput input, Dictionary<int, AnimalDef> species)
    {
        var leavings = species.Values.SelectMany(a => a.Leavings).ToHashSet();
        var needs = input.Needs.Where(n => n.Amount > 0 && leavings.Contains(n.PouchRow)).ToList();
        foreach (var row in leavings.OrderBy(r => r))
        {
            var short_ = input.Rules.LeavingsFloor - input.OnHand.GetValueOrDefault(row);
            if (short_ > 0)
                needs.Add(new GranaryNeed(row, short_, input.Rules.HorizonDays, NeedSource.StockFloor));
        }

        return needs;
    }

    // ---- advice ----

    private static List<PastureAdvice> Advise(List<Member> pen, Context ctx, int capacity)
    {
        var input = ctx.Input;
        var rules = input.Rules;
        var members = pen.ToList();
        var releases = new List<PastureAdvice>();
        var captures = new List<PastureAdvice>();
        var free = capacity - pen.Sum(m => rules.SlotsFor(m.Def.Size));

        // A better feed for an animal already there comes before catching another one; it then counts at that mood.
        var feeds = new List<PastureAdvice>();
        foreach (var m in members)
        {
            if (BetterFeed(m, members, ctx) is { } better)
                feeds.Add(better);
        }

        // Free room: the species that adds the most, one at a time.
        while (Best(members, ctx, free) is { } pick)
        {
            captures.Add(Capture(pick.Def, members, ctx));
            members.Add(new Member(pick.Def, null, rules.NewAnimalMood));
            free -= rules.SlotsFor(pick.Def.Size);
        }

        // A full pen trades its least useful animal for a clearly better one.
        var released = new HashSet<Member>(ReferenceEqualityComparer.Instance);
        for (var guard = 0; guard < capacity; guard++)
        {
            var worst = members.Where(m => m.Pen != null && !released.Contains(m))
                .Select(m => (M: m, Value: Marginal(m, members, ctx)))
                .OrderBy(x => x.Value).ThenBy(x => x.M.Def.Rarity).ThenByDescending(x => x.M.Pen!.Slot)
                .FirstOrDefault();
            if (worst.M == null)
                break;

            var without = members.Where(m => !ReferenceEquals(m, worst.M)).ToList();
            if (Best(without, ctx, free + rules.SlotsFor(worst.M.Def.Size)) is not { } swap || swap.Gain - worst.Value <= rules.SwapThreshold)
                break;

            var capture = Capture(swap.Def, without, ctx);
            releases.Add(new PastureAdvice(AdviceKind.Release, worst.M.Def.Row, worst.M.Pen!.Slot, null,
                $"release {Describe(worst.M, input)}: what it gives covers {(worst.Value <= Epsilon ? "nothing short" : "little")} — make room for {Label(swap.Def, input)}{CareNote(worst.M)}"));
            captures.Add(capture);
            released.Add(worst.M);
            members = without;
            members.Add(new Member(swap.Def, null, rules.NewAnimalMood));
            free += rules.SlotsFor(worst.M.Def.Size) - rules.SlotsFor(swap.Def.Size);
        }

        var keeps = new List<PastureAdvice>();
        foreach (var m in members.Where(m => m.Pen != null))
        {
            var value = Marginal(m, members, ctx);
            if (value > Epsilon)
                keeps.Add(new PastureAdvice(AdviceKind.Keep, m.Def.Row, m.Pen!.Slot, null, $"keep {Describe(m, input)}: {Serves(members, m, ctx)}"));
            else if (m.Pen!.UnderCare && Overflows(m, ctx))
                releases.Add(new PastureAdvice(AdviceKind.Release, m.Def.Row, m.Pen.Slot, null,
                    $"release {Describe(m, input)} or dismiss its caretaker: nothing needs what it gives and it would overflow the pouch ({rules.CareFee} cowries a day saved){CareNote(m)}"));

            if (feeds.All(f => f.Slot != m.Pen.Slot) && CheaperFeed(m, members, ctx) is { } cheaper)
                feeds.Add(cheaper);
        }

        return releases.Concat(captures).Concat(feeds).Concat(keeps).ToList();
    }

    /// <summary>The species (fitting in <paramref name="room"/> slots) whose capture adds the most to <paramref name="members"/>.</summary>
    private static (AnimalDef Def, double Gain)? Best(List<Member> members, Context ctx, int room)
    {
        var rules = ctx.Input.Rules;
        if (room <= 0)
            return null;

        var before = Coverage(members, ctx, out _);
        (AnimalDef Def, double Gain)? best = null;
        foreach (var def in ctx.Input.Species.Where(d => rules.SlotsFor(d.Size) <= room).OrderBy(d => d.Row))
        {
            members.Add(new Member(def, null, rules.NewAnimalMood));
            var gain = Coverage(members, ctx, out _) - before;
            members.RemoveAt(members.Count - 1);
            if (gain <= Epsilon)
                continue;
            if (best == null || gain > best.Value.Gain + Epsilon || (Math.Abs(gain - best.Value.Gain) <= Epsilon && Tie(def, best.Value.Def, ctx)))
                best = (def, gain);
        }

        return best;
    }

    /// <summary>Equal gain: easier to find first (common, variant, rare), then a known spot, then smaller (lowest row wins otherwise).</summary>
    private static bool Tie(AnimalDef a, AnimalDef b, Context ctx)
    {
        if (a.Rarity != b.Rarity)
            return a.Rarity < b.Rarity;
        var seenA = ctx.Input.Sighted?.Contains(a.BNpcBase) == true;
        var seenB = ctx.Input.Sighted?.Contains(b.BNpcBase) == true;
        if (seenA != seenB)
            return seenA;
        return a.Size < b.Size;
    }

    private static PastureAdvice Capture(AnimalDef def, List<Member> members, Context ctx)
    {
        var input = ctx.Input;
        var seen = input.Sighted == null ? "" : input.Sighted.Contains(def.BNpcBase) ? ", seen before" : ", no spot known yet";
        members.Add(new Member(def, null, input.Rules.NewAnimalMood));
        var serves = Serves(members, members[^1], ctx);
        members.RemoveAt(members.Count - 1);
        return new PastureAdvice(AdviceKind.Capture, def.Row, null, null,
            $"capture {Label(def, input)} ({def.Rarity.ToString().ToLowerInvariant()}, {PastureRules.CaptureTool(def.Size)}{seen}): {serves}");
    }

    /// <summary>
    /// The best feed, when the second leaving is wanted enough (it comes on 80% of days at Gleeful). The member is left
    /// at that mood, so what is planned after counts it.
    /// </summary>
    private static PastureAdvice? BetterFeed(Member m, List<Member> members, Context ctx)
    {
        var input = ctx.Input;
        var rules = input.Rules;
        var best = rules.Feeds.OrderByDescending(f => f.MoodCap).First();
        var mood = m.Mood;
        if (m.Pen == null || mood >= best.MoodCap)
            return null;

        var now = Coverage(members, ctx, out _);
        m.Mood = best.MoodCap;
        if (Coverage(members, ctx, out _) - now <= rules.FeedThreshold)
        {
            m.Mood = mood;
            return null;
        }

        return new PastureAdvice(AdviceKind.Feed, m.Def.Row, m.Pen.Slot, best.ItemId,
            $"feed {Describe(m, input)} {best.Name} {How(m)}: {Name(input, m.Def.Leavings[1])} comes {Percent(rules.SecondChance(best.MoodCap))} of days " +
            $"at {PastureRules.MoodName(best.MoodCap)}, not {Percent(rules.SecondChance(mood))} at {PastureRules.MoodName(mood)}");
    }

    /// <summary>The cheapest feed, when a dearer one brings nothing anyone needs (greenfeed is made from produce, sweetfeed from apples).</summary>
    private static PastureAdvice? CheaperFeed(Member m, List<Member> members, Context ctx)
    {
        var input = ctx.Input;
        var rules = input.Rules;
        var cheapest = rules.Feeds.OrderBy(f => f.MoodCap).First();
        if (m.Pen is not { UnderCare: true } pen || rules.Feed(pen.FeedItemId) is not { } feed || feed.MoodCap <= cheapest.MoodCap)
            return null;

        var now = Coverage(members, ctx, out _);
        var mood = m.Mood;
        m.Mood = cheapest.MoodCap;
        var loss = now - Coverage(members, ctx, out _);
        m.Mood = mood;
        return loss > Epsilon
            ? null
            : new PastureAdvice(AdviceKind.Feed, m.Def.Row, pen.Slot, cheapest.ItemId,
                $"{Describe(m, input)} needs only {cheapest.Name} {How(m)}: nothing needs more of its {Name(input, m.Def.Leavings[1])} (it now gets {feed.Name})");
    }

    private static string Percent(double chance) => $"{Math.Round(chance * 100)}%";

    private static string How(Member m) => m.Pen!.UnderCare ? "as its caretaker's feed" : "by hand";

    /// <summary>Every leaving the animal gives would pass the cap within the horizon even without it.</summary>
    private static bool Overflows(Member m, Context ctx)
    {
        var rules = ctx.Input.Rules;
        return m.Def.Leavings.All(row =>
            ctx.Input.OnHand.GetValueOrDefault(row) + ctx.Input.Waiting.GetValueOrDefault(row) + Rate(m, row, rules) * rules.HorizonDays > rules.PouchCap);
    }

    private static string CareNote(Member m) =>
        m.Pen != null && (m.Pen.UnderCare || m.Pen.Waiting > 0) ? " (dismiss its caretaker and collect its leavings first: the game will not release it before)" : "";

    // ---- coverage ----

    private sealed class Context
    {
        public Context(PastureInput input, List<GranaryNeed> needs)
        {
            Input = input;
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

        public PastureInput Input { get; }
        public Dictionary<int, List<GranaryNeed>> Needs { get; }
    }

    private sealed class Member
    {
        public Member(AnimalDef def, PenAnimal? pen, int mood) => (Def, Pen, Mood) = (def, pen, mood);

        public AnimalDef Def { get; }

        /// <summary>The animal in the pen; null for one the advice would capture.</summary>
        public PenAnimal? Pen { get; }

        public int Mood { get; set; }
    }

    /// <summary>
    /// Score of the needs <paramref name="members"/> cover: what waits now plus the daily leavings up to each need's due
    /// day, stronger needs on a row first (the granary planner's rule). <paramref name="taken"/> = units per need.
    /// </summary>
    private static double Coverage(List<Member> members, Context ctx, out Dictionary<GranaryNeed, double> taken)
    {
        var rules = ctx.Input.Rules;
        var rates = Rates(members, rules);
        taken = new Dictionary<GranaryNeed, double>(ReferenceEqualityComparer.Instance);
        var score = 0.0;
        foreach (var (row, needs) in ctx.Needs)
        {
            var held = ctx.Input.Waiting.GetValueOrDefault(row);
            var rate = rates.GetValueOrDefault(row);
            var used = 0.0;
            foreach (var need in needs)
            {
                var supply = held + rate * need.DueInDays;
                var take = Math.Clamp(Math.Min(need.Amount, supply - used), 0, need.Amount);
                taken[need] = take;
                used += take;
                score += take * rules.Weight(need.Source);
            }
        }

        return score;
    }

    private static double Marginal(Member m, List<Member> members, Context ctx) =>
        Coverage(members, ctx, out _) - Coverage(members.Where(x => !ReferenceEquals(x, m)).ToList(), ctx, out _);

    /// <summary>Expected leavings a day by pouch row.</summary>
    private static Dictionary<int, double> Rates(IEnumerable<Member> members, PastureRules rules)
    {
        var rates = new Dictionary<int, double>();
        foreach (var m in members)
        {
            foreach (var row in m.Def.Leavings.Distinct())
                rates[row] = rates.GetValueOrDefault(row) + Rate(m, row, rules);
        }

        return rates;
    }

    private static double Rate(Member m, int row, PastureRules rules) =>
        (m.Def.Leavings[0] == row ? rules.GuaranteedPerDay : 0)
        + (m.Def.Leavings.Count > 1 && m.Def.Leavings[1] == row ? rules.SecondPerDay * rules.SecondChance(m.Mood) : 0);

    /// <summary>What <paramref name="m"/> adds to <paramref name="members"/>: the need it adds the most weighted units to.</summary>
    private static string Serves(List<Member> members, Member m, Context ctx)
    {
        var input = ctx.Input;
        Coverage(members, ctx, out var with);
        Coverage(members.Where(x => !ReferenceEquals(x, m)).ToList(), ctx, out var without);
        return FarmNeeds.Strongest(with, without, input.Rules.Weight) is { } gain
            ? $"{gain.Units:0.#} {Name(input, gain.Row)} toward {Describe(gain.Source)} (short {gain.Short})"
            : "nothing is short";
    }

    // ---- text ----

    private static string Describe(NeedSource source) => source switch
    {
        NeedSource.WorkshopThisWeek => "this week's workshop",
        NeedSource.WorkshopNextWeek => "next week's workshop",
        NeedSource.Construction => "construction",
        NeedSource.SlowGather => "slow gathering",
        NeedSource.StockFloor => "the stock floor",
        _ => source.ToString(),
    };

    /// <summary>"Sanctuary Milk / Sanctuary Fleece animal (MJIAnimals 2)" — or its name, when the adapter knows it.</summary>
    private static string Label(AnimalDef def, PastureInput input) =>
        def.Name ?? $"{string.Join(" / ", def.Leavings.Select(r => Name(input, r)))} animal (MJIAnimals {def.Row})";

    private static string Describe(Member m, PastureInput input) => $"{Label(m.Def, input)} in slot {m.Pen!.Slot + 1}";

    private static string Name(PastureInput input, int row) =>
        input.MaterialNames != null && input.MaterialNames.TryGetValue(row, out var n) ? n : $"pouch {row}";

    private static string List(PastureInput input, IReadOnlyDictionary<int, int> amounts) =>
        string.Join(", ", amounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {Name(input, kv.Key)}"));
}
