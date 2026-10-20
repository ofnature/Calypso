using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core;

/// <summary>
/// What the island's built layout looks like, as one string: everything construction changes on the island (user
/// 2026-10-19: "it changes states during the making of the island") — village development level (the pathfinder
/// leveling ground), cabin level, each workshop and granary (place, level, under construction), cropland and pasture
/// (level, under construction), landmarks. When it changes, a navmesh built earlier no longer matches the island
/// (Saar snagged on a pasture fence the mesh didn't have).
/// </summary>
public static class IslandLayoutFingerprint
{
    public static string Of(IslandSnapshot s) =>
        $"v{s.VillageDevelopmentLevel}|c{s.CabinLevel}|w{Buildings(s.Workshops)}|g{Buildings(s.Granaries)}"
        + $"|f{One(s.Farm)}|p{One(s.Pasture)}|l{string.Join(".", s.LandmarkIds)}";

    private static string Buildings(IEnumerable<BuildingState> buildings) =>
        string.Join(".", buildings.Where(b => b.IsBuilt).OrderBy(b => b.PlaceId).Select(b => $"{b.PlaceId}:{One(b)}"));

    private static string One(BuildingState b) => $"{b.Level}{(b.UnderConstruction ? "*" : string.Empty)}";
}
