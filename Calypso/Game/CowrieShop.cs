using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Market;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>
/// What the island's cowrie shops sell that can be resold on the market board: every SpecialShop entry priced in
/// Seafarer's (37549) or Islander's (37550) Cowries whose item is tradable and has a market-board category. Found by
/// scanning the SpecialShop sheet by cost currency (2026-10-17: shops 1770601…1770709 — Materia IX/X, Glamour Prism and
/// Dispeller, island furnishings, crackers), because the NPCs' shop links live in their event scripts.
/// </summary>
public sealed class CowrieShop
{
    public const uint SeafarersCowrie = 37549;
    public const uint IslandersCowrie = 37550;

    public CowrieShop(IDataManager data, IPluginLog log)
    {
        var offers = new List<ShopOffer>();
        try
        {
            Scan(data, offers);
        }
        catch (Exception ex)
        {
            // A sheet change must cost the shop list, never the plugin (2026-10-18: a bad lookup here failed the whole load).
            log.Error(ex, "Could not read the cowrie shops; the shop list will be empty");
        }

        Offers = offers;
    }

    private static void Scan(IDataManager data, List<ShopOffer> offers)
    {
        var items = data.GetExcelSheet<Item>();
        foreach (var shop in data.GetExcelSheet<SpecialShop>())
        {
            var index = -1;
            foreach (var entry in shop.Item)
            {
                index++;
                // [REPLACED: FirstOrDefault returns a default Lumina struct when nothing matches, and reading its
                // ItemCost throws — load error 2026-10-18]
                // var cost = entry.ItemCosts.FirstOrDefault(c => c.ItemCost.RowId is SeafarersCowrie or IslandersCowrie);
                // if (cost.ItemCost.RowId == 0)
                //     continue;
                var cowrieCosts = entry.ItemCosts.Where(c => c.ItemCost.RowId is SeafarersCowrie or IslandersCowrie).ToList();
                if (cowrieCosts.Count == 0)
                    continue;
                var cost = cowrieCosts[0];

                foreach (var got in entry.ReceiveItems.Where(r => r.Item.RowId != 0))
                {
                    var item = items.GetRowOrDefault(got.Item.RowId);
                    if (item is not { IsUntradable: false } i || i.ItemSearchCategory.RowId == 0)
                        continue;
                    offers.Add(new ShopOffer(shop.RowId, index, got.Item.RowId, i.Name.ExtractText(), (int)got.ReceiveCount,
                        cost.ItemCost.RowId, (int)cost.CurrencyCost));
                }
            }
        }
    }

    public IReadOnlyList<ShopOffer> Offers { get; }
}
