using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Progression;

/// <summary>
/// A building the tutorial can name (MJIBuilding row: 0 cabin, 1 workshop, 2 granary). <see cref="Name"/> is what the
/// objective says ("cozy cabin", "a workshop"); <see cref="TierName"/> is the first tier the construction window lists
/// ("Cozy Cabin I"); <see cref="Materials"/> are that tier's MJIBuilding materials.
/// </summary>
public sealed record Buildable(int Building, string Name, string TierName, bool Done, IReadOnlyList<(int PouchRow, int Amount)> Materials);

/// <summary>
/// A building place (MJIBuildingPlace row) and its logboard EObj. <see cref="ForCabin"/>: the place's SGB is one of the
/// cabin's (MJIBuilding row 0 Sgb); the others hold workshops and granaries. <see cref="DevelopmentLevel"/> = the
/// village development level the place needs (sheet byte +0x10, ClientStructs IslandState.VillageDevelopmentLevel).
/// </summary>
public sealed record BuildingPlace(int Row, uint Logboard, bool ForCabin, int DevelopmentLevel);

public static class Construction
{
    /// <summary>The name a building's tiers share: "Cozy Cabin I" … "Cozy Cabin V" → "Cozy Cabin".</summary>
    public static string BaseName(IReadOnlyList<string> tierNames)
    {
        var names = tierNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count == 0)
            return string.Empty;
        if (names.Count == 1)
            return names[0].Trim();

        var prefix = names[0];
        foreach (var n in names.Skip(1))
        {
            var i = 0;
            while (i < prefix.Length && i < n.Length && char.ToLowerInvariant(prefix[i]) == char.ToLowerInvariant(n[i]))
                i++;
            prefix = prefix[..i];
        }

        // Cut back to a whole word: "Workshop I" and "Workshop II" share "Workshop I", which isn't the base name.
        var cut = prefix.LastIndexOf(' ');
        return (cut < 0 ? prefix : prefix[..cut]).Trim();
    }

    /// <summary>Places a building can go: the right kind, open at this development level, nothing on it. Lowest row first.</summary>
    public static IEnumerable<BuildingPlace> FreePlaces(IEnumerable<BuildingPlace> places, int building, int developmentLevel, IReadOnlyCollection<int> occupied) =>
        places.Where(p => p.ForCabin == (building == 0) && p.DevelopmentLevel <= developmentLevel && !occupied.Contains(p.Row))
            .OrderBy(p => p.Row);
}
