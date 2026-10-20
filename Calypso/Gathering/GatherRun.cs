using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Calypso.Core.Gathering;
using Calypso.Core.Tasks;
using Calypso.Core.Workshop;
using Calypso.Game;
using Dalamud.Game.ClientState.Objects.Types;

namespace Calypso.Gathering;

/// <summary>
/// Gathers island materials until every target count is reached. A step machine driven once per tick by the task
/// that owns it (the gather-goals task, or the tutorial when a step lists materials).
///
/// Per node: Gather mode on → pick a node → walk there → interact → wait for the Isleventory to change → record what
/// it gave in the <see cref="YieldBook"/> → next. Node choice: types the book knows give a missing item, then node
/// types never tried (that's how the book learns), then the rest. When no node is loaded nearby it walks toward the
/// nearest of the island's 505 gathering spots it hasn't checked recently. Nodes are the node EObjs
/// (ReactionEventObject, probe-verified 2026-10-17): interact in Gather mode, one interaction gave 1 item ~1.9 s later.
/// </summary>
public sealed class GatherRun
{
    private const float InteractRange = 3.5f;
    private const int StackCap = 999;
    private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SpotCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NodeCooldown = TimeSpan.FromSeconds(45);

    private enum Phase
    {
        Mode,
        Pick,
        Travel,
        Collect,
    }

    private readonly IslandActions _act;
    private readonly IslandTravel _nav;
    private readonly IslandLayout _layout;
    private readonly CraftCatalog _catalog;
    private readonly YieldBook _book;
    private readonly IslandData _data;
    private readonly GatherTools _tools;
    private readonly Func<uint, int> _count;

    private readonly Dictionary<int, int> _targets = new();
    private readonly Dictionary<Vector3, DateTime> _spotChecked = new();
    private readonly Dictionary<Vector3, DateTime> _nodeUsed = new();

    private Phase _phase;
    private DateTime _phaseStart;
    private DateTime _lastProgress;
    private IGameObject? _node;
    private Vector3? _spot;
    private string _nodeName = string.Empty;
    private Dictionary<uint, int> _before = new();
    private int _navTries;
    private DateTime _lastModeRequest;

    public GatherRun(IslandActions act, IslandTravel nav, IslandLayout layout, CraftCatalog catalog, YieldBook book, IslandData data,
        GatherTools tools, Func<uint, int> count)
    {
        _tools = tools;
        _act = act;
        _nav = nav;
        _layout = layout;
        _catalog = catalog;
        _book = book;
        _data = data;
        _count = count;
    }

    /// <summary>Called whenever a node gave something — the owner persists the book.</summary>
    public event Action? Learned;

    /// <summary>Items given up on this run: a full sweep of the island found no node that gives them.</summary>
    public IReadOnlyCollection<int> Dropped => _dropped;

    private readonly HashSet<int> _dropped = new();

    /// <summary>Node types that gave nothing this run (full bag) — skipped until the next run.</summary>
    private readonly HashSet<string> _gaveNothing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gather until each pouch row reaches its absolute target count (capped at the stack size).</summary>
    public void Begin(IReadOnlyDictionary<int, int> targets, DateTime now)
    {
        _targets.Clear();
        _dropped.Clear();
        _gaveNothing.Clear();
        _spotChecked.Clear();
        foreach (var (row, n) in targets)
            _targets[row] = Math.Min(n, StackCap);
        _node = null;
        _spot = null;
        _lastProgress = now;
        Enter(Phase.Mode, now);
    }

    /// <summary>Pouch rows still short, with how many are missing.</summary>
    public IEnumerable<(int Row, int Missing)> Missing() =>
        _targets.Select(t => (t.Key, Missing: t.Value - Count(t.Key))).Where(t => t.Missing > 0);

    /// <summary>The tool a pouch row needs that the character doesn't hold (MJIGatheringItem -> MJIGatheringTool), or null.</summary>
    public string? MissingTool(int pouchRow) => _catalog.Pouch.TryGetValue(pouchRow, out var p) ? _tools.MissingTool(p.ItemId) : null;

