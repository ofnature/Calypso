using Calypso.Core.Progression;

namespace Calypso.Tests.Core;

public class ConstructionTests
{
    [Fact]
    public void BaseNameIsWhatTheTiersShare()
    {
        // MJIText rows 2-4, 73, 87 and 5-7, 75, 89 as the sheets hold them.
        Assert.Equal("Cozy Cabin", Construction.BaseName(new[] { "Cozy Cabin I", "Cozy Cabin II", "Cozy Cabin III", "Cozy Cabin IV", "Cozy Cabin V" }));
        Assert.Equal("Workshop", Construction.BaseName(new[] { "Workshop I", "Workshop II", "Workshop III" }));
        Assert.Equal("Granary", Construction.BaseName(new[] { "Granary I" }.Concat(new[] { "Granary II" }).ToList()));
        Assert.Equal("Granary I", Construction.BaseName(new[] { "Granary I" }));
    }

    [Fact]
    public void ObjectivesMentionTheBaseName()
    {
        Assert.True(ProgressRules.Mentions("Construct the cozy cabin after collecting the requisite materials.", "Cozy Cabin"));
        Assert.True(ProgressRules.Mentions("Construct a workshop after collecting the requisite materials.", "Workshop"));
        Assert.False(ProgressRules.Mentions("Construct a workshop after collecting the requisite materials.", "Workshop I"));
    }

    // MJIBuildingPlace as the sheet holds it: rows 1-5, 7 facility logboards; row 6 the cabin's.
    private static readonly BuildingPlace[] Places =
    {
        new(1, 2013005, false, 1), new(2, 2013006, false, 1), new(3, 2013007, false, 3), new(4, 2013008, false, 3),
        new(5, 2013009, false, 4), new(6, 2013004, true, 0), new(7, 2013437, false, 6),
    };

    [Fact]
    public void FreePlacesRespectKindLevelAndOccupancy()
    {
        Assert.Equal(new[] { 6 }, Construction.FreePlaces(Places, 0, 0, new int[0]).Select(p => p.Row));
        Assert.Empty(Construction.FreePlaces(Places, 1, 0, new int[0]));
        Assert.Equal(new[] { 1, 2 }, Construction.FreePlaces(Places, 1, 1, new int[0]).Select(p => p.Row));
        // Korha: Workshop I on Facility II (place 2) → a granary would go on Facility I.
        Assert.Equal(new[] { 1 }, Construction.FreePlaces(Places, 2, 1, new[] { 2 }).Select(p => p.Row));
        Assert.Equal(new[] { 3, 4 }, Construction.FreePlaces(Places, 1, 3, new[] { 1, 2 }).Select(p => p.Row));
    }
}
