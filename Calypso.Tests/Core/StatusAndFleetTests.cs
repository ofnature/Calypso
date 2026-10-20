using Calypso.Core;
using Calypso.Core.Status;
using Calypso.Core.Tasks;
using Calypso.Game;

namespace Calypso.Tests.Core;

public class StatusAndFleetTests
{
    private static readonly DateTime T = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StatusJsonUsesTheAgreedCamelCaseFieldNames()
    {
        var runner = new TaskRunner(Array.Empty<IIslandTask>());
        var status = CalypsoStatus.Build(runner, new IslandSnapshot { Available = true, Rank = 7, InSanctuary = true },
            new CycleLedger(), _ => true, "Kaia Calypso@Twintania", "0.0.1", new IslandCycle(5, 2), T);

        var json = status.ToJson();

        // Contract fields other plugins read. Renaming any of these is a breaking IPC change.
        foreach (var field in new[] { "\"schema\":1", "\"character\":\"Kaia Calypso@Twintania\"", "\"busy\":false", "\"state\":\"idle\"", "\"rank\":7", "\"inSanctuary\":true", "\"cycle\":\"5.2\"", "\"tasks\":[]" })
            Assert.Contains(field, json);

        var back = CalypsoStatus.TryParse(json);
        Assert.NotNull(back);
        Assert.Equal(7, back!.Rank);
    }

    [Fact]
    public void StatusParserToleratesUnknownFieldsAndRejectsGarbage()
    {
        Assert.NotNull(CalypsoStatus.TryParse("{\"schema\":2,\"character\":\"A@B\",\"somethingNew\":[1,2]}"));
        Assert.Null(CalypsoStatus.TryParse("not json"));
    }

    [Fact]
    public void ControlMessageTargetsAllOrOneCaseInsensitively()
    {
        var all = ControlMessage.TryParse(new ControlMessage { Act = ControlMessage.ActStart }.ToJson())!;
        Assert.True(all.IsFor("Anyone@Anywhere"));

        var one = ControlMessage.TryParse(new ControlMessage { Act = ControlMessage.ActStop, Target = "kaia calypso@twintania" }.ToJson())!;
        Assert.True(one.IsFor("Kaia Calypso@Twintania"));
        Assert.False(one.IsFor("Other@Twintania"));
    }

    [Fact]
    public void ControlMessageWithUnknownActIsDropped()
    {
        Assert.Null(ControlMessage.TryParse("{\"act\":\"selfdestruct\",\"target\":\"*\"}"));
    }

    [Fact]
    public void FleetBoardDropsStaleBoxes()
    {
        var board = new FleetBoard();
        board.Record(new CalypsoStatus { Character = "A@W" }, T);
        board.Record(new CalypsoStatus { Character = "B@W" }, T.AddSeconds(60));

        var now = T.Add(FleetBoard.StaleAfter).AddSeconds(1);
        Assert.Equal(new[] { "B@W" }, board.Current(now).Select(b => b.Status.Character));
    }

    [Fact]
    public void FunctionGateTreatsUnknownAsLocked()
    {
        var s = new IslandSnapshot { FunctionUnlocked = new[] { true, false } };
        Assert.True(s.IsFunctionUnlocked(0));
        Assert.False(s.IsFunctionUnlocked(1));
        Assert.False(s.IsFunctionUnlocked(2));
        Assert.False(s.IsFunctionUnlocked(-1));
        Assert.False(IslandSnapshot.Empty.IsFunctionUnlocked(0));
    }

    [Fact]
    public void BuildingDecodeAddsOneAndTreatsPlaceZeroAsNotBuilt()
    {
        Assert.False(IslandStateReader.DecodeBuilding(0, 2, false, 0).IsBuilt);

        var w = IslandStateReader.DecodeBuilding(3, 2, true, 5);
        Assert.True(w.IsBuilt);
        Assert.Equal(3, w.Level);
        Assert.True(w.UnderConstruction);
        Assert.Equal(5, w.HoursToCompletion);
    }

    [Fact]
    public void NewCharacterGetsACopyOfDefaultsWithAnEmptyLedger()
    {
        var config = new CalypsoConfig();
        config.Defaults.SetTaskEnabled("exports", false);
        config.Defaults.Ledger.MarkCompleted("x", T);

        var p = config.ProfileFor(42, "A@W", out var created);
        Assert.True(created);
        Assert.False(p.IsTaskEnabled("exports"));
        Assert.Empty(p.Ledger.Entries);

        p.SetTaskEnabled("exports", true);
        Assert.False(config.Defaults.IsTaskEnabled("exports"));

        Assert.Same(p, config.ProfileFor(42, "A@W", out var again));
        Assert.False(again);
    }
}
