using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Calypso.Core.Pasture;

/// <summary>
/// Where wild island animals have been seen, by species (MJIAnimals.BNpcBase). Nothing in the game files places them —
/// no BattleNpc in the island's LGBs, no position in MJIAnimals (checked 2026-10-19) — the server spawns them, so the
/// spots are learned whenever one is loaded nearby. Global, not per character: every island has the same layout.
/// </summary>
public sealed class AnimalBook
{
    /// <summary>Sightings closer than this to a known spot are the same spot.</summary>
    public const float SameSpot = 25f;

    public const int MaxSpotsPerSpecies = 24;

    public Dictionary<uint, List<Vector3>> Seen { get; set; } = new();

    /// <summary>Remember a sighting; true when it's a new spot.</summary>
    public bool Note(uint bnpcBase, Vector3 position)
    {
        if (!Seen.TryGetValue(bnpcBase, out var spots))
            Seen[bnpcBase] = spots = new List<Vector3>();
        if (spots.Count >= MaxSpotsPerSpecies || spots.Any(p => Vector3.Distance(p, position) < SameSpot))
            return false;
        spots.Add(position);
        return true;
    }

    public IEnumerable<Vector3> SpotsOf(IEnumerable<uint> species) =>
        species.Distinct().SelectMany(s => Seen.TryGetValue(s, out var spots) ? spots : Enumerable.Empty<Vector3>());
}
