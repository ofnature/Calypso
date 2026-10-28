using System.Collections.Generic;

namespace Calypso.Core.Granary;

/// <summary>
/// One expedition destination: an MJIStockyardManagementArea row (its rare) and that row's MJIStockyardManagementTable
/// subrows (the normal resources it brings). Pouch rows are MJIItemPouch rows, the ids the Isleventory uses.
/// <see cref="Id"/> is the area index (0-5) the planner hands back as the expedition to dispatch.
/// </summary>
public sealed record ExpeditionArea(int Id, string Name, int RarePouchRow, IReadOnlyList<int> NormalPouchRows)
{
    /// <summary>Crumbling Caverns (Island Cave Shrimp), added in 6.4 behind its own unlock.</summary>
    public const int Caverns = 5;

    /// <summary>
    /// UNVERIFIED mapping: the names are MJIText rows 39-43 and 71 (xivapi/ffxiv-datamining en CSV, game data, facts
    /// only), matched to areas by order; every area's rare agrees with the Console Games Wiki "Island Granary" list
    /// (search snippets — the wiki is unreachable from the cloud sandbox). The game adapter should prefer the names the
    /// granary agent shows.
    /// </summary>
    public static string KnownName(int id) => id switch
    {
        0 => "Meandering Meadows",
        1 => "Fatal Falls",
        2 => "Wild Woods",
        3 => "Bending Beaches",
        4 => "Mossy Mountains",
        5 => "Crumbling Caverns",
        _ => $"Area {id}",
    };
}
