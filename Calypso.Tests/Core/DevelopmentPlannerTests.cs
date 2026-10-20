using Calypso.Core;
using Calypso.Core.Development;

namespace Calypso.Tests.Core;

public class DevelopmentPlannerTests
{
    private const int Log = 9, PalmLeaf = 0, Limestone = 14, Sand = 11, Vine = 8, Clay = 19;

    // MJIBuilding as the sheet holds it (subset): name, rank (Unknown1), materials.
    private static readonly BuildingTier[] Tiers =
    {
        new(0, 0, "Cozy Cabin I", 1, new[] { (10, 10), (PalmLeaf, 10), (Sand, 5), (3, 5), (Vine, 5) }),
        new(0, 1, "Cozy Cabin II", 4, new[] { (10, 15), (Sand, 10), (Limestone, 10), (PalmLeaf, 5), (13, 5) }),
        new(1, 0, "Workshop I", 1, new[] { (Log, 10), (PalmLeaf, 10), (Limestone, 10), (Sand, 5), (Vine, 5) }),
        new(1, 1, "Workshop II", 6, new[] { (Log, 15), (Limestone, 15) }),
        new(2, 0, "Granary I", 5, new[] { (10, 10), (Log, 10), (Clay, 10) }),
    };

    // MJILandmark: Windmill and Tree Fort need development level 2 (rank 4 on the wiki).
    private static readonly LandmarkInfo[] Landmarks =
    {
        new(1, "Quixotic Windmill", 2, 0, 0, new[] { (Log, 15), (Limestone, 15) }),
        new(2, "Towering Tree Fort", 2, 0, 0, new[] { (10, 15), (Log, 15) }),
        new(5, "Water Otter Fountain", 5, 0, 326, new[] { (Limestone, 15) }),
    };

    // MJIVillageDevelopment: level, NPC, rank (Unknown5), builder's tools key item (Unknown6).
    private static readonly PathfinderStep[] Pathfinders =
    {
        new(1, 1043079, "pathological pathfinder mk. I", 1, 0),
        new(2, 1043135, "pathological pathfinder mk. II", 4, 9),
        new(3, 1043136, "pathological pathfinder mk. III", 5, 10),
    };

    private static ToolRecipe Tool(int recipe, string name, int key, ToolPurpose purpose, bool unlocked = true, bool held = false) =>
        new(recipe, name, key, unlocked, held, true, purpose, new[] { (Log, 2) });

    private static readonly ToolRecipe[] Tools =
    {
        Tool(1, "Islekeep's Stone Hammer", 2, ToolPurpose.Gathering, held: true),
        Tool(2, "Islekeep's Shovel", 3, ToolPurpose.Gathering, unlocked: false),
        Tool(6, "Mammet-sized Cropworker's Tools", 7, ToolPurpose.HandIn),
        Tool(14, "Basic Mammet-sized Builder's Tools", 9, ToolPurpose.Builders),
        Tool(15, "Better Mammet-sized Builder's Tools", 10, ToolPurpose.Builders),
    };

    /// <summary>Saar at 22:47 2026-10-19: rank 4, Cabin I, Workshop I on place 1, development level 1, Facility II free.</summary>
    private static DevelopmentInput Saar(int rank = 4, int dev = 1, int freeFacility = 1, int freeLandmark = 0, Func<int, int>? pouch = null,
        BuildingState? workshop = null) => new(
        rank, dev, 1,
        new[] { workshop ?? new BuildingState(1, false, 0, 1), default, default, default },
        new BuildingState[2],
        new byte[5],
        freeFacility,
        freeLandmark,
        pouch ?? (_ => 99));

    private static IReadOnlyList<DevelopmentTask> Plan(DevelopmentInput s) => DevelopmentPlanner.Plan(s, Tiers, Landmarks, Pathfinders, Tools);

    [Fact]
    public void Rank4PlanAsTheWikiHasIt()
    {
        var plan = Plan(Saar());

        // Only the builder's tools Mk. II takes; no cropworker's tools (handed in later), no locked shovel.
        Assert.Equal(new[] { "Basic Mammet-sized Builder's Tools" }, plan.Where(t => t.Kind == DevelopmentKind.CraftTool).Select(t => t.Name));
        Assert.Contains(plan, t => t is { Kind: DevelopmentKind.Upgrade, Name: "Cozy Cabin II", State: DevelopmentState.NeedsRecording });
        Assert.Contains(plan, t => t is { Kind: DevelopmentKind.Construct, Name: "Workshop I", State: DevelopmentState.Ready, Row: 1 });
        Assert.Contains(plan, t => t is { Kind: DevelopmentKind.Pathfinder, Name: "pathological pathfinder mk. II", State: DevelopmentState.NeedsRecording });
        Assert.DoesNotContain(plan, t => t.Kind == DevelopmentKind.Landmark); // no landmark place before Mk. II
        Assert.Contains(plan, t => t is { Name: "Workshop II", State: DevelopmentState.Waiting, Note: "needs rank 6" });
    }

    [Fact]
    public void LandmarksAreChoicesOnceAPlaceOpens()
    {
        var plan = Plan(Saar(dev: 2, freeLandmark: 1));
        var landmarks = plan.Where(t => t.Kind == DevelopmentKind.Landmark).Select(t => t.Name).ToList();
        Assert.Equal(new[] { "Quixotic Windmill", "Towering Tree Fort" }, landmarks); // the Otter Fountain needs its quest
    }

    [Fact]
    public void MaterialsAreCountedCumulatively()
    {
        // 15 limestone: Cabin II takes 10 of it, so Workshop I (10) is 5 short.
        var plan = Plan(Saar(pouch: row => row == Limestone ? 15 : 99));
        var workshop = plan.Single(t => t.Name == "Workshop I");
        Assert.Equal(DevelopmentState.NeedsMaterials, workshop.State);
        Assert.Equal(new[] { (Limestone, 5) }, workshop.Missing);
        Assert.Empty(plan.Single(t => t.Name == "Cozy Cabin II").Missing);
    }

    [Fact]
    public void OnceGranariesOpenTheFacilityChoiceIsByHand()
    {
        var plan = Plan(Saar(rank: 5));
        var construct = plan.Single(t => t.Kind == DevelopmentKind.Construct);
        Assert.Equal("Granary I", construct.Name); // a granary first, as the wiki does it
        Assert.Equal(DevelopmentState.NeedsRecording, construct.State);
    }

    [Fact]
    public void AnUpgradeUnderConstructionWaits()
    {
        var plan = Plan(Saar(rank: 6, workshop: new BuildingState(1, true, 2, 1)));
        var upgrade = plan.Single(t => t.Name == "Workshop II");
        Assert.Equal(DevelopmentState.Waiting, upgrade.State);
        Assert.Contains("2 h", upgrade.Note);
    }

    [Fact]
    public void BuildersToolsWaitForTheirPathfinder()
    {
        // Mk. II done (level 2) but rank 4 < Mk. III's 5: no builder's tools yet; the pathfinder waits on the rank.
        var plan = Plan(Saar(dev: 2));
        Assert.DoesNotContain(plan, t => t.Kind == DevelopmentKind.CraftTool);
        Assert.Contains(plan, t => t is { Kind: DevelopmentKind.Pathfinder, State: DevelopmentState.Waiting });
    }
}
