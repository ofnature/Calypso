using Calypso.Core;
using Calypso.Core.Tasks;

namespace Calypso.Tests.Core;

public class CycleLedgerTests
{
    private static readonly DateTime T = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RollWipesEntriesOnlyWhenTheCycleChanges()
    {
        var ledger = new CycleLedger();
        Assert.True(ledger.Roll(new IslandCycle(10, 2)));

        ledger.MarkCompleted("exports", T);
        Assert.False(ledger.Roll(new IslandCycle(10, 2)));
        Assert.True(ledger.IsCompleted("exports"));

        Assert.True(ledger.Roll(new IslandCycle(10, 3)));
        Assert.False(ledger.IsCompleted("exports"));
    }

    [Fact]
    public void FailuresAccumulateWithLastReason()
    {
        var ledger = new CycleLedger();
        ledger.RecordFailure("granary", "agent not open", T);
        ledger.RecordFailure("granary", "timed out", T);

        Assert.Equal(2, ledger.Failures("granary"));
        Assert.Equal("timed out", ledger.Get("granary")!.LastError);
        Assert.Equal(0, ledger.Failures("other"));
    }
}
