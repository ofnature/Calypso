using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Calypso.Game;

/// <summary>
/// Island movement with the Isle Return shortcut (user request 2026-10-17): before a long walk, when the destination is
/// much nearer the cozy cabin than to the player and Isle Return is ready, cast it (3 s) and walk the rest from the
/// cabin. Otherwise just paths through <see cref="NavClient"/>.
///
/// Isle Return = Action 29573 (validated against the Action sheet at load; the shortcut turns itself off if the row is
/// gone). Isle Sprint = Action 31314 (also validated): cast whenever a walk still has more than 50 y to go and it's
/// ready, at most every 10 s (user request 2026-10-17). Where it lands is learned the first time it's used and remembered; until then the cozy cabin logboard
/// (EObj 2013004 in the layout) stands in for it.
/// </summary>
public sealed unsafe class IslandTravel
{
    private const uint IsleReturn = 29573;
    private const uint IsleSprint = 31314;
    private const float SprintWhenFartherThan = 50f;
    private static readonly TimeSpan SprintEvery = TimeSpan.FromSeconds(10);
    private const uint CabinLogboard = 2013004;

    /// <summary>Only worth the 3 s cast when it saves a real walk.</summary>
    private const float MinWalk = 120f;
    private const float MinSaving = 80f;

    private readonly NavClient _nav;
    private readonly IslandActions _act;
    private readonly Func<Vector3?> _landing;
    private readonly Action<Vector3> _learnLanding;
    private readonly IPluginLog _log;
    private readonly bool _returnExists;
    private readonly bool _sprintExists;
    private DateTime _lastSprint = DateTime.MinValue;
    private Vector3? _dest;
    private readonly Vector3? _landingEstimate;

    private bool _returning;
    private DateTime _returnStart;
    private Vector3 _returnFrom;
    private Vector3 _pendingDest;
    private float _pendingRange;
    private bool _pendingFly;

    public IslandTravel(NavClient nav, IslandActions act, IslandLayout layout, Dalamud.Plugin.Services.IDataManager data,
        Func<Vector3?> landing, Action<Vector3> learnLanding, IPluginLog log)
    {
        _nav = nav;
        _act = act;
        _landing = landing;
        _learnLanding = learnLanding;
        _log = log;

        var actions = data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        _returnExists = (actions.GetRowOrDefault(IsleReturn)?.Name.ExtractText() ?? string.Empty).Length > 0;
        if (!_returnExists)
            log.Warning("Isle Return (Action {0}) not found in the Action sheet; travel shortcut disabled", IsleReturn);
        _sprintExists = (actions.GetRowOrDefault(IsleSprint)?.Name.ExtractText() ?? string.Empty).Length > 0;
        if (!_sprintExists)
            log.Warning("Isle Sprint (Action {0}) not found in the Action sheet; sprinting disabled", IsleSprint);

        _landingEstimate = layout.EObjPosition(CabinLogboard);
    }

    public string Provider => _nav.Provider;
    public string Unavailable => _nav.Unavailable;
    public bool IsMoving => _returning || _nav.IsMoving;

    /// <summary>Head for <paramref name="dest"/>, taking Isle Return first when it saves a long walk. False = no navigation.</summary>
    public bool MoveCloseTo(Vector3 dest, float range, bool fly = false)
    {
        _dest = dest;
        if (_returning)
        {
            (_pendingDest, _pendingRange, _pendingFly) = (dest, range, fly);
            return true;
        }

        if (ShouldReturn(dest) && TryCastReturn())
        {
            (_pendingDest, _pendingRange, _pendingFly) = (dest, range, fly);
            return true;
        }

        return _nav.MoveCloseTo(dest, range, fly);
    }

    /// <summary>Drive an Isle Return in progress and sprint on long walks. Call every tick from the run that moves.</summary>
    public void Update(DateTime now)
    {
        if (_act.PlayerPosition is not { } me)
            return;

        if (!_returning)
        {
            TrySprint(me, now);
            return;
        }

        var jumped = Vector3.Distance(me, _returnFrom) > 40f;
        if (jumped && !_act.Busy)
        {
            _returning = false;
            _learnLanding(me);
            _nav.MoveCloseTo(_pendingDest, _pendingRange, _pendingFly);
            return;
        }

        if (now - _returnStart > TimeSpan.FromSeconds(10))
        {
            // Cast interrupted or refused: walk instead.
            _returning = false;
            _log.Information("Isle Return did not land within 10 s; walking");
            _nav.MoveCloseTo(_pendingDest, _pendingRange, _pendingFly);
        }
    }

    public void Stop()
    {
        _returning = false;
        _dest = null;
        _nav.Stop();
    }

    private void TrySprint(Vector3 me, DateTime now)
    {
        if (!_sprintExists || _dest is not { } dest || !_nav.IsMoving || now - _lastSprint < SprintEvery)
            return;
        if (Vector3.Distance(me, dest) <= SprintWhenFartherThan)
            return;

        var am = ActionManager.Instance();
        if (am == null || am->GetActionStatus(ActionType.Action, IsleSprint) != 0)
            return;

        _lastSprint = now;
        am->UseAction(ActionType.Action, IsleSprint);
    }

    private bool ShouldReturn(Vector3 dest)
    {
        if (!_returnExists || _act.PlayerPosition is not { } me)
            return false;
        var landing = _landing() ?? _landingEstimate;
        if (landing is not { } land)
            return false;

        var walk = Vector3.Distance(me, dest);
        var fromCabin = Vector3.Distance(land, dest);
        return walk > MinWalk && walk - fromCabin > MinSaving;
    }

    private bool TryCastReturn()
    {
        var am = ActionManager.Instance();
        if (am == null || am->GetActionStatus(ActionType.Action, IsleReturn) != 0 || _act.Busy)
            return false;

        _nav.Stop();
        if (!am->UseAction(ActionType.Action, IsleReturn))
            return false;

        _returning = true;
        _returnStart = DateTime.UtcNow;
        _returnFrom = _act.PlayerPosition ?? Vector3.Zero;
        _log.Information("Isle Return toward the cabin to shorten a walk");
        return true;
    }
}
