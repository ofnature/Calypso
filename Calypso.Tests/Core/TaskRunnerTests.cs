using Calypso.Core;
using Calypso.Core.Tasks;

namespace Calypso.Tests.Core;

public class TaskRunnerTests
{
    private static readonly DateTime T = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private readonly CycleLedger _ledger = new();
    private readonly IslandSnapshot _island = new() { Available = true };

    private TaskContext Ctx() => new(_island, _ledger, T);

    [Fact]
    public void LoopRunsReadyTasksInOrderAndSkipsDoneOrLocked()
    {
        var locked = new FakeTask("locked") { State = TaskState.Locked };
        var done = new FakeTask("done") { State = TaskState.Done };
        var a = new FakeTask("a") { StepsToFinish = 2 };
        var b = new FakeTask("b");
        var runner = new TaskRunner(new IIslandTask[] { locked, done, a, b });

        Assert.True(runner.StartLoop(_ => true));
        Tick(runner, 10);

        Assert.Equal(new[] { "a", "b" }, runner.History.Reverse().Select(r => r.TaskId));
        Assert.Equal(0, locked.Begins + done.Begins);
        Assert.False(runner.IsBusy);
        Assert.Equal("Daily loop finished", runner.LastResult);
    }

    [Fact]
    public void ResumeAfterStopSkipsWhatLiveStateSaysIsDone()
    {
        // The Theseus model: no saved index — a finished task reports Done from state, so a re-run skips it.
        var a = new FakeTask("a") { BecomesDoneOnSuccess = true };
        var b = new FakeTask("b") { StepsToFinish = 100 };
        var runner = new TaskRunner(new IIslandTask[] { a, b });

        runner.StartLoop(_ => true);
        Tick(runner, 5);
        Assert.Equal("b", runner.Current?.Id);
        runner.Stop();
        Assert.Equal(1, b.Aborts);

        runner.StartLoop(_ => true);
        Tick(runner, 1);
        Assert.Equal(1, a.Begins);
        Assert.Equal("b", runner.Current?.Id);
    }

