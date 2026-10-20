using Calypso.Core;

namespace Calypso.Tests.Core;

public class IslandLayoutFingerprintTests
{
    private static IslandSnapshot Island(byte dev = 1, byte cabin = 1, BuildingState[]? workshops = null, BuildingState pasture = default) => new()
    {
        Available = true,
        VillageDevelopmentLevel = dev,
        CabinLevel = cabin,
        Workshops = workshops ?? new BuildingState[4],
        Granaries = new BuildingState[2],
        Pasture = pasture,
        LandmarkIds = new byte[5],
    };

    [Fact]
    public void EveryConstructionChangesIt()
    {
        var before = IslandLayoutFingerprint.Of(Island());
        Assert.Equal(before, IslandLayoutFingerprint.Of(Island()));

        // Korha 2026-10-19: Workshop I on place 2.
        Assert.NotEqual(before, IslandLayoutFingerprint.Of(Island(workshops: new[] { new BuildingState(1, false, 0, 2), default, default, default })));
        Assert.NotEqual(before, IslandLayoutFingerprint.Of(Island(dev: 2)));              // the pathfinder levels the ground
        Assert.NotEqual(before, IslandLayoutFingerprint.Of(Island(cabin: 2)));
        Assert.NotEqual(before, IslandLayoutFingerprint.Of(Island(pasture: new BuildingState(1, false, 0)))); // Saar's fence
        Assert.NotEqual(IslandLayoutFingerprint.Of(Island(pasture: new BuildingState(1, false, 0))),
            IslandLayoutFingerprint.Of(Island(pasture: new BuildingState(1, true, 5))));      // scaffolding while building
    }

    [Fact]
    public void WorkshopSlotOrderDoesNotMatter()
    {
        var a = Island(workshops: new[] { new BuildingState(1, false, 0, 2), new BuildingState(1, false, 0, 1), default, default });
        var b = Island(workshops: new[] { new BuildingState(1, false, 0, 1), new BuildingState(1, false, 0, 2), default, default });
        Assert.Equal(IslandLayoutFingerprint.Of(a), IslandLayoutFingerprint.Of(b));
    }
}
