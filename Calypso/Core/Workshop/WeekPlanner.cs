using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Calypso.Core.Workshop;

/// <summary>A favor (Felicitous Furball request) still to fill: at least <see cref="Required"/> more of a craft this week.</summary>
public sealed record FavorRequirement(int CraftId, int Required);

public sealed record PlannerLimits
{
    /// <summary>Chains kept per day before any state is looked at, ranked by a single-workshop optimistic value.</summary>
    public int ChainsPerDay { get; init; } = 150;

    /// <summary>Of those, the best chains for one search state that are paired up into whole-day assignments.</summary>
    public int ChainsPerState { get; init; } = 10;

    public int BeamWidth { get; init; } = 24;

    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(1.5);
}

public sealed record PlannerInput
{
    public required CraftCatalog Catalog { get; init; }
    public required SupplyForecast Forecast { get; init; }
    public required int Rank { get; init; }

    /// <summary>Tier (1-5) per workshop slot; 0 = not built. Index = workshop index used by ScheduleCraft.</summary>
    public required IReadOnlyList<int> WorkshopTiers { get; init; }

    public required int PopularityRow { get; init; }
    public required int MaxGroove { get; init; }

    /// <summary>Groove at the start of <see cref="FirstDay"/>.</summary>
    public required int StartGroove { get; init; }

    /// <summary>First day (0-6) the planner may change — tomorrow, in the daily loop.</summary>
    public required int FirstDay { get; init; }

    public int RestDaysPerWeek { get; init; } = 2;

    /// <summary>Rest days already taken this week before <see cref="FirstDay"/>.</summary>
    public int RestsTaken { get; init; }

    /// <summary>Own production this week before <see cref="FirstDay"/>, by craft id (length MaxCraftId+1).</summary>
    public required int[] ProducedBefore { get; init; }

    /// <summary>Isleventory on hand by pouch row, already minus anything reserved (rank mode).</summary>
    public required IReadOnlyDictionary<int, int> Materials { get; init; }

    /// <summary>Score cost per gatherable unit the plan would have to gather beyond what is on hand.</summary>
    public double GatherCostPerUnit { get; init; } = 1.0;

    public IReadOnlyList<FavorRequirement> Favors { get; init; } = Array.Empty<FavorRequirement>();

    public PlannerLimits Limits { get; init; } = new();

    /// <summary>
    /// ASSUMPTION to verify in game: an efficient craft makes 2 items and consumes materials for both. False would
    /// charge one craft's materials per step.
    /// </summary>
    public bool MaterialsPerItem { get; init; } = true;
}

/// <summary>One planned day. <see cref="Main"/> runs in workshops [0, MainCount), <see cref="Fourth"/> in the rest.</summary>
public sealed record PlannedDay(int Day, bool Rest, int[]? Main, int MainCount, int[]? Fourth, double Value, double Confidence);

