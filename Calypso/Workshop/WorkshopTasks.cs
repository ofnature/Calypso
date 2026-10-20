using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Tasks;
using Calypso.Core.Workshop;
using Calypso.Game;

namespace Calypso.Workshop;

/// <summary>
/// Daily-loop task 1: read today's supply/demand into the week's observations — the evidence the peak inference
/// runs on. Done once per island day.
/// </summary>
public sealed class WorkshopObserveTask : IIslandTask
{
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(2);

    private readonly WorkshopService _service;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<IslandCycle> _cycle;
    private DateTime _requestedUtc;

    public WorkshopObserveTask(WorkshopService service, Func<CharacterProfile?> profile, Func<IslandCycle> cycle)
    {
        _service = service;
        _profile = profile;
        _cycle = cycle;
    }

    public string Id => "workshop.observe";
    public string Name => "Read workshop demand";
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        var profile = _profile();
        if (profile == null || profile.Workshop.Mode == WorkshopMode.Off)
            return TaskCheck.Done("workshop is off");
        if (!WorkshopService.HasWorkshop(island))
            return TaskCheck.Locked("no workshop built yet");
        if (profile.WorkshopWeek.Observed.ContainsKey(_cycle().Day))
            return TaskCheck.Done("read today");
        if (!island.InSanctuary)
            return TaskCheck.Blocked("not on the island");
        return TaskCheck.Ready();
    }

    public void Begin(TaskContext context)
    {
        _service.Game.RequestDemand();
        _requestedUtc = context.UtcNow;
    }

    public StepResult Step(TaskContext context)
    {
        // Give the request time to mark the data dirty and come back; reading straight away could see yesterday's.
        if (context.UtcNow - _requestedUtc < MinWait)
            return StepResult.Running("asking for demand data");

        if (_service.Game.ReadSupplyDemand() is not { } data)
            return StepResult.Running("waiting for demand data");

        var profile = _profile();
        if (profile == null)
            return StepResult.Failed("logged out");

        profile.WorkshopWeek.Observed[_cycle().Day] = data;
        return StepResult.Succeeded($"{data.Length} crafts read");
    }

    public void Abort()
    {
    }
}

/// <summary>
/// Daily-loop task 2: re-plan the rest of the week and — in Auto — write tomorrow into the in-game agenda (decided:
/// re-plan daily, commit only tomorrow). In Suggest it stops after planning. Done once per island day (ledger).
///
/// The last day of the week has nothing to plan this week: it sets next week's first cycle to rest (that day has no
/// demand evidence at all, and community schedules rest it) and leaves the real planning to that day's run.
/// </summary>
public sealed class WorkshopAgendaTask : IIslandTask
{
    private enum Phase
    {
        Open,
        ReadToday,
        Plan,
        ShowTomorrow,
        Commit,
        Verify,
    }

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    private readonly WorkshopService _service;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<IslandCycle> _cycle;

    private Phase _phase;
    private DateTime _phaseStartUtc;
    private uint? _restMask;
    private CyclePlan? _tomorrow;
    private bool _planStarted;

    public WorkshopAgendaTask(WorkshopService service, Func<CharacterProfile?> profile, Func<IslandCycle> cycle)
    {
        _service = service;
        _profile = profile;
        _cycle = cycle;
    }

