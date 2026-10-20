using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Progression;
using Calypso.Core.Tasks;
using Calypso.Game;
using Calypso.Gathering;

namespace Calypso.Progression;

/// <summary>
/// Daily-loop task: work through the island tutorial (MJIProgress) as far as Calypso can — gather a step's materials,
/// talk to the NPC it names — and stop at the first step that needs something not automated yet (crafting tools,
/// construction, Sow/Water/Capture/Feed), saying exactly what. Runs several steps per run.
/// </summary>
public sealed class ProgressionTask : IIslandTask
{
    private readonly IslandLayout _layout;
    private readonly IslandData _data;
    private readonly IslandActions _act;
    private readonly GatherRun _gather;
    private readonly TalkRun _talk;
    private readonly CraftRun _craft;
    private readonly ModeRun _mode;
    private readonly ConstructRun _construct;
    private readonly Core.Pasture.AnimalBook _animals;
    private readonly List<System.Numerics.Vector3> _searched = new();
    private readonly GatherTools _tools;
    private readonly Func<uint, int> _itemCount;
    private readonly Func<int, uint> _pouchItem;

    private ProgressAction? _current;
    private int _advanced;

    public ProgressionTask(IslandLayout layout, IslandData data, IslandActions act, GatherRun gather, TalkRun talk, CraftRun craft,
        ModeRun mode, GatherTools tools, Func<uint, int> itemCount, Func<int, uint> pouchItem, ConstructRun construct,
        Core.Pasture.AnimalBook animals)
    {
        _animals = animals;
        _construct = construct;
        _mode = mode;
        _tools = tools;
        _craft = craft;
        _layout = layout;
        _data = data;
        _act = act;
        _gather = gather;
        _talk = talk;
        _itemCount = itemCount;
        _pouchItem = pouchItem;
    }

    public string Id => "progress.island";
    public string Name => "Island tutorial";
    public TimeSpan Timeout => TimeSpan.FromMinutes(20);

    /// <summary>IslandState.CurrentProgress is the step being worked on (MJIFunction gates line up with it). 0 = before step 1 — UNVERIFIED.</summary>
    public static int StepRow(IslandSnapshot island) => Math.Max(1, (int)island.Progress);

    // MJIHudMode rows: 0 Free, 1 Gather, 2 Sow, 3 Water, 4 Cull, 5 Feed, 6 Pet, 7 Beckon, 8 Capture.
    private const uint SowMode = 2;
    private const uint WaterMode = 3;
    private const uint CaptureMode = 8;
    private const uint FeedMode = 5;

    public ProgressAction Decide(IslandSnapshot island) => ProgressRules.Decide(
        _layout.Step(StepRow(island)),
        row => _itemCount(_pouchItem(row)),
        _layout.Npcs,
        _data.HudModeNames.Where(m => m.Key > WaterMode && m.Key != CaptureMode && m.Key != FeedMode).Select(m => m.Value).ToList(),
        _layout.Craftables(),
        _layout.Buildings(island),
        new HudModeNames(_data.HudModeNames.GetValueOrDefault(SowMode, "Sow"), _data.HudModeNames.GetValueOrDefault(WaterMode, "Water"),
            _data.HudModeNames.GetValueOrDefault(CaptureMode, "Capture"), _data.HudModeNames.GetValueOrDefault(FeedMode, "Feed")),
        Seed(island),
        HeldRestraints().Any(),
        HeldFeeds().Any(),
        RestraintRecipe());

    /// <summary>The smallest restraint the craft log can make (the Makeshift Net for a new island).</summary>
    private Craftable? RestraintRecipe()
    {
        var r = _layout.Recipes.Where(x => x.Tab == 2 && _layout.IsRecipeUnlocked(x.Row)).OrderBy(x => x.Order).FirstOrDefault();
        return r == null ? null : new Craftable(r.Name, false, r.Row, r.Materials, r.Automatable);
    }

    private List<uint> HeldFeeds() => _layout.Feeds.Where(f => _itemCount(f.ItemId) > 0).Select(f => f.ItemId).ToList();

    private List<(int PouchRow, uint ItemId, uint IconId, int Size)> HeldRestraints() =>
        _layout.Restraints.Where(r => _itemCount(r.ItemId) > 0).ToList();