    public StepResult Step(DateTime now)
    {
        _nav.Update(now);
        var shortRows = Missing().ToList();
        var missing = shortRows.Where(m => MissingTool(m.Row) == null).ToList();
        if (shortRows.Count == 0)
            return StepResult.Succeeded("all materials gathered");
        if (missing.Count == 0)
        {
            var needs = shortRows.Select(m => MissingTool(m.Row)).Distinct();
            return StepResult.Failed($"what is left needs a tool you do not have yet: {string.Join(", ", needs)}");
        }
        if (now - _lastProgress > NoProgressLimit)
            return StepResult.Failed($"nothing gathered for {NoProgressLimit.TotalMinutes:0} minutes");
        if (_nav.Provider.Length == 0)
            return StepResult.Failed(_nav.Unavailable);

        switch (_phase)
        {
            case Phase.Mode:
                if (_act.HudMode == _data.ModeValue(IslandData.GatherModeRow))
                {
                    _act.CloseModeMenu();
                    Enter(Phase.Pick, now);
                    return StepResult.Running("in Gather mode");
                }

                if (now - _phaseStart > TimeSpan.FromSeconds(10))
                    return StepResult.Failed("could not switch the HUD to Gather mode");
                if (now - _lastModeRequest > TimeSpan.FromSeconds(1))
                {
                    _lastModeRequest = now;
                    _act.RequestHudMode(_data.HudModeIcons.GetValueOrDefault(IslandData.GatherModeRow));
                }

                return StepResult.Running("switching to Gather mode");

            case Phase.Pick:
                return Pick(missing, now);

            case Phase.Travel:
                return Travel(now);

            case Phase.Collect:
                return Collect(now);
        }

        return StepResult.Failed("unknown phase");
    }

    public void Abort()
    {
        _nav.Stop();
        _act.CloseModeMenu();
    }

    private StepResult Pick(List<(int Row, int Missing)> missing, DateTime now)
    {
        var wanted = missing.Select(m => _catalog.Pouch.TryGetValue(m.Row, out var p) ? p.ItemId : 0u).Where(i => i != 0).ToList();

        var signature = _tools.Signature();

        // Wanted items no node has given yet, and the items sharing their map area: a node known to give one of those
        // is the likeliest source (Island Limestone → whatever gives Island Stone).
        var hints = _book.Unsourced(wanted).SelectMany(_tools.SameArea).ToHashSet();

        // [REPLACED: nodes whose every known drop is at its cap were still visited — the game refuses them ("You cannot
        // carry any more…"), so a search walked from one full node to the next (Korha 2026-10-19)]
        // var scored = Candidates(now).Select(o => (o, p: _book.Priority(o.Name.TextValue, signature, wanted))).ToList();
        // A hinted node still gets one try when its known drops are full: whether the game gives the new drop alone
        // when the old one is capped isn't known yet. A node type that gave nothing is skipped for the rest of the run.
        var scored = Candidates(now)
            .Where(o => !_gaveNothing.Contains(o.Name.TextValue))
            .Select(o => (o, p: _book.Priority(o.Name.TextValue, signature, wanted),
                hinted: !_book.Known(o.Name.TextValue, signature) && _book.Gives(o.Name.TextValue).Any(hints.Contains)))
            .Where(x => x.hinted || !AllFull(x.o.Name.TextValue))
            .ToList();
        if (scored.Any(x => x.p.Tier < 2))
            scored = scored.Where(x => x.p.Tier < 2).ToList(); // known non-producers only when nothing better is loaded

        // [REPLACED: nearest within a tier, with no use of the sheet's area hint]
        // var node = scored.OrderBy(x => x.p.Tier).ThenBy(x => _act.DistanceTo(x.o.Position)).Select(x => x.o).FirstOrDefault();
        // Within a tier: hinted nodes first, then the nearest — walking dominates a node's yield (1 item per interaction).
        var node = scored
            .OrderBy(x => x.p.Tier)
            .ThenBy(x => x.hinted ? 0 : 1)
            .ThenBy(x => _act.DistanceTo(x.o.Position))
            .Select(x => x.o)
            .FirstOrDefault();

        if (node != null)
        {
            _node = node;
            _spot = null;
            _nodeName = node.Name.TextValue;
            _navTries = 0;
            _nav.MoveCloseTo(node.Position, InteractRange - 1f);
            Enter(Phase.Travel, now);
            return StepResult.Running($"going to {_nodeName}");
        }

        // Nothing loaded nearby: head for the nearest gathering spot not checked recently.
        var spot = _layout.GatherSpots
            .Where(s => !_spotChecked.TryGetValue(s, out var t) || now - t > SpotCooldown)
            .OrderBy(s => _act.DistanceTo(s))
            .Cast<Vector3?>()
            .FirstOrDefault();
        if (spot == null)
        {
            // [REPLACED: one item with no reachable node (underwater, a cave not open yet) ended the whole run]
            // return StepResult.Failed("no gathering node found anywhere on the island");

            // The whole island has been looked at: give up on what no node has ever given, then sweep again (nodes respawn).
            var unsourced = _book.Unsourced(wanted).ToHashSet();
            foreach (var row in missing.Select(m => m.Row).Where(r => _catalog.Pouch.TryGetValue(r, out var p) && unsourced.Contains(p.ItemId)))
            {
                _targets.Remove(row);
                _dropped.Add(row);
            }

            _spotChecked.Clear();
            // [REPLACED: blamed the island when full bags may have blocked the only source]
            // if (Missing().All(m => _dropped.Contains(m.Row)))
            //     return StepResult.Failed("no node on the island gives what's left");
            if (Missing().All(m => _dropped.Contains(m.Row)))
                return StepResult.Failed(_gaveNothing.Count == 0
                    ? "no node on the island gives what's left"
                    : $"no node gave what's left; full bags blocked {string.Join(", ", _gaveNothing)} — sell or use some and try again");
            return StepResult.Running(_dropped.Count > 0 ? $"no node gives {_dropped.Count} item(s); carrying on with the rest" : "sweeping the island again");
        }

        _node = null;
        _spot = spot;
        _navTries = 0;
        _nav.MoveCloseTo(spot.Value, 10f);
        Enter(Phase.Travel, now);
        return StepResult.Running("looking for gathering nodes");
    }