    public string Id => "workshop.agenda";
    public string Name => "Plan workshop agenda";
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        var profile = _profile();
        if (profile == null || profile.Workshop.Mode == WorkshopMode.Off)
            return TaskCheck.Done("workshop is off");
        if (!WorkshopService.HasWorkshop(island))
            return TaskCheck.Locked("no workshop built yet");
        if (ledger.IsCompleted(Id))
            return TaskCheck.Done(profile.Workshop.Mode == WorkshopMode.Suggest ? "planned today (suggest only)" : "tomorrow is set");
        if (!profile.WorkshopWeek.Observed.ContainsKey(_cycle().Day) && _cycle().Day < 6)
            return TaskCheck.Blocked("needs today's demand reading first");
        if (profile.Workshop.Mode == WorkshopMode.Auto && !island.InSanctuary)
            return TaskCheck.Blocked("not on the island");
        return TaskCheck.Ready();
    }

    public void Begin(TaskContext context)
    {
        _restMask = null;
        _tomorrow = null;
        _planStarted = false;
        var auto = _profile()?.Workshop.Mode == WorkshopMode.Auto;
        Enter(auto ? Phase.Open : Phase.Plan, context);
    }

    public StepResult Step(TaskContext context)
    {
        var profile = _profile();
        if (profile == null)
            return StepResult.Failed("logged out");

        var game = _service.Game;
        var today = _cycle().Day;
        var auto = profile.Workshop.Mode == WorkshopMode.Auto;

        switch (_phase)
        {
            case Phase.Open:
                if (!game.OpenAgenda() || game.ReadAgenda() == null)
                {
                    return context.UtcNow - _phaseStartUtc > TimeSpan.FromSeconds(10)
                        ? StepResult.Failed("the agenda did not open (it may need the Tactful Taskmaster)")
                        : StepResult.Running("opening the agenda");
                }

                game.DisplayCycle(today);
                Enter(Phase.ReadToday, context);
                return StepResult.Running("reading today's schedule");

            case Phase.ReadToday:
            {
                if (context.UtcNow - _phaseStartUtc < SettleTime)
                    return StepResult.Running("reading today's schedule");
                if (game.ReadAgenda() is not { } view || view.CycleDisplayed != today)
                    return StepResult.Running("waiting for today's schedule");

                _restMask = view.RestCycles;
                profile.WorkshopWeek.LastGroove = view.Groove;
                if (!profile.WorkshopWeek.Produced.ContainsKey(today))
                    profile.WorkshopWeek.Produced[today] = ItemsFrom(view);

                if (today == 6)
                    return SetNextWeekFirstRest(view.RestCycles, game);

                Enter(Phase.Plan, context);
                return StepResult.Running("planning");
            }

            case Phase.Plan:
            {
                if (today == 6)
                    return StepResult.Succeeded("last day of the week: nothing left to plan this week");

                if (!_planStarted)
                {
                    var input = _service.BuildInput(context.Island, profile, today + 1, _restMask);
                    if (!_service.StartPlan(input, profile.WorkshopWeek.Week, today))
                        return StepResult.Running("waiting for another plan to finish");
                    _planStarted = true;
                    return StepResult.Running("planning");
                }

                if (_service.TakeResult() is not { } plan)
                {
                    return _service.IsPlanning
                        ? StepResult.Running("planning")
                        : StepResult.Failed(string.IsNullOrEmpty(_service.LastError) ? "planning produced nothing" : _service.LastError);
                }

                _planStarted = false;
                profile.WorkshopWeek.Plan = plan;
                if (!auto)
                    return StepResult.Succeeded($"planned {plan.ExpectedValue:0} cowries for the rest of the week");

                _tomorrow = plan.Day(today + 1);
                if (_tomorrow == null)
                    return StepResult.Failed("the plan has no entry for tomorrow");

                game.DisplayCycle(today + 1);
                Enter(Phase.ShowTomorrow, context);
                return StepResult.Running("showing tomorrow");
            }

            case Phase.ShowTomorrow:
            {
                if (context.UtcNow - _phaseStartUtc < SettleTime)
                    return StepResult.Running("showing tomorrow");
                if (game.ReadAgenda() is not { } view || view.CycleDisplayed != today + 1)
                    return StepResult.Running("waiting for tomorrow's schedule");
                if (!view.DisplayedCycleEmpty)
                    return StepResult.Failed("tomorrow already has crafts scheduled — clear it, or use Suggest mode");

                Enter(Phase.Commit, context);
                return StepResult.Running("setting tomorrow");
            }

            case Phase.Commit:
            {
                var plan = profile.WorkshopWeek.Plan!;
                var mask = RestMask(_restMask ?? 0, plan, today + 1);
                if (mask != (_restMask ?? 0))
                    game.SetRestCycles(mask);

                if (!_tomorrow!.Rest)
                {
                    var built = context.Island.Workshops.Select((w, i) => (w, i)).Where(x => x.w.IsBuilt).Select(x => x.i).ToList();
                    var chains = _tomorrow.ChainsFor(built.Count);
                    for (var k = 0; k < built.Count; k++)
                    {
                        if (chains[k] is { Count: > 0 } chain)
                            game.ScheduleChain(today + 1, built[k], chain, _service.Catalog);
                    }
                }

                Enter(Phase.Verify, context);
                return StepResult.Running("checking tomorrow");
            }

            case Phase.Verify:
            {
                if (context.UtcNow - _phaseStartUtc < SettleTime)
                    return StepResult.Running("checking tomorrow");

                var view = game.ReadAgenda();
                var expected = _tomorrow!.Rest ? 0 : (_tomorrow.Main?.Count ?? 0);
                if (view == null || view.CycleDisplayed != today + 1 || (expected > 0 && view.Workshops.All(w => w.Count == 0)))
                {
                    return context.UtcNow - _phaseStartUtc > TimeSpan.FromSeconds(8)
                        ? StepResult.Failed("tomorrow's agenda did not show the crafts after scheduling")
                        : StepResult.Running("checking tomorrow");
                }

                var built = context.Island.Workshops.Count(w => w.IsBuilt);
                _tomorrow.Committed = true;
                profile.WorkshopWeek.Produced[today + 1] = _tomorrow.Items(built, _service.Catalog.MaxCraftId);
                return StepResult.Succeeded(_tomorrow.Rest ? "tomorrow set to rest" : $"tomorrow set ({_tomorrow.Value:0} cowries expected)");
            }
        }

        return StepResult.Failed("unknown phase");
    }

    public void Abort()
    {
        _service.Cancel();
        _planStarted = false;
    }

    private void Enter(Phase phase, TaskContext context)
    {
        _phase = phase;
        _phaseStartUtc = context.UtcNow;
    }

    /// <summary>This week's rest bits: kept as-is before <paramref name="firstDay"/>, from the plan after; next week's untouched.</summary>
    internal static uint RestMask(uint current, WeekPlan plan, int firstDay)
    {
        var mask = current & ~(((1u << 7) - 1) & ~((1u << firstDay) - 1));
        foreach (var c in plan.Cycles.Where(c => c.Rest && c.Day >= firstDay && c.Day < 7))
            mask |= 1u << c.Day;
        return mask;
    }

    private StepResult SetNextWeekFirstRest(uint current, WorkshopGame game)
    {
        const uint nextWeekFirst = 1u << 7;
        if ((current & nextWeekFirst) == 0)
            game.SetRestCycles(current | nextWeekFirst);
        return StepResult.Succeeded("last day: next week's first cycle set to rest");
    }

    private int[] ItemsFrom(AgendaView view)
    {
        var items = new int[_service.Catalog.MaxCraftId + 1];
        foreach (var ws in view.Workshops)
        {
            for (var i = 0; i < ws.Count; i++)
            {
                if (ws[i].Craft > 0 && ws[i].Craft < items.Length)
                    items[ws[i].Craft] += i == 0 ? 1 : 2; // assumes the schedule is one efficiency chain (Calypso's are)
            }
        }

        return items;
    }
}
