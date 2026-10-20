using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Tasks;

public enum RunnerState
{
    Idle,
    Running,

    /// <summary>Started, but a global blocker (combat, cutscene, zoning, not logged in) holds every step.</summary>
    Waiting,
}

/// <summary>
/// The daily-loop state machine. Pure: the plugin feeds it a <see cref="TaskContext"/> and the current global
/// blocker every framework tick. The plugin rolls the ledger to the current cycle before ticking.
///
/// Loop mode walks the registered tasks IN ORDER and runs the first one that is enabled for this character,
/// <see cref="TaskState.Ready"/>, and under the per-cycle failure cap; when none qualifies the loop is finished.
/// Single mode runs one named task once (IPC <c>Calypso.RunTask</c>, or a button), ignoring the enable list and
/// failure cap because a person or plugin asked for it explicitly.
///
/// Resume = start the loop again. Tasks re-derive "done" from live state, so nothing is replayed. A task that
/// succeeded is not picked again in the same run even if its Check still says Ready — that guards against a
/// task whose Check is wrong spinning forever.
/// </summary>
public sealed class TaskRunner
{
    public const int DefaultMaxFailuresPerCycle = 3;
    private const int HistoryLength = 30;

    private readonly IReadOnlyList<IIslandTask> _tasks;
    private readonly int _maxFailuresPerCycle;
    private readonly List<RunRecord> _history = new();
    private readonly HashSet<string> _succeededThisRun = new(StringComparer.Ordinal);

    private Func<string, bool> _isEnabled = _ => true;
    private string? _singleTaskId;
    private bool _singleTaskStarted;
    private TimeSpan _elapsed;

    public TaskRunner(IReadOnlyList<IIslandTask> tasks, int maxFailuresPerCycle = DefaultMaxFailuresPerCycle)
    {
        var duplicate = tasks.GroupBy(t => t.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"Duplicate task id '{duplicate.Key}'.", nameof(tasks));

        _tasks = tasks;
        _maxFailuresPerCycle = maxFailuresPerCycle;
    }

    public IReadOnlyList<IIslandTask> Tasks => _tasks;
    public RunnerState State { get; private set; } = RunnerState.Idle;
    public bool IsBusy => State != RunnerState.Idle;
    public IIslandTask? Current { get; private set; }

    /// <summary>True while running a single task rather than the daily loop.</summary>
    public bool IsSingleRun => _singleTaskId != null;

    /// <summary>What the runner is doing right now, for status lines and IPC.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>How the last run ended ("Daily loop finished", "Stopped: ...").</summary>
    public string LastResult { get; private set; } = string.Empty;

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<RunRecord> History => _history;

    /// <summary>Raised when a task finishes (taskId, succeeded, detail).</summary>
    public event Action<string, bool, string>? TaskFinished;

    public bool StartLoop(Func<string, bool> isEnabled)
    {
        if (IsBusy)
            return false;

        _isEnabled = isEnabled;
        _singleTaskId = null;
        BeginRun("Starting daily loop");
        return true;
    }

    public bool StartSingle(string taskId)
    {
        if (IsBusy || _tasks.All(t => t.Id != taskId))
            return false;

        _isEnabled = _ => true;
        _singleTaskId = taskId;
        _singleTaskStarted = false;
        BeginRun($"Starting {taskId}");
        return true;
    }

    public void Stop(string reason = "Stopped")
    {
        if (!IsBusy)
            return;

        AbortCurrent();
        Finish(reason);
    }

    public void Tick(TaskContext context, string? blocker, TimeSpan delta)
    {
        if (!IsBusy)
            return;

        if (blocker != null)
        {
            State = RunnerState.Waiting;
            Detail = blocker;
            return;
        }

        State = RunnerState.Running;

        if (Current == null && !PickNext(context))
            return;

        _elapsed += delta;
        var task = Current!;

        if (_elapsed > task.Timeout)
        {
            AbortCurrent();
            Complete(task, context, false, $"timed out after {task.Timeout.TotalSeconds:0}s");
            return;
        }

        StepResult result;
        try
        {
            result = task.Step(context);
        }
        catch (Exception ex)
        {
            AbortCurrent();
            Complete(task, context, false, $"error: {ex.Message}");
            return;
        }

        switch (result.Outcome)
        {
            case StepOutcome.Running:
                Detail = string.IsNullOrEmpty(result.Detail) ? task.Name : $"{task.Name}: {result.Detail}";
                break;
            case StepOutcome.Succeeded:
                Complete(task, context, true, result.Detail);
                break;
            case StepOutcome.Failed:
                AbortCurrent();
                Complete(task, context, false, result.Detail);
                break;
        }
    }

