using System;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Progression;
using Calypso.Core.Tasks;
using Calypso.Game;

namespace Calypso.Progression;

/// <summary>
/// Construct a building at a free logboard, the way probes 10 (Workshop I at Facility II, Korha) and 11 (Cozy Cabin I,
/// Saar) recorded it — the two were identical (claude-memory.md "Probe 10"):
/// walk to the logboard → interact → SelectString [i:0] (its first option constructs; the other is "Nothing.") →
/// MJIBuilding: [i:1,i:1] → [i:0,i:1] → [i:2,U] → [i:3,U] + [i:-2] (close=false) → "Skip cutscene?" [i:0] →
/// materials gone and the building appears.
///
/// What [1,x] and [0,x] select is not settled (both recordings offered one category with one building), so Calypso only
/// clicks when the window shows exactly that shape with the building it wants, and stops otherwise.
/// </summary>
public sealed class ConstructRun
{
    private const string Menu = "SelectString";
    private const string Window = "MJIBuilding";
    private const float InteractRange = 3f;
    private const int TierNameIndex = 12; // MJIBuilding AtkValues[12] = the entry ("Workshop I", "Cozy Cabin I")
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClickGap = TimeSpan.FromSeconds(0.7);

    private enum Phase
    {
        Walk,
        Menu,
        Window,
        After,
    }

    private readonly IslandActions _act;
    private readonly IslandTravel _nav;
    private readonly IslandLayout _layout;

    private Buildable _build = null!;
    private BuildingPlace? _place;
    private Phase _phase;
    private DateTime _phaseStart;
    private DateTime _lastAction;
    private DateTime? _readySince;
    private int _clicks;
    private int _interactTries;
    private int _skipAnswers;
    private int _startProgress;

    public ConstructRun(IslandActions act, IslandTravel nav, IslandLayout layout)
    {
        _act = act;
        _nav = nav;
        _layout = layout;
    }

    public void Begin(Buildable build, IslandSnapshot island, DateTime now)
    {
        _build = build;
        _place = _layout.FreePlaces(build.Building, island).FirstOrDefault();
        _startProgress = island.Progress;
        _clicks = _interactTries = _skipAnswers = 0;
        _readySince = null;
        _lastAction = DateTime.MinValue;
        Enter(Phase.Walk, now);
    }

    public StepResult Step(IslandSnapshot island, DateTime now)
    {
        _nav.Update(now);
        if (Built(island))
        {
            if (!_act.Busy && !_act.IsAddonVisible(Menu))
                return StepResult.Succeeded($"constructed {_build.TierName}");
            var r = Answer(now); // the cutscene prompt can come after the building already counts as built
            return r.Outcome == StepOutcome.Failed ? StepResult.Succeeded($"constructed {_build.TierName} ({r.Detail})") : r;
        }

        if (_place == null)
            return StepResult.Failed($"no free logboard for {_build.TierName} (village development level {island.VillageDevelopmentLevel})");

        return _phase switch
        {
            Phase.Walk => Walk(now),
            Phase.Menu => ChooseConstruct(now),
            Phase.Window => Click(now),
            Phase.After => now - _phaseStart < TimeSpan.FromSeconds(45)
                ? Answer(now)
                : StepResult.Failed($"{_build.TierName} did not appear after confirming the construction"),
            _ => StepResult.Failed("unknown phase"),
        };
    }

    public void Abort() => _nav.Stop();

    private bool Built(IslandSnapshot island) =>
        island.Progress != _startProgress
        || (_build.Building == 0 ? island.CabinLevel > 0
            : (_build.Building == 1 ? island.Workshops : island.Granaries).Any(b => b.IsBuilt && b.PlaceId == _place?.Row));

    private StepResult Walk(DateTime now)
    {
        if (_nav.Provider.Length == 0)
            return StepResult.Failed(_nav.Unavailable);

        var board = _act.Nearest(_place!.Logboard);
        var where = board?.Position ?? _layout.EObjPosition(_place.Logboard);
        if (where == null)
            return StepResult.Failed($"logboard {_place.Logboard} isn't in the island layout");
        if (board == null && _act.DistanceTo(where.Value) < 5f)
            return StepResult.Failed($"the logboard for {_build.TierName} isn't where the layout puts it");

        if (board != null && _act.DistanceTo(board.Position) <= InteractRange && !_nav.IsMoving)
        {
            if (_act.IsAddonVisible(Menu))
            {
                Enter(Phase.Menu, now);
                return StepResult.Running("reading the logboard");
            }

            if (now - _lastAction > TimeSpan.FromSeconds(3))
            {
                if (++_interactTries > 3)
                    return StepResult.Failed("the logboard didn't open its menu");
                _lastAction = now;
                _act.Interact(board);
            }

            return StepResult.Running("opening the logboard");
        }

        if (!_nav.IsMoving && now - _lastAction > TimeSpan.FromSeconds(3))
        {
            _lastAction = now;
            _nav.MoveCloseTo(where.Value, InteractRange - 1f);
        }

        return StepResult.Running($"walking to the logboard for {_build.TierName}");
    }