    [Fact]
    public void DisabledTasksAreSkippedInLoopButRunnableSingly()
    {
        var a = new FakeTask("a");
        var runner = new TaskRunner(new IIslandTask[] { a });

        runner.StartLoop(id => id != "a");
        Tick(runner, 3);
        Assert.Equal(0, a.Begins);

        Assert.True(runner.StartSingle("a"));
        Tick(runner, 3);
        Assert.Equal(1, a.Begins);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public void FailuresAreCappedPerCycle()
    {
        var a = new FakeTask("a") { FailEveryRun = true };
        var runner = new TaskRunner(new IIslandTask[] { a }, maxFailuresPerCycle: 3);

        runner.StartLoop(_ => true);
        Tick(runner, 20);

        Assert.Equal(3, a.Begins);
        Assert.Equal(3, _ledger.Failures("a"));
        Assert.Equal(3, a.Aborts);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public void ACappedTaskIsNamedWhenTheLoopSkipsItAndRunsAgainOnceFailuresAreCleared()
    {
        var a = new FakeTask("a") { FailEveryRun = true };
        var runner = new TaskRunner(new IIslandTask[] { a }, maxFailuresPerCycle: 1);
        runner.StartLoop(_ => true);
        Tick(runner, 5);

        // Saar 2026-10-19: "Do my island did nothing" — the tutorial was at the cap and nothing said so.
        runner.StartLoop(_ => true);
        Tick(runner, 5);
        Assert.Equal(1, a.Begins);
        Assert.Contains("skipped after 1 failures today", runner.LastResult);

        _ledger.ClearFailures(); // what a hand-pressed "Do my island" does first
        runner.StartLoop(_ => true);
        Tick(runner, 5);
        Assert.Equal(2, a.Begins);
    }

    [Fact]
    public void SucceededTaskIsNotRepeatedInTheSameRunEvenIfCheckStillSaysReady()
    {
        var a = new FakeTask("a"); // State stays Ready forever: a buggy Check
        var runner = new TaskRunner(new IIslandTask[] { a });

        runner.StartLoop(_ => true);
        Tick(runner, 10);

        Assert.Equal(1, a.Begins);
        Assert.True(_ledger.IsCompleted("a"));
    }

    [Fact]
    public void BlockerHoldsStepsAndDoesNotCountTowardTimeout()
    {
        var a = new FakeTask("a") { StepsToFinish = 1000, Timeout = TimeSpan.FromMilliseconds(100) };
        var runner = new TaskRunner(new IIslandTask[] { a });

        runner.StartLoop(_ => true);
        for (var i = 0; i < 50; i++)
            runner.Tick(Ctx(), "In combat", Frame);

        Assert.Equal(RunnerState.Waiting, runner.State);
        Assert.Equal("In combat", runner.Detail);
        Assert.Equal(0, a.Steps);

        Tick(runner, 3);
        Assert.Equal(RunnerState.Running, runner.State);
        Assert.Equal(0, _ledger.Failures("a"));
    }

    [Fact]
    public void TimeoutAbortsAndRecordsFailure()
    {
        var a = new FakeTask("a") { StepsToFinish = 1000, Timeout = TimeSpan.FromMilliseconds(50) };
        var runner = new TaskRunner(new IIslandTask[] { a }, maxFailuresPerCycle: 1);

        runner.StartLoop(_ => true);
        Tick(runner, 10);

        Assert.Equal(1, a.Aborts);
        Assert.Contains("timed out", _ledger.Get("a")!.LastError);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public void ThrowingStepIsAFailureNotACrash()
    {
        var a = new FakeTask("a") { ThrowOnStep = true };
        var runner = new TaskRunner(new IIslandTask[] { a }, maxFailuresPerCycle: 1);

        runner.StartLoop(_ => true);
        Tick(runner, 3);

        Assert.Equal(1, _ledger.Failures("a"));
        Assert.StartsWith("error:", _ledger.Get("a")!.LastError);
    }

    [Fact]
    public void SingleRunOfNotReadyTaskFinishesWithItsReason()
    {
        var a = new FakeTask("a") { State = TaskState.Locked, Reason = "needs rank 5" };
        var runner = new TaskRunner(new IIslandTask[] { a });

        Assert.True(runner.StartSingle("a"));
        Tick(runner, 1);

        Assert.False(runner.IsBusy);
        Assert.Contains("needs rank 5", runner.LastResult);
        Assert.Equal(0, a.Begins);
    }

    [Fact]
    public void StartRefusedWhileBusyOrForUnknownTask()
    {
        var a = new FakeTask("a") { StepsToFinish = 100 };
        var runner = new TaskRunner(new IIslandTask[] { a });

        Assert.False(runner.StartSingle("nope"));
        Assert.True(runner.StartLoop(_ => true));
        Assert.False(runner.StartLoop(_ => true));
        Assert.False(runner.StartSingle("a"));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new TaskRunner(new IIslandTask[] { new FakeTask("x"), new FakeTask("x") }));
    }

    private void Tick(TaskRunner runner, int frames)
    {
        for (var i = 0; i < frames; i++)
            runner.Tick(Ctx(), null, Frame);
    }

    private sealed class FakeTask : IIslandTask
    {
        private int _stepsThisRun;

        public FakeTask(string id) => Id = id;

        public string Id { get; }
        public string Name => Id;
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);

        public TaskState State { get; set; } = TaskState.Ready;
        public string Reason { get; init; } = string.Empty;
        public int StepsToFinish { get; init; } = 1;
        public bool BecomesDoneOnSuccess { get; init; }
        public bool FailEveryRun { get; init; }
        public bool ThrowOnStep { get; init; }

        public int Begins { get; private set; }
        public int Steps { get; private set; }
        public int Aborts { get; private set; }

        public TaskCheck Check(IslandSnapshot island, CycleLedger ledger) => new(State, Reason);

        public void Begin(TaskContext context)
        {
            Begins++;
            _stepsThisRun = 0;
        }

        public StepResult Step(TaskContext context)
        {
            Steps++;
            if (ThrowOnStep)
                throw new InvalidOperationException("boom");
            if (FailEveryRun)
                return StepResult.Failed("nope");
            if (++_stepsThisRun < StepsToFinish)
                return StepResult.Running($"{_stepsThisRun}/{StepsToFinish}");
            if (BecomesDoneOnSuccess)
                State = TaskState.Done;
            return StepResult.Succeeded();
        }

        public void Abort() => Aborts++;
    }
}
