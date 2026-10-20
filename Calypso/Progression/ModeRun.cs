using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Tasks;
using Calypso.Game;
using Dalamud.Game.ClientState.Objects.Types;

namespace Calypso.Progression;

/// <summary>
/// The island's "mode + item + interact" pattern, as recorded for sowing and watering (claude-memory.md, probes
/// 2026-10-17): switch the HUD to a mode, choose the mode's item if it takes one, walk to a target object, interact,
/// and finish when the tutorial step moves on. Built to be reused for Capture (restraint + animal) and Feed (feed +
/// pasture animal) once those are recorded.
/// </summary>
public sealed class ModeRun
{
    private const float InteractRange = 3.5f;

    private readonly IslandActions _act;
    private readonly IslandTravel _travel;
    private readonly IslandData _data;

    private uint _mode;
    private uint _modeValue;
    private IReadOnlyList<uint> _items = Array.Empty<uint>();
    private Func<IGameObject, bool> _isTarget = _ => false;
    private string _what = string.Empty;
    private int _startProgress;
    private IGameObject? _target;
    private int _interacts;
    private int _itemTries;
    private DateTime _phaseStart;
    private DateTime _lastRequest;
    private DateTime _lastInteract;
    private Func<System.Numerics.Vector3?>? _search;
    private System.Numerics.Vector3? _searchSpot;
    private DateTime _lastSearchMove;
    private static readonly TimeSpan SearchLimit = TimeSpan.FromMinutes(10);
    private readonly HashSet<nint> _unreachable = new();
    private int _moveTries;

    public ModeRun(IslandActions act, IslandTravel travel, IslandData data)
    {
        _act = act;
        _travel = travel;
        _data = data;
    }

    /// <param name="mode">MJIHudMode sheet row (2 Sow, 3 Water, 8 Capture...) — converted to the game's mode value.</param>
    /// <param name="items">Acceptable mode item ids (any one will do), or empty when the mode takes none.</param>
    /// <param name="isTarget">Which loaded objects to interact with.</param>
    /// <param name="search">Where to look next when no target is loaded (null = only what's nearby).</param>
    public void Begin(uint mode, IReadOnlyList<uint> items, Func<IGameObject, bool> isTarget, string what, int progress, DateTime now,
        Func<System.Numerics.Vector3?>? search = null)
    {
        _search = search;
        _searchSpot = null;
        _unreachable.Clear();
        _moveTries = 0;
        _lastSearchMove = DateTime.MinValue;
        _mode = mode;
        _modeValue = _data.ModeValue(mode);
        _items = items;
        _isTarget = isTarget;
        _what = what;
        _startProgress = progress;
        _target = null;
        _interacts = 0;
        _itemTries = 0;
        _phaseStart = now;
        _lastRequest = _lastInteract = DateTime.MinValue;
    }

    public StepResult Step(DateTime now, int progress)
    {
        _travel.Update(now);

        if (progress != _startProgress)
        {
            _act.CloseModeMenu();
            return StepResult.Succeeded(_what);
        }

        if (_act.HudMode != _modeValue)
        {
            if (now - _phaseStart > TimeSpan.FromSeconds(10))
                return StepResult.Failed($"could not switch the HUD to {ModeName}");
            if (now - _lastRequest > TimeSpan.FromSeconds(1))
            {
                _lastRequest = now;
                if (!_act.RequestHudMode(_data.HudModeIcons.GetValueOrDefault(_mode)))
                    return StepResult.Failed($"{ModeName} isn't in the mode menu yet");
            }

            return StepResult.Running($"switching to {ModeName}");
        }

        if (_items.Count > 0 && !_items.Contains(_act.HudModeItem))
        {
            // The item menu is read and the entry holding an acceptable item is picked (probe-decoded layout).
            if (_itemTries >= 6)
                return StepResult.Failed($"could not choose an item for {ModeName}");
            if (now - _lastRequest > TimeSpan.FromSeconds(1))
            {
                _lastRequest = now;
                _itemTries++;
                if (!_act.RequestModeItem(_items))
                    return StepResult.Failed($"the {ModeName} item menu offers none of the items it needs");
            }

            return StepResult.Running($"choosing the item for {ModeName}");
        }

        _act.CloseModeMenu();

        if (_target == null || !_target.IsTargetable || !_isTarget(_target))
        {
            _target = _act.Nearby(o => _isTarget(o) && !_unreachable.Contains(o.Address)).OrderBy(o => _act.DistanceTo(o.Position)).FirstOrDefault();
            _moveTries = 0;
            // [REPLACED: gave up after 20 s when nothing was loaded nearby — Saar's capture step, 2026-10-19 22:08]
            // if (_target == null)
            //     return now - _phaseStart > TimeSpan.FromSeconds(20)
            //         ? StepResult.Failed($"nothing to {_what} nearby")
            //         : StepResult.Running($"looking for somewhere to {_what}");
            if (_target == null)
                return Search(now);
            _searchSpot = null;
        }

        if (_act.DistanceTo(_target.Position) > InteractRange || _travel.IsMoving)
        {
            if (!_travel.IsMoving)
            {
                // [REPLACED: re-pathed to the same target forever when the path ended short (a fence, an animal in a pen)]
                // _travel.MoveCloseTo(_target.Position, InteractRange - 1f);
                if (++_moveTries > 3)
                {
                    _unreachable.Add(_target.Address);
                    var name = _target.Name.TextValue;
                    _target = null;
                    return StepResult.Running($"can't reach {name}, trying another");
                }

                _travel.MoveCloseTo(_target.Position, InteractRange - 1f);
            }

            return StepResult.Running($"walking to {_target.Name.TextValue}");
        }

        if (_act.Busy || now - _lastInteract < TimeSpan.FromSeconds(3))
            return StepResult.Running($"{_what}: {_target.Name.TextValue}");

        if (_interacts >= 4)
            return StepResult.Failed($"interacting with {_target.Name.TextValue} didn't move the step on");

        _act.Interact(_target);
        _interacts++;
        _lastInteract = now;
        return StepResult.Running($"{_what}: {_target.Name.TextValue}");
    }

    /// <summary>Nothing loaded nearby: walk to the next place to look, until one turns up or the search runs out.</summary>
    private StepResult Search(DateTime now)
    {
        if (_search == null)
            return now - _phaseStart > TimeSpan.FromSeconds(20)
                ? StepResult.Failed($"nothing to {_what} nearby")
                : StepResult.Running($"looking for somewhere to {_what}");
        if (now - _phaseStart > SearchLimit)
            return StepResult.Failed($"searched for {SearchLimit.TotalMinutes:0} minutes and found nothing to {_what}");

        var arrived = _searchSpot is { } at && (_act.DistanceTo(at) < 20f || (!_travel.IsMoving && now - _lastSearchMove > TimeSpan.FromSeconds(5)));
        if (_searchSpot == null || arrived)
        {
            _searchSpot = _search();
            if (_searchSpot == null)
                return StepResult.Failed($"looked everywhere and found nothing to {_what}");
            _lastSearchMove = now;
            _travel.MoveCloseTo(_searchSpot.Value, 10f);
        }

        return StepResult.Running($"looking for something to {_what}");
    }

    public void Abort()
    {
        _travel.Stop();
        _act.CloseModeMenu();
    }

    private string ModeName => _data.HudModeNames.TryGetValue(_mode, out var n) ? $"{n} mode" : $"mode {_mode}";
}
