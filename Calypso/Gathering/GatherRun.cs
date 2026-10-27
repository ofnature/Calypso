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
    // [REPLACED: 3 minutes ended Saar's copper run while he walked to the next rocks (2026-10-22 19:21) — dead ends are
    // caught on their own now (unreachable types, no navmesh, items no node gives), so this only guards a stuck run]
    // private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan SpotCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NodeCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NavWaitLimit = TimeSpan.FromMinutes(3);
    private DateTime? _navMissingSince;

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
    private bool _spotInArea;
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

    /// <summary>
    /// Node types whose nodes couldn't be reached this run, with how many (underwater nodes: the navmesh can't walk to
    /// them — Saar 2026-10-22). At <see cref="UnreachableLimit"/> the type is skipped until the next run.
    /// </summary>
    private readonly Dictionary<string, int> _unreachable = new(StringComparer.OrdinalIgnoreCase);
    private const int UnreachableLimit = 3;

    /// <summary>Gather until each pouch row reaches its absolute target count (capped at the stack size).</summary>
    public void Begin(IReadOnlyDictionary<int, int> targets, DateTime now)
    {
        _targets.Clear();
        _dropped.Clear();
        _gaveNothing.Clear();
        _unreachable.Clear();
        _navMissingSince = null;
        _seenAt.Clear();
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
        // [REPLACED: no navmesh failed the run at once — but Ariadne rebuilds the island mesh whenever the island's layout
        // key changes (Saar 2026-10-22 19:05, "navmesh not ready … (first visit builds it)"), which takes a while]
        // if (now - _lastProgress > NoProgressLimit)
        //     return StepResult.Failed($"nothing gathered for {NoProgressLimit.TotalMinutes:0} minutes");
        // if (_nav.Provider.Length == 0)
        //     return StepResult.Failed(_nav.Unavailable);
        if (_nav.Provider.Length == 0)
        {
            _navMissingSince ??= now;
            if (now - _navMissingSince.Value > NavWaitLimit)
                return StepResult.Failed(_nav.Unavailable);
            _lastProgress = now; // waiting for the mesh isn't "nothing gathered"
            return StepResult.Running($"waiting for the navmesh ({_nav.Unavailable})");
        }

        _navMissingSince = null;
        if (now - _lastProgress > NoProgressLimit)
            return StepResult.Failed($"nothing gathered for {NoProgressLimit.TotalMinutes:0} minutes");

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
        Remember(now);

        // Wanted items no node has given yet, and the items sharing their map area: a node known to give one of those
        // is the likeliest source (Island Limestone → whatever gives Island Stone).
        var unsourced = _book.Unsourced(wanted).ToList();
        var hints = unsourced.SelectMany(_tools.SameArea).ToHashSet();

        // [REPLACED: nodes whose every known drop is at its cap were still visited — the game refuses them ("You cannot
        // carry any more…"), so a search walked from one full node to the next (Korha 2026-10-19)]
        // var scored = Candidates(now).Select(o => (o, p: _book.Priority(o.Name.TextValue, signature, wanted))).ToList();
        // A hinted node still gets one try when its known drops are full: whether the game gives the new drop alone
        // when the old one is capped isn't known yet. A node type that gave nothing is skipped for the rest of the run.
        var scored = Candidates(now)
            .Where(o => !_gaveNothing.Contains(o.Name.TextValue) && _unreachable.GetValueOrDefault(o.Name.TextValue) < UnreachableLimit
                        && !_tools.UnderwaterNodes.Contains(o.Name.TextValue))
            .Select(o => (o, p: _book.Priority(o.Name.TextValue, signature, wanted),
                // [REPLACED: same-area items only — an item nobody has a neighbour for (Copper Ore) had no hint, and Saar
                // tried every underwater node on the east coast first (2026-10-22)]
                // hinted: !_book.Known(o.Name.TextValue, signature) && _book.Gives(o.Name.TextValue).Any(hints.Contains)))
                hinted: !_book.Known(o.Name.TextValue, signature)
                        && (_book.Gives(o.Name.TextValue).Any(hints.Contains) || unsourced.Any(u => _tools.InArea(u, o.Position)))))
            .Where(x => x.hinted || !AllFull(x.o.Name.TextValue))
            .ToList();
        if (scored.Any(x => x.p.Tier < 2))
            scored = scored.Where(x => x.p.Tier < 2).ToList(); // known non-producers only when nothing better is loaded

        // An unfound item the sheet places somewhere, and nothing loaded inside its area: go there (spot search below
        // prefers its spots) rather than trying whatever untried node is nearest — Saar kept going for coral formations
        // off the east coast while looking for copper ore (2026-10-22). Known producers of something wanted still count.
        if (unsourced.Any(u => _tools.Areas(u).Count > 0) && !scored.Any(x => x.hinted))
            scored = scored.Where(x => x.p.Rate > 0).ToList();

        // Everything wanted has a known source: exploring untried node types only wastes walks (Saar 2026-10-22, copper
        // 8/20 and vine 16/20, swimming at coral). No known producer loaded → the spot search walks to their areas.
        if (unsourced.Count == 0)
            scored = scored.Where(x => x.p.Rate > 0).ToList();

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

        // No producer loaded: go back to where producers of what's wanted stood earlier this run — gathered nodes respawn
        // (Saar "ran out of nodes" mining copper, 2026-10-22). The place visited longest ago first.
        var producers = _seenAt.Keys.Where(n => _book.BestRate(n, wanted) > 0 && !_gaveNothing.Contains(n)).ToHashSet();
        var back = _seenAt.Where(kv => producers.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .Where(p => !_spotChecked.TryGetValue(p, out var t) || now - t > SpotCooldown)
            .OrderBy(p => _spotChecked.TryGetValue(p, out var t) ? t : DateTime.MinValue)
            .ThenBy(p => _act.DistanceTo(p))
            .Cast<Vector3?>()
            .FirstOrDefault();
        if (back is { } where)
        {
            _node = null;
            _spot = where;
            _spotInArea = true; // must actually get there before it counts as checked
            _navTries = 0;
            _nav.MoveCloseTo(where, 10f);
            Enter(Phase.Travel, now);
            return StepResult.Running($"back to where {string.Join(" / ", producers.Take(2))} stood");
        }

        // Nothing loaded nearby: head for the nearest gathering spot not checked recently — one inside an unfound
        // item's area first (the sheet says where it grows).
        // [REPLACED: nearest spot anywhere]
        // var spot = _layout.GatherSpots
        //     .Where(s => !_spotChecked.TryGetValue(s, out var t) || now - t > SpotCooldown)
        //     .OrderBy(s => _act.DistanceTo(s))
        var spot = _layout.GatherSpots
            .Where(s => !_spotChecked.TryGetValue(s, out var t) || now - t > SpotCooldown)
            .OrderBy(s => unsourced.Any(u => _tools.InArea(u, s)) ? 0 : wanted.Any(w => _tools.InArea(w, s)) ? 1 : 2)
            .ThenBy(s => _act.DistanceTo(s))
            .Cast<Vector3?>()
            .FirstOrDefault();
        if (spot == null)
        {
            // [REPLACED: one item with no reachable node (underwater, a cave not open yet) ended the whole run]
            // return StepResult.Failed("no gathering node found anywhere on the island");

            // The whole island has been looked at: give up on what no node has ever given, then sweep again (nodes respawn).
            var noSource = unsourced.ToHashSet();
            foreach (var row in missing.Select(m => m.Row).Where(r => _catalog.Pouch.TryGetValue(r, out var p) && noSource.Contains(p.ItemId)))
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
        // [REPLACED: only unfound items' areas were walked to; known items' areas are worth reaching too]
        // _spotInArea = unsourced.Any(u => _tools.InArea(u, spot.Value));
        _spotInArea = wanted.Any(w => _tools.InArea(w, spot.Value));
        _navTries = 0;
        _nav.MoveCloseTo(spot.Value, 10f);
        Enter(Phase.Travel, now);
        // [REPLACED: one message for both kinds of walk]
        // return StepResult.Running("looking for gathering nodes");
        return StepResult.Running(_spotInArea ? "heading to where what's wanted grows" : "looking for gathering nodes");
    }

    /// <summary>Every item this node type is known to give is at its Isleventory cap — the game would refuse it.</summary>
    private bool AllFull(string node)
    {
        var gives = _book.Gives(node).ToList();
        return gives.Count > 0 && gives.All(i => _count(i) >= _tools.StackSize(i));
    }

    /// <summary>Note where each node type stands (this run) — positions to come back to once a node respawns.</summary>
    private void Remember(DateTime now)
    {
        foreach (var o in _act.NearbyGatheringPoints(_layout.NodeBaseIds))
        {
            var name = o.Name.TextValue;
            if (name.Length == 0)
                continue;
            if (!_seenAt.TryGetValue(name, out var list))
                _seenAt[name] = list = new List<Vector3>();
            if (list.Count < 40 && !list.Any(p => Vector3.Distance(p, o.Position) < 15f))
                list.Add(o.Position);
        }
    }

    private readonly Dictionary<string, List<Vector3>> _seenAt = new(StringComparer.OrdinalIgnoreCase);

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
            // [REPLACED: any loaded node ended the walk — on the island that's at once, so an unfound item's area was
            // marked checked without ever going there]
            // if (_act.NearbyGatheringPoints(_layout.NodeBaseIds).Any() || _act.DistanceTo(spot) < 25f)
            if (_act.DistanceTo(spot) < 25f || (!_spotInArea && _act.NearbyGatheringPoints(_layout.NodeBaseIds).Any()))
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
                {
                    _nodeUsed[_node.Position] = now;
                    _unreachable[_nodeName] = _unreachable.GetValueOrDefault(_nodeName) + 1; // after a few, the whole type is skipped
                }
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