    /// <summary>Each task's check against the given state, in run order — what the Daily Loop tab and status JSON show.</summary>
    public IEnumerable<(IIslandTask Task, TaskCheck Check)> Evaluate(IslandSnapshot island, CycleLedger ledger) =>
        _tasks.Select(t => (t, SafeCheck(t, island, ledger)));

    private bool PickNext(TaskContext context)
    {
        IIslandTask? next = null;

        if (_singleTaskId != null)
        {
            if (_singleTaskStarted)
            {
                Finish(LastResult);
                return false;
            }

            var task = _tasks.First(t => t.Id == _singleTaskId);
            var check = SafeCheck(task, context.Island, context.Ledger);
            if (check.State != TaskState.Ready)
            {
                Finish($"{task.Name}: {Describe(check)}");
                return false;
            }

            _singleTaskStarted = true;
            next = task;
        }
        else
        {
            next = _tasks.FirstOrDefault(t =>
                _isEnabled(t.Id)
                && !_succeededThisRun.Contains(t.Id)
                && context.Ledger.Failures(t.Id) < _maxFailuresPerCycle
                && SafeCheck(t, context.Island, context.Ledger).State == TaskState.Ready);

            if (next == null)
            {
                // [REPLACED: tasks skipped by the failure cap vanished silently — Saar's "Do my island did nothing", 2026-10-19]
                // Finish("Daily loop finished");
                var capped = _tasks
                    .Where(t => _isEnabled(t.Id) && !_succeededThisRun.Contains(t.Id) && context.Ledger.Failures(t.Id) >= _maxFailuresPerCycle)
                    .Select(t => t.Name)
                    .ToList();
                Finish(capped.Count == 0
                    ? "Daily loop finished"
                    : $"Daily loop finished — skipped after {_maxFailuresPerCycle} failures today: {string.Join(", ", capped)} (press Do my island to retry)");
                return false;
            }
        }

        Current = next;
        _elapsed = TimeSpan.Zero;
        Detail = next.Name;

        try
        {
            next.Begin(context);
        }
        catch (Exception ex)
        {
            AbortCurrent();
            Complete(next, context, false, $"error starting: {ex.Message}");
            return false;
        }

        return true;
    }

    private void Complete(IIslandTask task, TaskContext context, bool ok, string detail)
    {
        if (ok)
        {
            context.Ledger.MarkCompleted(task.Id, context.UtcNow);
            _succeededThisRun.Add(task.Id);
        }
        else
        {
            context.Ledger.RecordFailure(task.Id, detail, context.UtcNow);
        }

        Current = null;
        LastResult = ok
            ? $"{task.Name}: done{(string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})")}"
            : $"{task.Name}: failed ({detail})";

        _history.Insert(0, new RunRecord(task.Id, task.Name, ok, detail, context.UtcNow));
        if (_history.Count > HistoryLength)
            _history.RemoveAt(_history.Count - 1);

        TaskFinished?.Invoke(task.Id, ok, detail);
    }

    private void BeginRun(string detail)
    {
        _succeededThisRun.Clear();
        _elapsed = TimeSpan.Zero;
        Current = null;
        State = RunnerState.Running;
        Detail = detail;
    }

    private void Finish(string result)
    {
        Current = null;
        _singleTaskId = null;
        State = RunnerState.Idle;
        Detail = string.Empty;
        LastResult = result;
    }

    private void AbortCurrent()
    {
        try
        {
            Current?.Abort();
        }
        catch
        {
            // Abort is best-effort cleanup; a throwing abort must not wedge the runner.
        }
    }

    private static TaskCheck SafeCheck(IIslandTask task, IslandSnapshot island, CycleLedger ledger)
    {
        try
        {
            return task.Check(island, ledger);
        }
        catch (Exception ex)
        {
            return TaskCheck.Blocked($"check failed: {ex.Message}");
        }
    }

    private static string Describe(TaskCheck check) =>
        string.IsNullOrEmpty(check.Reason) ? check.State.ToString().ToLowerInvariant() : check.Reason;
}

public sealed record RunRecord(string TaskId, string TaskName, bool Succeeded, string Detail, DateTime Utc);
