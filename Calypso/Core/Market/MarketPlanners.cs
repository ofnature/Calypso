using System;
using System.Collections.Generic;
using System.Linq;

namespace Calypso.Core.Market;

/// <summary>An Isleventory item the Enterprising Exporter takes (MJIDisposalShopItem).</summary>
public sealed record ExportItem(uint ShopRow, int PouchRow, uint ItemId, string Name, int Category, int CowriesEach, bool Islanders, int Held, int Cap);

/// <summary>One bulk-export call: the exporter takes items of one category with one "keep" amount (visland's protocol).</summary>
public sealed record ExportBatch(int Category, int Keep, IReadOnlyList<(uint ShopRow, int Quantity)> Items);

public sealed record ExportPlan(IReadOnlyList<ExportBatch> Batches, int SeafarersGained, int IslandersGained, IReadOnlyList<string> Skipped);

/// <summary>
/// What to sell (user rule, 2026-10-17): an item at its Isleventory cap (999) is sold down to <c>keep</c> (100) — or to
/// what a reservation needs, if that is more. Sales that would overflow a cowrie cap are skipped and reported.
/// </summary>
public static class ExportPlanner
{
    public static ExportPlan Plan(IEnumerable<ExportItem> items, int keep, Func<int, int> reserved,
        (int Seafarers, int Islanders) held, (int Seafarers, int Islanders) caps)
    {
        var sea = held.Seafarers;
        var isl = held.Islanders;
        var skipped = new List<string>();
        var picks = new List<(ExportItem Item, int Keep, int Quantity)>();

        foreach (var item in items.Where(i => i.Cap > 0 && i.Held >= i.Cap).OrderBy(i => i.ShopRow))
        {
            var keepThis = Math.Max(keep, reserved(item.PouchRow));
            var quantity = item.Held - keepThis;
            if (quantity <= 0)
                continue;

            var gain = quantity * item.CowriesEach;
            if (item.Islanders ? isl + gain > caps.Islanders : sea + gain > caps.Seafarers)
            {
                skipped.Add($"{item.Name}: would overflow {(item.Islanders ? "Islander's" : "Seafarer's")} Cowries");
                continue;
            }

            if (item.Islanders)
                isl += gain;
            else
                sea += gain;
            picks.Add((item, keepThis, quantity));
        }

        var batches = picks
            .GroupBy(p => (p.Item.Category, p.Keep))
            .OrderBy(g => g.Key.Category).ThenBy(g => g.Key.Keep)
            .SelectMany(g => g.Chunk(MaxItemsPerBatch).Select(chunk =>
                new ExportBatch(g.Key.Category, g.Key.Keep, chunk.Select(p => (p.Item.ShopRow, p.Quantity)).ToList())))
            .ToList();

        return new ExportPlan(batches, sea - held.Seafarers, isl - held.Islanders, skipped);
    }

    /// <summary>visland refuses more than 64 items in one call; kept identical.</summary>
    public const int MaxItemsPerBatch = 64;
}

/// <summary>A cowrie shop offer of a market-board-tradable item.</summary>
public sealed record ShopOffer(uint ShopId, int EntryIndex, uint ItemId, string Name, int Quantity, uint CurrencyItemId, int Cost);

/// <summary>An offer with its market price: gil per cowrie spent is the ranking.</summary>
public sealed record RankedOffer(ShopOffer Offer, long UnitPrice, double SaleVelocity, double GilPerCowrie, int Affordable);

/// <summary>
/// Best value per cowrie (user: "once it gets a list it can buy best value"): rank every tradable cowrie-shop item by
/// market unit price × quantity ÷ cost, using only currencies the character allows spending and keeping each
/// currency's reserve untouched. Items nobody is buying (no recent sales) rank last — a price with no buyers is not gil.
/// </summary>
public static class ShoppingPlanner
{
    public static IReadOnlyList<RankedOffer> Rank(IEnumerable<ShopOffer> offers, Func<uint, (long Price, double Velocity)?> price,
        Func<uint, int> spendable)
    {
        return offers
            .Select(o =>
            {
                var p = price(o.ItemId);
                var unit = p?.Price ?? 0;
                var gpc = o.Cost > 0 ? unit * (double)o.Quantity / o.Cost : 0;
                var affordable = o.Cost > 0 ? Math.Max(0, spendable(o.CurrencyItemId)) / o.Cost : 0;
                return new RankedOffer(o, unit, p?.Velocity ?? 0, gpc, affordable);
            })
            .OrderByDescending(r => r.SaleVelocity > 0)
            .ThenByDescending(r => r.GilPerCowrie)
            .ToList();
    }

    /// <summary>The single best thing to buy right now, or null when nothing is affordable and has a market.</summary>
    public static RankedOffer? Best(IReadOnlyList<RankedOffer> ranked) =>
        ranked.FirstOrDefault(r => r.Affordable > 0 && r.UnitPrice > 0 && r.SaleVelocity > 0);
}