    private StepResult ChooseConstruct(DateTime now)
    {
        if (_act.IsAddonReady(Window))
        {
            Enter(Phase.Window, now);
            return StepResult.Running("construction window open");
        }

        if (!_act.IsAddonReady(Menu))
            return now - _phaseStart < TimeSpan.FromSeconds(10)
                ? StepResult.Running("waiting for the logboard menu")
                : StepResult.Failed("the logboard menu closed");

        if (now - _lastAction < TimeSpan.FromSeconds(1))
            return StepResult.Running("reading the logboard");

        // Recorded shape: [.., .., title, .., .., .., .., option 0, option 1] with two options ("Construct…", "Nothing.").
        // An empty place offers exactly that; anything else is a place Calypso doesn't understand — leave it alone.
        var options = _act.AddonStrings(Menu).Skip(7).Where(s => s != null).ToList();
        if (options.Count != 2)
        {
            _act.CloseAddon(Menu);
            return StepResult.Failed($"the logboard offered something else ({string.Join(" / ", options)}) — construct it by hand");
        }

        _lastAction = now;
        _act.FireCallback(Menu, true, 0);
        return StepResult.Running($"choosing \"{options[0]}\"");
    }

    private StepResult Click(DateTime now)
    {
        if (!_act.IsAddonVisible(Window))
        {
            if (_clicks >= 4)
            {
                Enter(Phase.After, now);
                return StepResult.Running($"constructing {_build.TierName}");
            }

            return StepResult.Failed("the construction window closed early");
        }

        if (!_act.IsAddonReady(Window))
        {
            _readySince = null;
            return StepResult.Running("waiting for the construction window");
        }

        _readySince ??= now;
        if (now - _readySince.Value < SettleTime || now - _lastAction < ClickGap)
            return StepResult.Running($"constructing {_build.TierName}");

        if (_clicks == 0)
        {
            var shown = _act.AddonStrings(Window);
            var entry = shown.Count > TierNameIndex ? shown[TierNameIndex] : null;
            if (!string.Equals(entry, _build.TierName, StringComparison.OrdinalIgnoreCase))
            {
                _act.FireCallback(Window, false, -2);
                return StepResult.Failed($"the construction window offers \"{entry}\", not {_build.TierName} — construct it by hand");
            }
        }

        _lastAction = now;
        switch (_clicks++)
        {
            case 0:
                _act.FireCallback(Window, true, 1, 1);
                break;
            case 1:
                _act.FireCallback(Window, true, 0, 1);
                break;
            case 2:
                _act.FireCallback(Window, true, 2, IslandActions.Undefined);
                break;
            default:
                // The player's confirm and the window's own close came in the same frame.
                _act.FireCallback(Window, true, 3, IslandActions.Undefined);
                _act.FireCallback(Window, false, -2);
                break;
        }

        return StepResult.Running($"constructing {_build.TierName}");
    }

    /// <summary>After confirming: answer "Skip cutscene?" with its first option (as recorded), then wait.</summary>
    private StepResult Answer(DateTime now)
    {
        if (_act.IsAddonReady(Menu) && now - _lastAction > TimeSpan.FromSeconds(1.5))
        {
            if (++_skipAnswers > 3)
                return StepResult.Failed("a menu kept asking after the construction — answer it by hand");
            var options = _act.AddonStrings(Menu).Skip(7).Where(s => s != null).ToList();
            if (options.Count != 2)
                return StepResult.Failed($"an unexpected menu after construction ({string.Join(" / ", options)})");
            _lastAction = now;
            _act.FireCallback(Menu, true, 0);
            return StepResult.Running("skipping the cutscene");
        }

        return StepResult.Running($"constructing {_build.TierName}");
    }

    private void Enter(Phase phase, DateTime now)
    {
        _phase = phase;
        _phaseStart = now;
        _readySince = null;
    }
}
