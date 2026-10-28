using System.Collections.Generic;

namespace Calypso.Core.Pasture;

/// <summary>MJIAnimals.Unknown0. How hard a species is to find (and, for rare ones, to catch).</summary>
public enum AnimalRarity
{
    Common = 1,

    /// <summary>The second of a pair of the same species group (MJIAnimals.Unknown1) — a colour variant.</summary>
    Variant = 2,

    /// <summary>Rare: appears only under conditions no sheet holds (a community table does), so the planner never says when.</summary>
    Rare = 3,
}

/// <summary>
/// One MJIAnimals row. <see cref="Leavings"/> are MJIAnimals.Reward[0] and Reward[1] as MJIItemPouch rows (category 5):
/// the two leavings this species gives. <see cref="BNpcBase"/> is the key <see cref="AnimalBook"/> remembers spots by.
/// </summary>
public sealed record AnimalDef(int Row, uint BNpcBase, IReadOnlyList<int> Leavings, int Size, AnimalRarity Rarity, string? Name = null);
