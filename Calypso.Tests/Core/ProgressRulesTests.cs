using System.Numerics;
using Calypso.Core.Progression;

namespace Calypso.Tests.Core;

public class ProgressRulesTests
{
    // Real data: names from the island layout (planlive.lgb + ENpcResident), objectives from MJIProgress (English client).
    private static readonly IslandNpc Furball = new(1043472, "felicitous furball", new Vector3(-321.2f, 33.5f, 255.8f));
    private static readonly IslandNpc PathfinderI = new(1043079, "pathological pathfinder mk. I", Vector3.Zero);
    private static readonly IslandNpc PathfinderII = new(1043135, "pathological pathfinder mk. II", Vector3.Zero);
    private static readonly IslandNpc Producer = new(1043465, "produce producer", Vector3.Zero);
    private static readonly IReadOnlyList<IslandNpc> Npcs = new[] { Furball, PathfinderI, PathfinderII, Producer };
    private static readonly string[] OtherModes = { "Sow", "Water", "Cull", "Feed", "Pet", "Beckon", "Capture" };
    // MJIRecipe row 0: 3 palm leaf (pouch 0), 2 branch (pouch 2), 2 stone (pouch 3).
    private static readonly Craftable Hatchet = new("islekeep's stone hatchet", false, 0, new[] { (0, 3), (2, 2), (3, 2) }, true);
    // [REPLACED: buildings are Buildable now (base name + tier name + tier materials)]
    // private static readonly NamedThing Cabin = new("Cozy Cabin", false);
    private static readonly Buildable Cabin = new(0, "Cozy Cabin", "Cozy Cabin I", false, new[] { (10, 10), (0, 10) });

    private static ProgressAction Decide(ProgressStep step, Func<int, int>? pouch = null,
        Craftable[]? craftables = null, Buildable[]? buildings = null) =>
        ProgressRules.Decide(step, pouch ?? (_ => 0), Npcs, OtherModes, craftables ?? new[] { Hatchet }, buildings ?? new[] { Cabin });

    [Fact]
    public void Step1TalksToTheFurball()
    {
        var a = Decide(new ProgressStep(1, "Speak with the felicitous furball.", Array.Empty<(int, int)>()));
        Assert.Equal(ProgressActionKind.Talk, a.Kind);
        Assert.Equal(Furball, Assert.Single(a.Npcs));
    }

    [Fact]
    public void Step3GathersTheListedMaterialsThenReportsBack()
    {
        var step = new ProgressStep(3, "With the Gather mode enabled, collect the necessary materials and report back to the felicitous furball.",
            new[] { (2, 2), (3, 2), (0, 3) });

        var gather = Decide(step, row => row == 0 ? 1 : 0);
        Assert.Equal(ProgressActionKind.Gather, gather.Kind);
        Assert.Equal(new[] { (2, 2), (3, 2), (0, 2) }, gather.Missing);

        var talk = Decide(step, _ => 10);
        Assert.Equal(ProgressActionKind.Talk, talk.Kind); // "Gather" is not one of the blocking modes
    }

    [Fact]
    public void CraftStepGathersTheRecipeThenCraftsThenReportsBack()
    {
        var step = new ProgressStep(4, "Craft an islekeep's stone hatchet and report back to the felicitous furball.", Array.Empty<(int, int)>());

        var gather = Decide(step, row => row == 0 ? 3 : 0);
        Assert.Equal(ProgressActionKind.Gather, gather.Kind);
        Assert.Equal(new[] { (2, 2), (3, 2) }, gather.Missing);
        Assert.Equal(Hatchet, gather.Craft);

        var craft = Decide(step, _ => 20);
        Assert.Equal(ProgressActionKind.Craft, craft.Kind);
        Assert.Equal(0, craft.Craft!.RecipeRow);

        Assert.Equal(ProgressActionKind.Talk, Decide(step, craftables: new[] { Hatchet with { Done = true } }).Kind);
    }

    [Fact]
    public void WildcardRecipesStayBlocked()
    {
        var feed = new Craftable("islekeep's stone hatchet", false, 9, new[] { (0, 2) }, Automatable: false);
        var step = new ProgressStep(4, "Craft an islekeep's stone hatchet and report back to the felicitous furball.", Array.Empty<(int, int)>());
        Assert.Equal(ProgressActionKind.Blocked, Decide(step, _ => 99, craftables: new[] { feed }).Kind);
    }

    // [REPLACED: construction is automated since probes 10/11 (2026-10-19)]
    // [Fact]
    // public void ConstructStepGathersFirstThenBlocks()
    // {
    //     var step = new ProgressStep(5, "Construct the cozy cabin after collecting the requisite materials.", new[] { (10, 10) });
    //
    //     Assert.Equal(ProgressActionKind.Gather, Decide(step).Kind);
    //     var blocked = Decide(step, _ => 99);
    //     Assert.Equal(ProgressActionKind.Blocked, blocked.Kind);
    //     Assert.Contains("construct", blocked.Reason);
    // }

