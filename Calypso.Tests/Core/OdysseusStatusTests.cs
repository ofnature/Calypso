using Calypso.Game;

namespace Calypso.Tests.Core;

public class OdysseusStatusTests
{
    [Fact]
    public void ParsesTheAgreedCamelCaseShape()
    {
        // As the Odysseus session described it: {schema:1, state, questId, sequence, step, status, reason}.
        const string json = """{"schema":1,"state":"Faulted","questId":70179,"sequence":2,"step":4,"status":"Walking to Tataru","reason":"stuck at the ferry"}""";
        var s = OdysseusQuestStatus.TryParse(json)!;

        Assert.Equal("Faulted", s.State);
        Assert.Equal(70179u, s.QuestId);
        Assert.Equal(2, s.Sequence);
        Assert.Equal("stuck at the ferry", s.Reason);
    }

    [Fact]
    public void ToleratesNewFieldsAndNullReason()
    {
        var s = OdysseusQuestStatus.TryParse("""{"schema":2,"state":"Running","questId":70179,"sequence":0,"step":0,"status":"","reason":null,"eta":12}""");
        Assert.NotNull(s);
        Assert.Null(s!.Reason);
        Assert.Null(OdysseusQuestStatus.TryParse("not json"));
    }
}
