using System.Collections.Generic;
using Calypso.Core.Market;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>What the Enterprising Exporter's window holds once it's open and loaded.</summary>
public sealed record ExportShop(IReadOnlyList<ExportItem> Items, (int Seafarers, int Islanders) Held, (int Seafarers, int Islanders) Caps);

/// <summary>
/// The Enterprising Exporter (AgentMJIDisposeShop, ClientStructs 78975d8). Reading uses the agent's own item list —
/// ShopItemRowId, PouchItemRowId, UICategory, CowriesPerItem, UseIslanderCowries, CountInInventory — and its currency
/// counts/caps. Bulk shipping is visland's protocol (WORKABLE until a manual export is probed): agent event 0 on the
/// agent's OpenerEventInterface with [UInt count, UInt keep, (UInt shopRow, UInt quantity)…].
/// </summary>
public sealed unsafe class ExportGame
{
    private const uint SeafarersCowrie = 37549;

    private readonly Dictionary<uint, int> _stackSize = new();

    public ExportGame(IDataManager data)
    {
        var items = data.GetExcelSheet<Item>();
        foreach (var row in data.GetExcelSheet<MJIItemPouch>())
        {
            if (row.Item.RowId != 0)
                _stackSize[row.Item.RowId] = (int)(items.GetRowOrDefault(row.Item.RowId)?.StackSize ?? 999);
        }
    }

    /// <summary>The open, fully loaded exporter window — or null.</summary>
    public ExportShop? Read()
    {
        var agent = AgentMJIDisposeShop.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive() || agent->Data == null)
            return null;

        var d = agent->Data;
        if (!d->DataInitialized || d->InitializationState != 3)
            return null;

        var list = new List<ExportItem>();
        foreach (ref var item in d->Items)
        {
            list.Add(new ExportItem(item.ShopItemRowId, item.PouchItemRowId, item.ItemId, item.Name.ToString(), item.UICategory,
                item.CowriesPerItem, item.UseIslanderCowries, (int)item.CountInInventory, _stackSize.GetValueOrDefault(item.ItemId, 999)));
        }

        // CurrencyItemIds says which slot is which; Seafarer's is 37549.
        var seaSlot = d->CurrencyItemIds[0] == SeafarersCowrie ? 0 : 1;
        var islSlot = 1 - seaSlot;
        return new ExportShop(list,
            (d->CurrencyCounts[seaSlot], d->CurrencyCounts[islSlot]),
            (d->CurrencyStackSizes[seaSlot], d->CurrencyStackSizes[islSlot]));
    }

    /// <summary>Ship one batch. False when the window isn't open.</summary>
    public bool Ship(ExportBatch batch)
    {
        var agent = AgentMJIDisposeShop.Instance();
        if (agent == null || agent->Data == null || agent->AgentInterface.OpenerEventInterface == null)
            return false;

        var count = 2 + 2 * batch.Items.Count;
        var values = stackalloc AtkValue[count];
        values[0].SetUInt((uint)batch.Items.Count);
        values[1].SetUInt((uint)batch.Keep);
        for (var i = 0; i < batch.Items.Count; i++)
        {
            values[2 + 2 * i].SetUInt(batch.Items[i].ShopRow);
            values[3 + 2 * i].SetUInt((uint)batch.Items[i].Quantity);
        }

        var ret = new AtkValue();
        agent->AgentInterface.OpenerEventInterface->ReceiveEvent(&ret, values, (uint)count, 0);
        return true;
    }
}