public sealed record PlanResult(
    IReadOnlyList<PlannedDay> Days,
    double ExpectedValue,
    IReadOnlyDictionary<int, int> GatherDemand,
    bool FavorsMet,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Week-horizon beam search (decided option B): plans every remaining day, carrying own production (supply), groove,
/// materials and rest days forward; the daily loop commits only the first planned day and re-plans tomorrow with
/// fresh observations.
///
/// Per day a state branches into Rest (when rest days remain) or an assignment: one chain in the "main" workshops and,
/// with two or more workshops, optionally a different chain in the last one — the community's "main + 4th" shape.
/// Hard-capped materials (granary rares, produce, leavings) can never go negative; a gatherable shortfall costs
/// <see cref="PlannerInput.GatherCostPerUnit"/> per unit and is reported as gather demand. Favors weigh heavily during
/// the search and are a hard filter on the final plans when any plan meets them.
/// </summary>
public static class WeekPlanner
{
    private const int Days = 7;

    /// <summary>Gatherable-only chains added to each day's candidates (see <see cref="PreselectChains"/>).</summary>
    private const int SafeChainsPerDay = 10;

    /// <summary>Score per favor item still owed — large enough that the beam keeps favor-filling paths alive.</summary>
    private const double FavorWeight = 1000;

    public static PlanResult Plan(PlannerInput input, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var catalog = input.Catalog;
        var workshops = input.WorkshopTiers.Select((t, i) => (Tier: t, Index: i)).Where(w => w.Tier > 0).ToList();
        var topTier = workshops.Count == 0 ? 0 : workshops.Max(w => w.Tier);
        var maxPouch = Math.Max(catalog.Pouch.Count == 0 ? 0 : catalog.Pouch.Keys.Max(), input.Materials.Count == 0 ? 0 : input.Materials.Keys.Max()) + 1;

        var start = new State(new int[catalog.MaxCraftId + 1], new int[maxPouch], input.StartGroove, input.RestsTaken, 0, new List<PlannedDay>());
        Array.Copy(input.ProducedBefore, start.Produced, Math.Min(input.ProducedBefore.Length, start.Produced.Length));
        var favorBaseline = input.Favors.ToDictionary(f => f.CraftId, f => start.Produced.ElementAtOrDefault(f.CraftId));

        if (workshops.Count == 0)
            return new PlanResult(Array.Empty<PlannedDay>(), 0, new Dictionary<int, int>(), input.Favors.Count == 0, new[] { "No workshop built." });

        bool Allowed(CraftDef c) => c.RankReq <= input.Rank && HardCapsAllow(c, input, catalog);
        var evaluator = new DayEvaluator(catalog, input.Forecast, input.PopularityRow, input.MaxGroove);
        var tiers = input.WorkshopTiers.ToArray();
        var scratch = new int[catalog.MaxCraftId + 1];
        var limits = input.Limits;
        var beam = new List<State> { start };

        for (var day = input.FirstDay; day < Days; day++)
        {
            cancel.ThrowIfCancellationRequested();
            if (watch.Elapsed > limits.TimeBudget && limits.BeamWidth > 2)
            {
                warnings.Add($"Time budget reached at day {day + 1}; finishing with a narrow search.");
                limits = limits with { BeamWidth = 2, ChainsPerState = 3 };
            }

            var (dayChains, favorChains) = PreselectChains(Allowed, day, input, catalog, topTier, limits.ChainsPerDay);
            var next = new List<State>();

            foreach (var state in beam)
            {
                var restsLeft = input.RestDaysPerWeek - state.RestsTaken;
                var daysLeft = Days - day;
                if (restsLeft > 0)
                    next.Add(state.Branch(day, rest: true));

                if (restsLeft >= daysLeft)
                    continue; // every remaining day has to be a rest day

                foreach (var child in WorkChildren(state, day, dayChains, favorChains, favorBaseline, workshops, tiers, topTier, evaluator, input, catalog, scratch, limits))
                    next.Add(child);
            }

            beam = next
                .OrderByDescending(s => Score(s, input, catalog, favorBaseline))
                .Take(limits.BeamWidth)
                .ToList();

            if (beam.Count == 0)
                break;
        }

        if (beam.Count == 0)
            warnings.Add("No workable plan: every option overdraws a hard-capped material.");

        var finals = beam.Where(s => s.RestsTaken == input.RestDaysPerWeek || input.FirstDay >= Days).ToList();
        if (finals.Count == 0)
            finals = beam;

        var meeting = finals.Where(s => FavorsMet(s, input, favorBaseline)).ToList();
        var favorsMet = meeting.Count > 0 || input.Favors.Count == 0;
        if (!favorsMet)
            warnings.Add("No plan found that fills every favor this week.");

        var best = (meeting.Count > 0 ? meeting : finals)
            .OrderByDescending(s => s.Value - GatherCost(s, input, catalog))
            .FirstOrDefault() ?? start;

        return new PlanResult(best.Choices, best.Value, GatherDemand(best, input, catalog), favorsMet, warnings);
    }

    /// <summary>
    /// Value of an existing plan (a pasted community schedule, or one of ours) under the same model the search uses,
    /// day by day from <see cref="PlannerInput.FirstDay"/>, carrying production and groove. Ignores materials and favors —
    /// it answers "how much would this make", not "is it allowed".
    /// </summary>
    public static double Evaluate(PlannerInput input, WeekPlan plan)
    {
        var catalog = input.Catalog;
        var evaluator = new DayEvaluator(catalog, input.Forecast, input.PopularityRow, input.MaxGroove);
        var tiers = input.WorkshopTiers.ToArray();
        var built = tiers.Count(t => t > 0);
        var produced = (int[])input.ProducedBefore.Clone();
        var groove = input.StartGroove;
        var total = 0.0;

        for (var day = input.FirstDay; day < Days; day++)
        {
            var cycle = plan.Day(day);
            if (cycle == null || cycle.Rest)
                continue;

            var chains = new int[]?[tiers.Length];
            var k = 0;
            for (var w = 0; w < tiers.Length; w++)
            {
                if (tiers[w] <= 0)
                    continue;
                var chain = k < Math.Min(cycle.MainCount, built) ? cycle.Main : cycle.Fourth ?? cycle.Main;
                chains[w] = chain?.ToArray();
                k++;
            }

            var today = new int[catalog.MaxCraftId + 1];
            var (value, endGroove) = evaluator.Evaluate(day, chains, tiers, groove, produced, today);
            total += value;
            groove = endGroove;
            for (var c = 0; c < today.Length; c++)
                produced[c] += today[c];
        }

        return total;
    }

    private static IEnumerable<State> WorkChildren(
        State state, int day, List<int[]> dayChains, Dictionary<int, List<int[]>> favorChains, Dictionary<int, int> favorBaseline,
        List<(int Tier, int Index)> workshops, int[] tiers, int topTier,
        DayEvaluator evaluator, PlannerInput input, CraftCatalog catalog, int[] scratch, PlannerLimits limits)
    {
        double Solo(int[] chain)
        {
            Array.Clear(scratch);
            return evaluator.Evaluate(day, new[] { chain }, new[] { topTier }, state.Groove, state.Produced, scratch).Value;
        }

        var n = workshops.Count;

        // Hard-capped materials one more chain would need, run in `count` workshops, against what this state has left
        // (the same accounting as State.Work).
        bool Fits(int[] chain, int count)
        {
            var need = new Dictionary<int, int>();
            CraftDef? previous = null;
            foreach (var id in chain)
            {
                var craft = catalog.Get(id)!;
                var units = input.MaterialsPerItem && previous != null && craft.ChainsAfter(previous) ? 2 : 1;
                foreach (var m in craft.Materials)
                {
                    if (catalog.KindOf(m.PouchRow) != MaterialKind.Gatherable)
                        need[m.PouchRow] = need.GetValueOrDefault(m.PouchRow) + m.Amount * units;
                }

                previous = craft;
            }

            return need.All(kv => (kv.Key < state.MaterialsUsed.Length ? state.MaterialsUsed[kv.Key] : 0) + count * kv.Value
                                  <= input.Materials.GetValueOrDefault(kv.Key));
        }

        // Rank the day's chains for THIS state (its own production changes what sells), single workshop. A chain no
        // workshop could run on what is left is no candidate: cutting to the best few first let scarce produce or rares
        // fill every seat, every work day overdrew, and the plan came back empty.
        var byValue = dayChains
            .Select(chain => (chain, v: Solo(chain)))
            .OrderByDescending(x => x.v)
            .Select(x => x.chain)
            .ToList();
        var ranked = byValue.Where(chain => Fits(chain, 1)).Take(limits.ChainsPerState).ToList();

        // And keep one chain every workshop can run, so a work day always has an assignment.
        if (!ranked.Any(chain => Fits(chain, n)) && byValue.FirstOrDefault(chain => Fits(chain, n)) is { } safe)
            ranked.Add(safe);

        // A favor this state still owes always gets its two best chains a seat, or the value cut would drop them.
        foreach (var f in input.Favors)
        {
            if (state.Produced[f.CraftId] - favorBaseline[f.CraftId] >= f.Required || !favorChains.TryGetValue(f.CraftId, out var fc))
                continue;
            foreach (var chain in fc.OrderByDescending(Solo).Take(2))
            {
                if (!ranked.Contains(chain))
                    ranked.Add(chain);
            }
        }

        foreach (var a in ranked)
        {
            if (Assign(a, null) is { } solo)
                yield return solo;
            if (n < 2)
                continue;

            foreach (var b in ranked)
            {
                if (!ReferenceEquals(a, b) && Assign(a, b) is { } split)
                    yield return split;
            }
        }

        State? Assign(int[] main, int[]? fourth)
        {
            var chains = new int[]?[tiers.Length];
            var mainCount = fourth == null ? n : n - 1;
            for (var k = 0; k < n; k++)
                chains[workshops[k].Index] = k < mainCount ? main : fourth;

            var today = new int[catalog.MaxCraftId + 1];
            var (value, groove) = evaluator.Evaluate(day, chains, tiers, state.Groove, state.Produced, today);
            var confidence = Confidence(day, main, fourth, input.Forecast);
            return state.Work(day, main, mainCount, fourth, value, groove, today, confidence, input, catalog);
        }
    }

    /// <summary>
    /// The day's candidate chains: the best by an additive per-craft weight (base value × top tier × popularity ×
    /// expected supply, no groove, no production planned yet) via branch and bound, plus per favor craft its best
    /// chains that contain it.
    /// </summary>
    private static (List<int[]> Chains, Dictionary<int, List<int[]>> FavorChains) PreselectChains(
        Func<CraftDef, bool> allowed, int day, PlannerInput input, CraftCatalog catalog, int topTier, int keep)
    {
        var tier = catalog.Rules.TierRatio(topTier) / 100.0;
        double Weight(int id)
        {
            var c = catalog.Get(id)!;
            return c.BaseValue * tier * catalog.Rules.PopularityRatio(input.PopularityRow, id) / 100.0
                   * input.Forecast.ExpectedRatio(id, day, input.ProducedBefore.ElementAtOrDefault(id), 0) / 100.0;
        }

        var chains = ChainGenerator.TopChains(catalog, allowed, Weight, keep);

        // Chains of gatherable-only crafts never hit a hard cap, so every day keeps a few any state can run.
        var safe = ChainGenerator.TopChains(catalog, c => allowed(c) && c.Materials.All(m => catalog.KindOf(m.PouchRow) == MaterialKind.Gatherable), Weight, SafeChainsPerDay);
        chains.AddRange(safe.Where(c => !chains.Any(x => x.SequenceEqual(c))));
        var favorChains = new Dictionary<int, List<int[]>>();
        foreach (var f in input.Favors)
        {
            // A huge bonus on the favor craft makes the top chains exactly the ones that contain it.
            var withFavor = ChainGenerator.TopChains(catalog, allowed, id => Weight(id) + (id == f.CraftId ? 1e6 : 0), 10)
                .Where(chain => chain.Contains(f.CraftId))
                .ToList();
            favorChains[f.CraftId] = withFavor;
            chains.AddRange(withFavor.Where(c => !chains.Contains(c)));
        }

        return (chains, favorChains);
    }

    /// <summary>A craft whose hard-capped material is not on hand at all can never be scheduled.</summary>
    private static bool HardCapsAllow(CraftDef craft, PlannerInput input, CraftCatalog catalog) =>
        craft.Materials.All(m => catalog.KindOf(m.PouchRow) == MaterialKind.Gatherable
                                 || input.Materials.GetValueOrDefault(m.PouchRow) >= m.Amount);

    private static double Score(State s, PlannerInput input, CraftCatalog catalog, Dictionary<int, int> favorBaseline)
    {
        var favor = 0.0;
        foreach (var f in input.Favors)
            favor += Math.Min(s.Produced[f.CraftId] - favorBaseline[f.CraftId], f.Required);
        return s.Value - GatherCost(s, input, catalog) + favor * FavorWeight;
    }

    private static bool FavorsMet(State s, PlannerInput input, Dictionary<int, int> favorBaseline) =>
        input.Favors.All(f => s.Produced[f.CraftId] - favorBaseline[f.CraftId] >= f.Required);

    private static double GatherCost(State s, PlannerInput input, CraftCatalog catalog) =>
        GatherDemand(s, input, catalog).Values.Sum() * input.GatherCostPerUnit;

    private static Dictionary<int, int> GatherDemand(State s, PlannerInput input, CraftCatalog catalog)
    {
        var demand = new Dictionary<int, int>();
        for (var row = 0; row < s.MaterialsUsed.Length; row++)
        {
            var short_ = s.MaterialsUsed[row] - input.Materials.GetValueOrDefault(row);
            if (short_ > 0 && catalog.KindOf(row) == MaterialKind.Gatherable)
                demand[row] = short_;
        }

        return demand;
    }

    private static double Confidence(int day, int[] main, int[]? fourth, SupplyForecast forecast)
    {
        var crafts = fourth == null ? main : main.Concat(fourth).ToArray();
        return crafts.Length == 0 ? 0 : crafts.Min(c => forecast.Confidence(c, day));
    }

    private sealed class State
    {
        public State(int[] produced, int[] materialsUsed, int groove, int restsTaken, double value, List<PlannedDay> choices)
        {
            Produced = produced;
            MaterialsUsed = materialsUsed;
            Groove = groove;
            RestsTaken = restsTaken;
            Value = value;
            Choices = choices;
        }

        public int[] Produced { get; }
        public int[] MaterialsUsed { get; }
        public int Groove { get; }
        public int RestsTaken { get; }
        public double Value { get; }
        public List<PlannedDay> Choices { get; }

        public State Branch(int day, bool rest) =>
            new(Produced, MaterialsUsed, Groove, RestsTaken + 1, Value, new List<PlannedDay>(Choices) { new(day, true, null, 0, null, 0, 1) });

        /// <summary>The state after working this day, or null when it would overdraw a hard-capped material.</summary>
        public State? Work(int day, int[] main, int mainCount, int[]? fourth, double value, int groove, int[] today, double confidence, PlannerInput input, CraftCatalog catalog)
        {
            var produced = (int[])Produced.Clone();
            var used = (int[])MaterialsUsed.Clone();
            for (var c = 0; c < today.Length; c++)
            {
                if (today[c] == 0)
                    continue;

                produced[c] += today[c];
                var craft = catalog.Get(c)!;
                var units = input.MaterialsPerItem ? today[c] : CraftsFor(c, main, mainCount, fourth);
                foreach (var m in craft.Materials)
                {
                    if (m.PouchRow < used.Length)
                        used[m.PouchRow] += m.Amount * units;
                }
            }

            // Hard caps: a plan that needs more of a non-gatherable material than is on hand is not a plan.
            for (var row = 0; row < used.Length; row++)
            {
                if (used[row] > input.Materials.GetValueOrDefault(row) && catalog.KindOf(row) != MaterialKind.Gatherable)
                    return null;
            }

            return new State(produced, used, groove, RestsTaken, Value + value,
                new List<PlannedDay>(Choices) { new(day, false, main, mainCount, fourth, value, confidence) });
        }

        private static int CraftsFor(int craftId, int[] main, int mainCount, int[]? fourth) =>
            main.Count(c => c == craftId) * mainCount + (fourth?.Count(c => c == craftId) ?? 0);
    }
}
