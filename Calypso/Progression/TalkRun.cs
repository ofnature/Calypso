using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core.Progression;
using Calypso.Core.Tasks;
using Calypso.Game;
using Dalamud.Game.ClientState.Objects.Types;

namespace Calypso.Progression;

/// <summary>
/// Walk to an island NPC, talk, and let dialogue advance (Charon's text-advance lease) until the tutorial step moves on.
///
/// Tutorial menus ("Clear the ridge to build a cropland?" → Clear the ridge! / Not right now., seen at step 8 on
/// 2026-10-17) are answered with the FIRST option — FFXIV lists the decline last. At most <see cref="MaxMenuChoices"/>
/// per talk, and the talk still only succeeds when the step advances, so a wrong pick surfaces as a failure.
/// Fails when the talk ends and the step hasn't moved.
/// </summary>
public sealed class TalkRun
{
    private const float TalkRange = 4f;
    private const int MaxMenuChoices = 3;

    private readonly IslandActions _act;
    private readonly IslandTravel _nav;

    private List<IslandNpc> _candidates = new();
    private int _startProgress;
    private IGameObject? _target;
    private bool _talking;
    private bool _sawEvent;
    private DateTime _phaseStart;
    private DateTime _eventEnded;
    private DateTime _lastMove;
    private int _interactTries;
    private int _menuChoices;
    private DateTime _lastMenuChoice;
    private string _lastChoice = string.Empty;

    public TalkRun(IslandActions act, IslandTravel nav)
    {
        _act = act;
        _nav = nav;
    }

    public string Who => _candidates.FirstOrDefault()?.Name ?? "?";

    public void Begin(IReadOnlyList<IslandNpc> npcs, int progress, DateTime now)
    {
        // Several instances share a name (three furballs); try the closest first.
        _candidates = npcs.OrderBy(n => _act.DistanceTo(n.Position)).ToList();
        _startProgress = progress;
        _target = null;
        _talking = _sawEvent = false;
        _interactTries = 0;
        _menuChoices = 0;
        _lastMenuChoice = DateTime.MinValue;
        _lastChoice = string.Empty;
        _phaseStart = now;
        _lastMove = DateTime.MinValue;
    }

    public StepResult Step(DateTime now, int progress)
    {
        _nav.Update(now);
        if (progress != _startProgress)
        {
            _act.ReleaseDialogue();
            return StepResult.Succeeded($"talked to the {Who}{_lastChoice}");
        }

        if (_act.WaitingMenu() is { } menu)
        {
            // [REPLACED: every tutorial menu stopped the run; the first option is the tutorial's "yes"]
            // return StepResult.Failed($"the {Who} is waiting for a dialogue choice ({menu}) — pick it once by hand with the probe recording");
            if (now - _lastMenuChoice < TimeSpan.FromSeconds(1))
                return StepResult.Running($"answering the {Who}");
            if (_menuChoices >= MaxMenuChoices)
                return StepResult.Failed($"the {Who} kept asking ({menu}) after {MaxMenuChoices} answers — pick by hand and tell Calypso which");

            _act.FireCallback(menu, true, 0); // SelectString / SelectIconString: entry 0; SelectYesno: 0 = Yes
            _menuChoices++;
            _lastMenuChoice = now;
            _lastChoice = $" (took the first option in {menu})";
            _eventEnded = DateTime.MaxValue; // the menu is part of the talk
            return StepResult.Running($"answering the {Who}: first option in {menu}");
        }

        return _talking ? Talking(now) : Approach(now);
    }

    public void Abort()
    {
        _nav.Stop();
        _act.ReleaseDialogue();
    }

    private StepResult Approach(DateTime now)
    {
        if (_nav.Provider.Length == 0)
            return StepResult.Failed(_nav.Unavailable);

        _target = _candidates.Select(n => _act.Nearest(n.BaseId)).FirstOrDefault(o => o != null);
        if (_target != null)
        {
            if (_act.DistanceTo(_target.Position) <= TalkRange && !_nav.IsMoving)
            {
                _act.KeepDialogueAdvancing();
                _act.Interact(_target);
                _interactTries++;
                _talking = true;
                _sawEvent = false;
                _phaseStart = now;
                return StepResult.Running($"talking to the {Who}");
            }

            MoveTo(_target.Position, now);
            return StepResult.Running($"walking to the {Who}");
        }

        // Not loaded yet: walk toward where the layout puts it; drop instances that turn out not to be spawned.
        if (_candidates.Count == 0)
            return StepResult.Failed("could not find the NPC this step names");

        var spot = _candidates[0].Position;
        if (_act.DistanceTo(spot) < 8f && now - _phaseStart > TimeSpan.FromSeconds(3))
        {
            _candidates.RemoveAt(0);
            _phaseStart = now;
            return _candidates.Count == 0
                ? StepResult.Failed($"the {Who} is not where the layout puts it")
                : StepResult.Running("trying another spot");
        }

        MoveTo(spot, now);
        return StepResult.Running($"walking to the {Who}");
    }

    private StepResult Talking(DateTime now)
    {
        var advancing = _act.KeepDialogueAdvancing();

        if (_act.InEvent)
        {
            _sawEvent = true;
            _eventEnded = DateTime.MaxValue;
            return StepResult.Running(advancing
                ? $"talking to the {Who}"
                : $"talking to the {Who} — click through the dialogue (Charon isn't loaded to advance it)");
        }

        if (!_sawEvent)
        {
            if (now - _phaseStart < TimeSpan.FromSeconds(5))
                return StepResult.Running($"talking to the {Who}");
            if (_interactTries < 2)
            {
                _talking = false; // walk closer and try once more
                return StepResult.Running($"the {Who} didn't answer, trying again");
            }

            return StepResult.Failed($"the {Who} didn't start a conversation");
        }

        if (_eventEnded == DateTime.MaxValue)
            _eventEnded = now;

        // The step can take a moment to move after the last line; give it 3 s before calling it a miss.
        return now - _eventEnded < TimeSpan.FromSeconds(3)
            ? StepResult.Running($"talking to the {Who}")
            : StepResult.Failed($"talked to the {Who} but the step didn't advance");
    }

    private void MoveTo(System.Numerics.Vector3 dest, DateTime now)
    {
        if (_nav.IsMoving || now - _lastMove < TimeSpan.FromSeconds(3))
            return;
        _lastMove = now;
        _nav.MoveCloseTo(dest, TalkRange - 1f);
    }
}