    [Fact]
    public void ConstructStepGathersTheBuildingsMaterialsThenConstructs()
    {
        // The step lists only part of what the building costs: the building's own materials are gathered too.
        var step = new ProgressStep(5, "Construct the cozy cabin after collecting the requisite materials.", new[] { (10, 10) });

        Assert.Equal(ProgressActionKind.Gather, Decide(step).Kind);
        var short_ = Decide(step, row => row == 10 ? 10 : 0);
        Assert.Equal(ProgressActionKind.Gather, short_.Kind);
        Assert.Equal(new[] { (0, 10) }, short_.Missing);
        Assert.Same(Cabin, short_.Build);

        var go = Decide(step, _ => 99);
        Assert.Equal(ProgressActionKind.Construct, go.Kind);
        Assert.Equal("Cozy Cabin I", go.Build!.TierName);
    }

    [Fact]
    public void ABuiltBuildingIsNotConstructedAgain()
    {
        var step = new ProgressStep(5, "Construct the cozy cabin after collecting the requisite materials.", Array.Empty<(int, int)>());
        Assert.NotEqual(ProgressActionKind.Construct, Decide(step, _ => 99, buildings: new[] { Cabin with { Done = true } }).Kind);
    }

    [Fact]
    public void SowStepGathersASeedFirstThenSows()
    {
        var modes = new HudModeNames("Sow", "Water");
        var step = new ProgressStep(10, "With the Sow mode enabled, plant seeds in the cropland.", Array.Empty<(int, int)>());

        var gather = ProgressRules.Decide(step, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, (35, false));
        Assert.Equal(ProgressActionKind.Gather, gather.Kind);
        Assert.Equal(new[] { (35, 1) }, gather.Missing);

        var sow = ProgressRules.Decide(step, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, (35, true));
        Assert.Equal(ProgressActionKind.Sow, sow.Kind);

        var water = ProgressRules.Decide(new ProgressStep(11, "With the Water mode enabled, water the seeds you sowed.", Array.Empty<(int, int)>()),
            _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, (35, true));
        Assert.Equal(ProgressActionKind.Water, water.Kind);
    }

    [Fact]
    public void ModeStepsAreBlocked()
    {
        // Without the capture-mode names wired in (older callers), Capture is just another blocked mode.
        var a = Decide(new ProgressStep(16, "With the Capture mode enabled, capture a wild animal.", Array.Empty<(int, int)>()));
        Assert.Equal(ProgressActionKind.Blocked, a.Kind);
        Assert.Contains("Capture", a.Reason);
    }

    [Fact]
    public void FeedNeedsFeed()
    {
        var modes = new HudModeNames("Sow", "Water", "Capture", "Feed");
        var step = new ProgressStep(18, "With the Feed mode enabled, feed an animal in the pasture.", Array.Empty<(int, int)>());

        Assert.Equal(ProgressActionKind.Blocked,
            ProgressRules.Decide(step, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, null, false, haveFeed: false).Kind);
        Assert.Equal(ProgressActionKind.Feed,
            ProgressRules.Decide(step, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, null, false, haveFeed: true).Kind);
    }

    [Fact]
    public void CaptureNeedsARestraint()
    {
        var modes = new HudModeNames("Sow", "Water", "Capture");
        var step = new ProgressStep(16, "With the Capture mode enabled, capture a wild animal.", Array.Empty<(int, int)>());

        var none = ProgressRules.Decide(step, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, null, haveRestraint: false);
        Assert.Equal(ProgressActionKind.Blocked, none.Kind);

        // MJIRecipe row 11: 1 Island Branch (pouch 2), 2 Island Vine (pouch 11).
        var net = new Craftable("Makeshift Net", false, 11, new[] { (2, 1), (11, 2) }, true);
        var gather = ProgressRules.Decide(step, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, null, false, false, net);
        Assert.Equal(ProgressActionKind.Gather, gather.Kind);
        var craft = ProgressRules.Decide(step, _ => 5, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, null, false, false, net);
        Assert.Equal(ProgressActionKind.Craft, craft.Kind);
        Assert.Equal(11, craft.Craft!.RecipeRow);

        var go = ProgressRules.Decide(step, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>(), modes, null, haveRestraint: true);
        Assert.Equal(ProgressActionKind.Capture, go.Kind);
    }

    [Fact]
    public void LongestNpcNameWins()
    {
        var a = Decide(new ProgressStep(25, "Report to the pathological pathfinder mk. II.", Array.Empty<(int, int)>()));
        Assert.Equal(PathfinderII, Assert.Single(a.Npcs));

        var b = Decide(new ProgressStep(22, "Speak with the pathological pathfinder mk. I.", Array.Empty<(int, int)>()));
        Assert.Equal(PathfinderI, Assert.Single(b.Npcs));
    }

    [Fact]
    public void UnknownStepsAndTheEndAreReported()
    {
        Assert.Equal(ProgressActionKind.Blocked, Decide(new ProgressStep(31, "Investigate the mossy mountains.", Array.Empty<(int, int)>())).Kind);
        Assert.Equal(ProgressActionKind.Finished, ProgressRules.Decide(null, _ => 0, Npcs, OtherModes, Array.Empty<Craftable>(), Array.Empty<Buildable>()).Kind);
    }

    [Theory]
    [InlineData("Speak with the pathological pathfinder mk. II.", "pathological pathfinder mk. I", false)]
    [InlineData("Speak with the pathological pathfinder mk. I.", "pathological pathfinder mk. I", true)]
    [InlineData("With the Sow mode enabled", "Sow", true)]
    [InlineData("Sowing is fun", "Sow", false)]
    public void MentionsMatchesWholePhrases(string text, string name, bool expected)
    {
        Assert.Equal(expected, ProgressRules.Mentions(text, name));
    }
}