    /// <summary>Every item this node type is known to give is at its Isleventory cap — the game would refuse it.</summary>
    private bool AllFull(string node)
    {
        var gives = _book.Gives(node).ToList();
        return gives.Count > 0 && gives.All(i => _count(i) >= _tools.StackSize(i));
    }

    private IEnumerable<IGameObject> Candidates(DateTime now) =>
        _act.NearbyGatheringPoints(_layout.NodeBaseIds).Where(o => !_nodeUsed.TryGetValue(o.Position, out var t) || now - t > NodeCooldown);

    private StepResult Travel(DateTime now)
    {
        if (_node != null)
        {
            if (!_node.IsTargetable)
            {
                Enter(Phase.Pick, now);
                return StepResult.Running("node gone, picking another");
            }

            if (_act.DistanceTo(_node.Position) <= InteractRange && !_nav.IsMoving)
            {
                _before = Snapshot();
                _nodeUsed[_node.Position] = now;
                _act.Interact(_node);
                Enter(Phase.Collect, now);
                return StepResult.Running($"gathering {_nodeName}");
            }
        }
        else if (_spot is { } spot)
        {
            if (_act.NearbyGatheringPoints(_layout.NodeBaseIds).Any() || _act.DistanceTo(spot) < 25f)
            {
                _spotChecked[spot] = now;
                Enter(Phase.Pick, now);
                return StepResult.Running("checking the area");
            }
        }

        // Path finished short of the target, or never started: retry a couple of times, then give up on this target.
        if (!_nav.IsMoving && now - _phaseStart > TimeSpan.FromSeconds(3))
        {
            if (++_navTries > 3)
            {
                if (_node != null)
                    _nodeUsed[_node.Position] = now;
                if (_spot is { } s)
                    _spotChecked[s] = now;
                Enter(Phase.Pick, now);
                return StepResult.Running("couldn't reach it, picking another");
            }

            var dest = _node?.Position ?? _spot!.Value;
            _nav.MoveCloseTo(dest, _node != null ? InteractRange - 1f : 10f);
            _phaseStart = now;
        }

        return StepResult.Running(_node != null ? $"going to {_nodeName}" : "looking for gathering nodes");
    }

    private StepResult Collect(DateTime now)
    {
        var elapsed = now - _phaseStart;
        if (elapsed < TimeSpan.FromSeconds(1) || (_act.Busy && elapsed < TimeSpan.FromSeconds(15)))
            return StepResult.Running($"gathering {_nodeName}");

        var after = Snapshot();
        var gained = after.Where(kv => kv.Value > _before.GetValueOrDefault(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value - _before.GetValueOrDefault(kv.Key));

        if (gained.Count == 0 && elapsed < TimeSpan.FromSeconds(6))
            return StepResult.Running($"gathering {_nodeName}");

        _book.Record(_nodeName, _tools.Signature(), gained);
        if (gained.Count > 0)
        {
            _lastProgress = now;
            Learned?.Invoke();
        }
        else
        {
            _gaveNothing.Add(_nodeName); // a node always gives something unless its bag is full
        }

        Enter(Phase.Pick, now);
        // [REPLACED: "gave nothing" hid why]
        // return StepResult.Running(gained.Count > 0 ? $"got {gained.Values.Sum()} from {_nodeName}" : $"{_nodeName} gave nothing");
        return StepResult.Running(gained.Count > 0 ? $"got {gained.Values.Sum()} from {_nodeName}" : $"{_nodeName} gave nothing (bag full?) — skipping it this run");
    }

    private Dictionary<uint, int> Snapshot() =>
        _catalog.Pouch.Values.ToDictionary(p => p.ItemId, p => _count(p.ItemId));

    private int Count(int pouchRow) => _catalog.Pouch.TryGetValue(pouchRow, out var p) ? _count(p.ItemId) : 0;

    private void Enter(Phase phase, DateTime now)
    {
        _phase = phase;
        _phaseStart = now;
    }
}
