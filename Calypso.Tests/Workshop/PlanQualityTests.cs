using System.Diagnostics;
using Calypso.Core.Workshop;
using Xunit.Abstractions;

namespace Calypso.Tests.Workshop;

/// <summary>
/// Plan quality on seeded weeks (<see cref="Scenarios"/>: rest day 0, plan days 1-6 with day 0 observed, 4 workshops at
/// tier V, rank 20).
/// </summary>
public class PlanQualityTests
{
    private readonly ITestOutputHelper _out;

    public PlanQualityTests(ITestOutputHelper output) => _out = output;

    private static readonly PlannerLimits Previous = new() { ChainsPerDay = 150 };

    [Fact]
    public void TheWiderCandidatePoolPlansBetterWeeks()
    {
        // ChainsPerDay 150 → 4000 (2026-10-27): the 150 chains ranked by an additive per-craft weight missed chains that
        // only pay off with groove and same-day supply; ranking 4000 for each state finds them, still inside the budget.
        var gains = new List<double>();
        foreach (var seed in Enumerable.Range(101, 10))
        {
            var before = WeekPlanner.Plan(Scenarios.Week(seed, limits: Previous)).ExpectedValue;
            var watch = Stopwatch.StartNew();
            var after = WeekPlanner.Plan(Scenarios.Week(seed)).ExpectedValue;
            _out.WriteLine($"seed {seed}: {before:0} → {after:0} ({watch.ElapsedMilliseconds} ms)");

            Assert.True(after >= before * 0.995, $"seed {seed}: {after:0} < {before:0}");
            gains.Add((after - before) / before);
        }

        Assert.True(gains.Average() >= 0.01, $"average gain {gains.Average():P2}");
    }

    [Fact(Skip = "Slow reference (about 25 s a week): run by hand to measure plan quality.")]
    public void CompareWithSlowerReferences()
    {
        // (a) the same beam search far wider and with no time limit; (b) day by day, every chain in the full space
        // (~1.4M at rank 20) tried as the main chain in all four workshops, the best 200 paired for the fourth, on the
        // planner's own rest days, greedy (no look-ahead).
        var cat = Fixture.Catalog;
        var all = ChainGenerator.Generate(cat, c => c.RankReq <= 20);
        foreach (var seed in Enumerable.Range(101, 20))
        {
            var input = Scenarios.Week(seed);
            var plan = WeekPlanner.Plan(input);
            var wider = WeekPlanner.Plan(Scenarios.Week(seed, limits: new PlannerLimits
            {
                ChainsPerDay = 4000, ChainsPerState = 40, BeamWidth = 200, TimeBudget = TimeSpan.FromMinutes(10),
            }));

            var evaluator = new DayEvaluator(cat, input.Forecast, input.PopularityRow, input.MaxGroove);
            var tiers = new[] { 5, 5, 5, 5 };
            var produced = (int[])input.ProducedBefore.Clone();
            var groove = input.StartGroove;
            var greedy = 0.0;
            var scratch = new int[cat.MaxCraftId + 1];
            foreach (var day in plan.Days.Where(d => !d.Rest))
            {
                double Eval(int[] main, int[] fourth)
                {
                    Array.Clear(scratch);
                    return evaluator.Evaluate(day.Day, new[] { main, main, main, fourth }, tiers, groove, produced, scratch).Value;
                }

                var top = all.Select(c => (c, v: Eval(c, c))).OrderByDescending(x => x.v).Take(200).Select(x => x.c).ToList();
                var best = (Main: top[0], Fourth: top[0], V: Eval(top[0], top[0]));
                foreach (var a in top)
                {
                    foreach (var b in top)
                    {
                        var v = Eval(a, b);
                        if (v > best.V)
                            best = (a, b, v);
                    }
                }

                var today = new int[cat.MaxCraftId + 1];
                var (value, endGroove) = evaluator.Evaluate(day.Day, new[] { best.Main, best.Main, best.Main, best.Fourth }, tiers, groove, produced, today);
                greedy += value;
                groove = endGroove;
                for (var c = 0; c < today.Length; c++)
                    produced[c] += today[c];
            }

            _out.WriteLine($"seed {seed}: planner {plan.ExpectedValue:0}, wider {wider.ExpectedValue:0}, day-exhaustive greedy {greedy:0}");
        }
    }
}
