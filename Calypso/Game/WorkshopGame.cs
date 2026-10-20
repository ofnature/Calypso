using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Workshop;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Calypso.Game;

/// <summary>What the agenda agent shows for its displayed cycle. Only exists while the agenda is open and loaded.</summary>
public sealed record AgendaView(int CycleDisplayed, int CycleInProgress, int Groove, uint RestCycles, IReadOnlyList<IReadOnlyList<(int Craft, int Slot)>> Workshops)
{
    public bool DisplayedCycleEmpty => Workshops.All(w => w.Count == 0);
}

/// <summary>
/// The workshop's game adapters: reading supply/demand, favors, Isleventory and the agenda; writing the agenda.
/// Struct layouts: ClientStructs 78975d8. Confidence per call is in claude-memory.md "Per-system read/drive map":
/// ScheduleCraft/SetDisplayedCycle/RequestDemandFull are SOLID (named functions); rest days (agent event 5) and the
/// "(slot + 17) % 24" starting-hour encoding come from visland only — WORKABLE until verified in game.
/// </summary>
public sealed unsafe class WorkshopGame
{
    // ---- Reading ----

    /// <summary>MJIManager.SupplyAndDemandShifts for every craft, or null while the server data is not in (DemandDirty).</summary>
    public byte[]? ReadSupplyDemand()
    {
        var mji = MJIManager.Instance();
        if (mji == null || mji->DemandDirty)
            return null;

        return mji->SupplyAndDemandShifts.ToArray();
    }

    /// <summary>Ask the server for fresh supply/demand (SOLID). Only meaningful on the island.</summary>
    public void RequestDemand()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->IsPlayerInSanctuary)
            mji->RequestDemandFull();
    }

    /// <summary>Isleventory counts by pouch row (InventoryManager, the way visland reads them).</summary>
    public Dictionary<int, int> PouchCounts(CraftCatalog catalog)
    {
        var inv = InventoryManager.Instance();
        var counts = new Dictionary<int, int>();
        if (inv == null)
            return counts;

        foreach (var p in catalog.Pouch.Values)
            counts[p.Row] = inv->GetInventoryItemCount(p.ItemId);
        return counts;
    }

    /// <summary>
    /// This week's favors (MJIFavorState indices 3-5, ordered 4h/6h/8h) still to fill. Required count comes from the
    /// favors agent when it has data, else from the sheet default for that position. <paramref name="alreadyMade"/>
    /// = items of each craft this week that are made but not yet counted as delivered (today's schedule).
    /// </summary>
    public List<FavorRequirement> Favors(int[] defaultRequired, Func<int, int> alreadyMade)
    {
        var result = new List<FavorRequirement>();
        var mji = MJIManager.Instance();
        if (mji == null || mji->FavorState == null || mji->FavorState->UpdateState != 2)
            return result;

        var agentRequired = AgentFavorRequirements();
        for (var i = 3; i < 6; i++)
        {
            int craft = mji->FavorState->CraftObjectIds[i];
            if (craft == 0)
                continue;

            var required = agentRequired.TryGetValue(craft, out var r) ? r
                : i - 3 < defaultRequired.Length ? defaultRequired[i - 3] : 0;
            var left = required - mji->FavorState->NumDelivered[i] - alreadyMade(craft);
            if (left > 0)
                result.Add(new FavorRequirement(craft, left));
        }

        return result;
    }

    private static Dictionary<int, int> AgentFavorRequirements()
    {
        var map = new Dictionary<int, int>();
        var agent = AgentMJINekomimiRequest.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive() || agent->Data == null || agent->Data->UpdateState < 2)
            return map;

        foreach (ref var item in agent->Data->Items)
        {
            if (item.CraftObjectId != 0)
                map.TryAdd(item.CraftObjectId, item.NumRequired);
        }

        return map;
    }

    public AgendaView? ReadAgenda()
    {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive() || agent->Data == null || agent->Data->UpdateState != 2)
            return null;

        var data = agent->Data;
        var workshops = new List<IReadOnlyList<(int, int)>>();
        foreach (ref var ws in data->WorkshopSchedules)
        {
            var entries = new List<(int, int)>();
            for (var e = 0; e < ws.NumScheduleEntries && e < ws.EntryData.Length; e++)
                entries.Add(((int)ws.EntryData[e].CraftObjectId, ws.EntryData[e].StartingSlot));
            workshops.Add(entries);
        }

        return new AgendaView(data->CycleDisplayed, data->CycleInProgress, (int)data->Groove, data->RestCycles, workshops);
    }

    // ---- Writing ----

    /// <summary>
    /// Open the agenda. UNVERIFIED: whether Show() works away from the Tactful Taskmaster — a guide says the agenda is
    /// opened by speaking to it. Returns true when the agent reports active.
    /// </summary>
    public bool OpenAgenda()
    {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null)
            return false;
        if (!agent->AgentInterface.IsAgentActive())
            agent->AgentInterface.Show();
        return agent->AgentInterface.IsAgentActive();
    }

    public void DisplayCycle(int cycle)
    {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent != null && agent->Data != null)
            agent->SetDisplayedCycle(cycle);
    }

    /// <summary>
    /// Queue one workshop's chain for a cycle (0-6 this week, 7-13 next) via MJIManager.ScheduleCraft, back to back
    /// from hour 0, then refresh the agenda window the way visland does.
    /// </summary>
    public void ScheduleChain(int cycle, int workshop, IReadOnlyList<int> chain, CraftCatalog catalog)
    {
        var mji = MJIManager.Instance();
        if (mji == null)
            return;

        var slot = 0;
        foreach (var id in chain)
        {
            mji->ScheduleCraft((ushort)id, (byte)((slot + 17) % 24), (byte)cycle, (byte)workshop);
            slot += catalog.Get(id)!.Hours;
        }

        Refresh();
    }

    /// <summary>
    /// Set rest cycles (bit i = cycle i; 0-6 this week, 7-13 next) — visland's agent event 5 after writing
    /// NewRestCycles. WORKABLE, verify in game.
    /// </summary>
    public bool SetRestCycles(uint mask)
    {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null)
            return false;

        agent->Data->NewRestCycles = mask;
        var value = new AtkValue();
        value.SetInt(0);
        var ret = new AtkValue();
        agent->AgentInterface.ReceiveEvent(&ret, &value, 1, 5);
        return true;
    }

    private static void Refresh()
    {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null)
            return;

        agent->SetDisplayedCycle(agent->Data->CycleDisplayed);
        agent->Data->Flags1 |= AgentMJICraftSchedule.DataFlags1.MaterialsUpdated;
    }
}