    /// <summary>A wild island animal one of the held restraints can catch; commons first so a rare isn't spent on the tutorial.</summary>
    private bool IsCatchable(Dalamud.Game.ClientState.Objects.Types.IGameObject o)
    {
        // Never one of the character's own pasture animals — they're the same BattleNpcs as wild ones (probe 2026-10-17).
        if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc || _act.IsPastureAnimal(o))
            return false;
        var sizes = HeldRestraints().Select(r => r.Size).ToHashSet();
        return _layout.Animals.Any(a => a.BNpcBase == o.BaseId && a.Rarity == 1 && sizes.Contains(a.Size));
    }

    /// <summary>
    /// Where to look for a catchable animal next: spots where one of the species the held restraints can catch has been
    /// seen (nearest first), then the island's gathering spots — each place once per run.
    /// </summary>
    private System.Numerics.Vector3? NextAnimalSpot()
    {
        var sizes = HeldRestraints().Select(r => r.Size).ToHashSet();
        var species = _layout.Animals.Where(a => a.Rarity == 1 && sizes.Contains(a.Size)).Select(a => a.BNpcBase);
        var next = _animals.SpotsOf(species).Concat(_layout.GatherSpots)
            .Where(p => !_searched.Any(s => System.Numerics.Vector3.Distance(s, p) < 40f))
            .OrderBy(p => _animals.SpotsOf(species).Contains(p) ? 0 : 1)
            .ThenBy(p => _act.DistanceTo(p))
            .Cast<System.Numerics.Vector3?>()
            .FirstOrDefault();
        if (next is { } n)
            _searched.Add(n);
        return next;
    }

    /// <summary>A seed on hand, else one that can be gathered right now (tool held), else none.</summary>
    private (int PouchRow, bool Have)? Seed(IslandSnapshot island)
    {
        var held = _layout.Seeds.FirstOrDefault(s => _itemCount(s.ItemId) > 0);
        if (held.ItemId != 0)
            return (held.PouchRow, true);

        var gatherable = _tools.GatherableNow(island.Progress).Select(g => g.ItemId).ToHashSet();
        var next = _layout.Seeds.FirstOrDefault(s => gatherable.Contains(s.ItemId));
        return next.ItemId != 0 ? (next.PouchRow, false) : null;
    }

    private bool IsEmptyPlot(Dalamud.Game.ClientState.Objects.Types.IGameObject o) =>
        o.BaseId == IslandLayout.PlotBaseId && string.Equals(o.Name.TextValue, _layout.EmptyPlotName, StringComparison.OrdinalIgnoreCase);

    private bool IsSownPlot(Dalamud.Game.ClientState.Objects.Types.IGameObject o) =>
        o.BaseId == IslandLayout.PlotBaseId && !string.Equals(o.Name.TextValue, _layout.EmptyPlotName, StringComparison.OrdinalIgnoreCase);

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        if (!UnlockTask.Unlocked)
            return TaskCheck.Blocked("unlock the island first (Seeking Sanctuary)");
        if (!island.Available)
            return TaskCheck.Blocked("no island data");

        var action = Decide(island);
        if (action.Kind == ProgressActionKind.Finished)
            return TaskCheck.Done(action.Reason);
        if (action.Kind == ProgressActionKind.Blocked)
            return TaskCheck.Blocked($"step {StepRow(island)}: {action.Reason}");
        if (!island.InSanctuary)
            return TaskCheck.Blocked("not on the island");
        return TaskCheck.Ready($"step {StepRow(island)}: {action.Reason}");
    }

    public void Begin(TaskContext context)
    {
        _advanced = 0;
        Start(context);
    }

    public StepResult Step(TaskContext context)
    {
        var island = context.Island;
        var step = StepRow(island);

        StepResult sub;
        switch (_current?.Kind)
        {
            case ProgressActionKind.Gather:
                sub = _gather.Step(context.UtcNow);
                break;
            case ProgressActionKind.Talk:
                sub = _talk.Step(context.UtcNow, island.Progress);
                if (sub.Outcome == StepOutcome.Succeeded)
                    _advanced++;
                break;
            case ProgressActionKind.Craft:
                sub = _craft.Step(context.UtcNow);
                break;
            case ProgressActionKind.Sow:
            case ProgressActionKind.Water:
            case ProgressActionKind.Capture:
            case ProgressActionKind.Feed:
                sub = _mode.Step(context.UtcNow, island.Progress);
                if (sub.Outcome == StepOutcome.Succeeded)
                    _advanced++;
                break;
            case ProgressActionKind.Construct:
                sub = _construct.Step(island, context.UtcNow);
                if (sub.Outcome == StepOutcome.Succeeded)
                    _advanced++;
                break;
            default:
                return Finish(context);
        }

        if (sub.Outcome == StepOutcome.Running)
            return StepResult.Running($"step {step}: {sub.Detail}");
        if (sub.Outcome == StepOutcome.Failed)
            return StepResult.Failed($"step {step}: {sub.Detail}");

        // Sub-goal done — look at the (possibly new) step and keep going while there's something to do.
        Start(context);
        // [REPLACED: Construct added to the kinds that keep the run going]
        // return _current?.Kind is ProgressActionKind.Gather or ProgressActionKind.Talk or ProgressActionKind.Craft
        //     or ProgressActionKind.Sow or ProgressActionKind.Water or ProgressActionKind.Capture or ProgressActionKind.Feed
        //     ? StepResult.Running($"step {StepRow(island)}: {_current.Reason}")
        //     : Finish(context);
        return _current?.Kind is ProgressActionKind.Gather or ProgressActionKind.Talk or ProgressActionKind.Craft
            or ProgressActionKind.Sow or ProgressActionKind.Water or ProgressActionKind.Capture or ProgressActionKind.Feed
            or ProgressActionKind.Construct
            ? StepResult.Running($"step {StepRow(island)}: {_current.Reason}")
            : Finish(context);
    }

    public void Abort()
    {
        _gather.Abort();
        _talk.Abort();
        _craft.Abort();
        _mode.Abort();
        _construct.Abort();
    }

    private void Start(TaskContext context)
    {
        _current = Decide(context.Island);
        switch (_current.Kind)
        {
            case ProgressActionKind.Gather:
                var targets = _current.Missing.ToDictionary(m => m.PouchRow, m => _itemCount(_pouchItem(m.PouchRow)) + m.Missing);
                _gather.Begin(targets, context.UtcNow);
                break;
            case ProgressActionKind.Talk:
                _talk.Begin(_current.Npcs, context.Island.Progress, context.UtcNow);
                break;
            case ProgressActionKind.Craft:
                _craft.Begin(_current.Craft!.RecipeRow, _current.Craft.Name, context.UtcNow);
                break;
            case ProgressActionKind.Sow:
                var seeds = _layout.Seeds.Where(s => _itemCount(s.ItemId) > 0).Select(s => s.ItemId).ToList();
                _mode.Begin(SowMode, seeds, IsEmptyPlot, "sow", context.Island.Progress, context.UtcNow);
                break;
            case ProgressActionKind.Water:
                _mode.Begin(WaterMode, Array.Empty<uint>(), IsSownPlot, "water", context.Island.Progress, context.UtcNow);
                break;
            case ProgressActionKind.Capture:
                var restraints = HeldRestraints().Select(r => r.ItemId).ToList();
                // [REPLACED: only animals already loaded nearby]
                // _mode.Begin(CaptureMode, restraints, IsCatchable, "capture", context.Island.Progress, context.UtcNow);
                _searched.Clear();
                _mode.Begin(CaptureMode, restraints, IsCatchable, "capture", context.Island.Progress, context.UtcNow, NextAnimalSpot);
                break;
            case ProgressActionKind.Feed:
                _mode.Begin(FeedMode, HeldFeeds(), _act.IsPastureAnimal, "feed", context.Island.Progress, context.UtcNow);
                break;
            case ProgressActionKind.Construct:
                _construct.Begin(_current.Build!, context.Island, context.UtcNow);
                break;
        }
    }

    private StepResult Finish(TaskContext context)
    {
        var where = $"step {StepRow(context.Island)}";
        return _current?.Kind == ProgressActionKind.Finished
            ? StepResult.Succeeded("the island tutorial is complete")
            : StepResult.Succeeded($"advanced {_advanced} step(s); stopped at {where}: {_current?.Reason}");
    }
}
