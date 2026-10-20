using System;

namespace Calypso.Core.Tasks;

/// <summary>
/// One step of "do my island" (collect granary, set agenda, gather for the plan, ...).
///
/// Contract — this is what makes resume work without a saved step index:
/// - <see cref="Check"/> is PURE and decides from live state (plus the ledger for unobservable facts) whether
///   the task still has work. A task that is already done must say <see cref="TaskState.Done"/> no matter how
///   many times it is asked, so re-running the loop after a crash, reload or stop simply skips finished work.
/// - <see cref="Check"/> must return <see cref="TaskState.Locked"/> when the island rank/progress has not
///   unlocked the feature (gate on <see cref="IslandSnapshot.IsFunctionUnlocked"/>), never try and fail.
/// - <see cref="Step"/> is called once per framework tick while the task runs; it must not block.
/// </summary>
public interface IIslandTask
{
    /// <summary>Stable id — used by the ledger, the per-character enable list and IPC <c>Calypso.RunTask</c>. Never rename.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Active running time (ticks spent waiting on a global blocker don't count) before the runner gives up.</summary>
    TimeSpan Timeout { get; }

    TaskCheck Check(IslandSnapshot island, CycleLedger ledger);

    /// <summary>Called once when the runner picks this task, before the first <see cref="Step"/>.</summary>
    void Begin(TaskContext context);

    StepResult Step(TaskContext context);

    /// <summary>Stop whatever is in flight (movement, open windows). Called on stop, timeout and exceptions.</summary>
    void Abort();
}

public enum TaskState
{
    /// <summary>Not unlocked at the current island rank/progress.</summary>
    Locked,

    /// <summary>Nothing left to do this cycle.</summary>
    Done,

    /// <summary>Has work and can start now.</summary>
    Ready,

    /// <summary>Has work but can't start (e.g. needs materials, not on the island and no way there yet).</summary>
    Blocked,
}

public readonly record struct TaskCheck(TaskState State, string Reason)
{
    public static TaskCheck Ready(string reason = "") => new(TaskState.Ready, reason);
    public static TaskCheck Done(string reason = "") => new(TaskState.Done, reason);
    public static TaskCheck Locked(string reason) => new(TaskState.Locked, reason);
    public static TaskCheck Blocked(string reason) => new(TaskState.Blocked, reason);
}

public enum StepOutcome
{
    Running,
    Succeeded,
    Failed,
}

public readonly record struct StepResult(StepOutcome Outcome, string Detail)
{
    public static StepResult Running(string detail = "") => new(StepOutcome.Running, detail);
    public static StepResult Succeeded(string detail = "") => new(StepOutcome.Succeeded, detail);
    public static StepResult Failed(string reason) => new(StepOutcome.Failed, reason);
}

public sealed record TaskContext(IslandSnapshot Island, CycleLedger Ledger, DateTime UtcNow);
