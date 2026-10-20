using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Market;
using Calypso.Core.Tasks;
using Calypso.Game;

namespace Calypso.Exports;

/// <summary>Per-character selling settings (user rule 2026-10-17: at 999, sell all but 100).</summary>
public sealed class MarketSettings
{
    /// <summary>Sell an item once it reaches its Isleventory cap, down to this many.</summary>
    public int Keep { get; set; } = 100;

    /// <summary>Spend Islander's Cowries (from exporting) on market-board items.</summary>
    public bool SpendIslanders { get; set; } = true;

    /// <summary>Spend Seafarer's Cowries (the workshop's) too. Off: they also buy the island's untradable rewards.</summary>
    public bool SpendSeafarers { get; set; }

    public int ReserveIslanders { get; set; }
    public int ReserveSeafarers { get; set; }
}

/// <summary>
/// Walk to the Enterprising Exporter, open the shop, sell everything at its cap down to <see cref="MarketSettings.Keep"/>,
/// and close it. Answers the opening menu and any shipping confirmation with the first option (the exporter's "export"
/// and "yes"). Succeeds only when the Isleventory counts have dropped.
/// </summary>
public sealed class ExportRun
{
    /// <summary>The Enterprising Exporter (ENpc 1043464, planlive.lgb at (-266.3, 41.0, 207.9)).</summary>
    public const uint ExporterNpc = 1043464;

    private const float TalkRange = 4f;
    private static readonly string[] Dialogs = { "SelectString", "SelectIconString", "SelectYesno" };

    private enum Phase
    {
        Approach,
        Opening,
        Shipping,
        Verify,
    }

    private readonly IslandActions _act;
    private readonly IslandTravel _travel;
    private readonly IslandLayout _layout;
    private readonly ExportGame _shop;

    private Phase _phase;
    private DateTime _phaseStart;
    private DateTime _lastAction;
    private int _keep;
    private Func<int, int> _reserved = _ => 0;
    private ExportPlan? _plan;
    private int _batch;
    private Dictionary<uint, int> _before = new();

    public ExportRun(IslandActions act, IslandTravel travel, IslandLayout layout, ExportGame shop)
    {
        _act = act;
        _travel = travel;
        _layout = layout;
        _shop = shop;
    }

    /// <summary>Last plan's result, for status lines: what sold, what was skipped.</summary>
    public string Summary { get; private set; } = string.Empty;

    public void Begin(int keep, Func<int, int> reserved, DateTime now)
    {
        _keep = keep;
        _reserved = reserved;
        _plan = null;
        _batch = 0;
        Enter(Phase.Approach, now);
    }

    public StepResult Step(DateTime now)
    {
        _travel.Update(now);

        switch (_phase)
        {
            case Phase.Approach:
            {
                if (_shop.Read() != null)
                {
                    Enter(Phase.Shipping, now);
                    return StepResult.Running("the exporter is open");
                }

                var npc = _act.Nearest(ExporterNpc);
                var spot = npc?.Position ?? _layout.Npcs.FirstOrDefault(n => n.BaseId == ExporterNpc)?.Position;
                if (spot is not { } dest)
                    return StepResult.Failed("could not find the Enterprising Exporter");

                if (npc != null && _act.DistanceTo(npc.Position) <= TalkRange && !_travel.IsMoving)
                {
                    _act.Interact(npc);
                    Enter(Phase.Opening, now);
                    return StepResult.Running("talking to the exporter");
                }

                if (!_travel.IsMoving && now - _lastAction > TimeSpan.FromSeconds(3))
                {
                    _lastAction = now;
                    if (!_travel.MoveCloseTo(dest, TalkRange - 1f))
                        return StepResult.Failed(_travel.Unavailable);
                }

                return StepResult.Running("walking to the exporter");
            }

            case Phase.Opening:
                if (_shop.Read() != null)
                {
                    Enter(Phase.Shipping, now);
                    return StepResult.Running("the exporter is open");
                }

                AnswerDialog(now);
                return now - _phaseStart > TimeSpan.FromSeconds(15)
                    ? StepResult.Failed("the exporter's window didn't open — record opening it with the probe")
                    : StepResult.Running("opening the exporter");

            case Phase.Shipping:
            {
                if (_shop.Read() is not { } shop)
                    return StepResult.Failed("the exporter's window closed");

                if (_plan == null)
                {
                    _plan = ExportPlanner.Plan(shop.Items, _keep, _reserved, shop.Held, shop.Caps);
                    _before = shop.Items.ToDictionary(i => i.ItemId, i => i.Held);
                    Summary = _plan.Batches.Count == 0
                        ? (_plan.Skipped.Count > 0 ? $"nothing sold: {string.Join("; ", _plan.Skipped)}" : "nothing at its cap")
                        : $"selling {_plan.Batches.Sum(b => b.Items.Count)} item type(s) for {_plan.IslandersGained} Islander's / {_plan.SeafarersGained} Seafarer's Cowries";
                    if (_plan.Batches.Count == 0)
                    {
                        Close();
                        return StepResult.Succeeded(Summary);
                    }
                }

                if (AnswerDialog(now))
                    return StepResult.Running("confirming the shipment");

                if (now - _lastAction < TimeSpan.FromSeconds(1.5))
                    return StepResult.Running(Summary);

                if (_batch < _plan.Batches.Count)
                {
                    _shop.Ship(_plan.Batches[_batch++]);
                    _lastAction = now;
                    return StepResult.Running(Summary);
                }

                Enter(Phase.Verify, now);
                return StepResult.Running("checking the shipment");
            }

            case Phase.Verify:
            {
                if (AnswerDialog(now))
                    return StepResult.Running("confirming the shipment");

                var shop = _shop.Read();
                var sold = shop != null && shop.Items.Any(i => _before.TryGetValue(i.ItemId, out var was) && i.Held < was);
                if (sold)
                {
                    Close();
                    return StepResult.Succeeded(Summary);
                }

                return now - _phaseStart > TimeSpan.FromSeconds(10)
                    ? StepResult.Failed("the shipment didn't go through — record a manual bulk export with the probe")
                    : StepResult.Running("checking the shipment");
            }
        }

        return StepResult.Failed("unknown phase");
    }

    public void Abort()
    {
        _travel.Stop();
        Close();
    }

    /// <summary>First option of any open dialog (the exporter's "export" / the shipment's "yes"). True when one was answered.</summary>
    private bool AnswerDialog(DateTime now)
    {
        var open = Dialogs.FirstOrDefault(_act.IsAddonVisible);
        if (open == null || now - _lastAction < TimeSpan.FromSeconds(1))
            return open != null;

        _act.FireCallback(open, true, 0);
        _lastAction = now;
        return true;
    }

    private void Close()
    {
        if (_act.IsAddonVisible("MJIDisposeShop"))
            _act.CloseAddon("MJIDisposeShop");
    }

    private void Enter(Phase phase, DateTime now)
    {
        _phase = phase;
        _phaseStart = now;
    }
}
