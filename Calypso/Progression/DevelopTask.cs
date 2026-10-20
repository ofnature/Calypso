using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Development;
using Calypso.Core.Tasks;
using Calypso.Game;
using Calypso.Gathering;

namespace Calypso.Progression;

/// <summary>
/// Daily-loop task after the tutorial: island development (user request 2026-10-19). Works down the
/// <see cref="DevelopmentPlanner"/>'s list: gathers what the next tasks need (also for the ones Calypso can't do itself
/// yet, so the materials are there when you do them by hand), crafts tools, and constructs new workshops. Runs only
/// while the tutorial has nothing to do itself.
/// </summary>
public sealed class DevelopTask : IIslandTask
{
    private readonly DevelopmentData _dev;
    private readonly ProgressionTask _tutorial;
    private readonly IslandLayout _layout;
    private readonly GatherRun _gather;
    private readonly CraftRun _craft;
    private readonly ConstructRun _construct;
    private readonly Func<int, int> _pouchCount;

    private DevelopmentTask? _current;
    private bool _gathering;
    private int _done;
    private readonly Dictionary<string, int> _gathers = new();

    public DevelopTask(DevelopmentData dev, ProgressionTask tutorial, IslandLayout layout, GatherRun gather, CraftRun craft, ConstructRun construct,
        Func<int, int> pouchCount)
    {
        _dev = dev;
        _tutorial = tutorial;
        _layout = layout;
        _gather = gather;
        _craft = craft;
        _construct = construct;
        _pouchCount = pouchCount;
    }

    public string Id => "progress.develop";
    public string Name => "Develop the island";
    public TimeSpan Timeout => TimeSpan.FromMinutes(45);

    public IReadOnlyList<DevelopmentTask> Plan(IslandSnapshot island) => _dev.Plan(island, _pouchCount);

    /// <summary>Calypso acts on: anything it can do, and gathering for anything waiting only on materials or a recording.</summary>
    private static bool Actionable(DevelopmentTask t) =>
        t.Kind != DevelopmentKind.Landmark && (t.State is DevelopmentState.Ready or DevelopmentState.NeedsMaterials
                                                || (t.State == DevelopmentState.NeedsRecording && t.Missing.Count > 0));

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        if (!UnlockTask.Unlocked || !island.Available)
            return TaskCheck.Blocked("the island isn't unlocked yet");
        if (_tutorial.Check(island, ledger).State == TaskState.Ready)
            return TaskCheck.Blocked("the tutorial comes first");

        var plan = Plan(island);
        if (plan.FirstOrDefault(Actionable) is { } next)
        {
            if (!island.InSanctuary)
                return TaskCheck.Blocked("not on the island");
            return TaskCheck.Ready(Describe(next));
        }

        var byHand = plan.Where(t => t.State == DevelopmentState.NeedsRecording).Select(t => t.Name).Distinct().ToList();
        if (byHand.Count > 0)
            return TaskCheck.Blocked($"materials ready — do by hand: {string.Join(", ", byHand.Take(4))}{(byHand.Count > 4 ? "…" : "")}");
        var waiting = plan.FirstOrDefault(t => t.State == DevelopmentState.Waiting);
        return TaskCheck.Done(waiting == null ? "nothing to develop right now" : $"waiting: {waiting.Name} ({waiting.Note})");
    }

    public void Begin(TaskContext context)
    {
        _done = 0;
        _gathers.Clear();
        Start(context);
    }

    public StepResult Step(TaskContext context)
    {
        if (_current == null)
            return Finish();

        StepResult sub;
        if (_gathering)
            sub = _gather.Step(context.UtcNow);
        else if (_current.Kind == DevelopmentKind.CraftTool)
            sub = _craft.Step(context.UtcNow);
        else
            sub = _construct.Step(context.Island, context.UtcNow);

        if (sub.Outcome == StepOutcome.Running)
            return StepResult.Running($"{_current.Name}: {sub.Detail}");
        if (sub.Outcome == StepOutcome.Failed)
            return StepResult.Failed($"{_current.Name}: {sub.Detail}");

        if (!_gathering)
            _done++;
        Start(context);
        if (_current != null && _gathering && _gathers.GetValueOrDefault(_current.Name) > 2)
            return StepResult.Failed($"{_current.Name}: still short of materials after gathering twice ({string.Join(", ", _current.Missing.Select(m => $"pouch #{m.PouchRow} ×{m.Missing}"))})");
        return _current == null ? Finish() : StepResult.Running(Describe(_current));
    }

    public void Abort()
    {
        _gather.Abort();
        _craft.Abort();
        _construct.Abort();
    }

    private void Start(TaskContext context)
    {
        _current = Plan(context.Island).FirstOrDefault(Actionable);
        if (_current == null)
            return;

        _gathering = _current.Missing.Count > 0;
        if (_gathering)
        {
            _gathers[_current.Name] = _gathers.GetValueOrDefault(_current.Name) + 1;
            _gather.Begin(_current.Missing.ToDictionary(m => m.PouchRow, m => _pouchCount(m.PouchRow) + m.Missing), context.UtcNow);
            return;
        }

        if (_current.Kind == DevelopmentKind.CraftTool)
            _craft.Begin(_current.Row, _current.Name, context.UtcNow);
        else if (_current.Kind == DevelopmentKind.Construct && _layout.Buildings(context.Island).FirstOrDefault(b => b.Building == _current.Row) is { } b)
            _construct.Begin(b with { Done = false }, context.Island, context.UtcNow);
        else
            _current = null; // a by-hand task with its materials ready: nothing more for Calypso to do
    }

    private StepResult Finish() => StepResult.Succeeded(_done == 0 ? "materials gathered; nothing left Calypso can do itself" : $"did {_done} development task(s)");

    private static string Describe(DevelopmentTask t) => t.Missing.Count > 0
        ? $"gather for {t.Name}"
        : t.Kind == DevelopmentKind.CraftTool ? $"craft {t.Name}" : $"construct {t.Name}";
}
