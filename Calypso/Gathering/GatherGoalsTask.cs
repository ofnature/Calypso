using System;
using System.Collections.Generic;
using System.Linq;
using Calypso.Core;
using Calypso.Core.Tasks;

namespace Calypso.Gathering;

/// <summary>Per-character gathering settings.</summary>
public sealed class GatherSettings
{
    /// <summary>Pouch row → "keep at least this many". Gathered up to, never beyond.</summary>
    public Dictionary<int, int> Goals { get; set; } = new();

    /// <summary>Also gather what the workshop plan says it is short of ("fill whatever the workshop plan needs").</summary>
    public bool ForWorkshopPlan { get; set; } = true;

    /// <summary>Island-XP grind: gather everything gatherable right now up to <see cref="FillTo"/> (runs last in the loop).</summary>
    public bool FillAll { get; set; }

    /// <summary>Fill each item to this many (capped at its stack size).</summary>
    public int FillTo { get; set; } = 999;
}

/// <summary>
/// Daily-loop task, last in the loop: the island-XP grind (user request 2026-10-17). Gathers every item the character can
/// gather right now — tool held, caves open — up to <see cref="GatherSettings.FillTo"/>. Items no node gives are dropped
/// after a full sweep instead of ending the run. Long by nature; stop it any time and it picks up where it is.
///
/// Overnight loop (user goal 2026-10-17: "gather overnight and sell once max and then go back to gathering"): when
/// everything is full — or at least half of it — it walks to the Enterprising Exporter, sells all at the cap down to
/// <see cref="Exports.MarketSettings.Keep"/>, and goes back to gathering. When the exporter can't take anything (cowrie
/// cap reached) it waits 30 minutes before trying again, and ends once there's nothing left to gather either.
/// </summary>
public sealed class GatherFillTask : IIslandTask
{
    private readonly GatherRun _run;
    private readonly Game.GatherTools _tools;
    private readonly Core.Workshop.CraftCatalog _catalog;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<uint, int> _itemCount;
    private readonly Exports.ExportRun _export;
    private readonly Func<int, int> _reserved;
    private readonly Func<uint, int> _stackSize;
    private bool _exporting;
    private DateTime _noExportUntil = DateTime.MinValue;
    private Dictionary<int, int> _targets = new();

    public GatherFillTask(GatherRun run, Game.GatherTools tools, Core.Workshop.CraftCatalog catalog, Func<CharacterProfile?> profile, Func<uint, int> itemCount,
        Exports.ExportRun export, Func<int, int> reserved, Func<uint, int> stackSize)
    {
        _export = export;
        _reserved = reserved;
        _stackSize = stackSize;
        _run = run;
        _tools = tools;
        _catalog = catalog;
        _profile = profile;
        _itemCount = itemCount;
    }

    public string Id => "gather.fill";
    public string Name => "Gather everything (island XP)";

    // [REPLACED: 12 h ended an overnight run as a failure; the grind runs until stopped or there's nothing left]
    // public TimeSpan Timeout => TimeSpan.FromHours(12);
    public TimeSpan Timeout => TimeSpan.FromHours(23);

    /// <summary>Pouch row → fill target for everything gatherable now.</summary>
    public Dictionary<int, int> Targets(CharacterProfile profile, IslandSnapshot island)
    {
        var byItem = _catalog.Pouch.Values.GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => g.First().Row);
        return _tools.GatherableNow(island.Progress)
            .Where(g => byItem.ContainsKey(g.ItemId))
            .ToDictionary(g => byItem[g.ItemId], g => Math.Min(profile.Gathering.FillTo, g.StackSize));
    }

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        var profile = _profile();
        if (profile == null || !profile.Gathering.FillAll)
            return TaskCheck.Done("island-XP grind is off");

        var below = Targets(profile, island).Count(t => _catalog.Pouch.TryGetValue(t.Key, out var p) && _itemCount(p.ItemId) < t.Value);
        if (below == 0)
            return TaskCheck.Done("everything gatherable is full");
        if (!island.InSanctuary)
            return TaskCheck.Blocked("not on the island");
        return TaskCheck.Ready($"{below} item(s) below {profile.Gathering.FillTo}");
    }

    public void Begin(TaskContext context)
    {
        _exporting = false;
        StartGathering(context);
    }

    public StepResult Step(TaskContext context)
    {
        var now = context.UtcNow;
        var profile = _profile();
        if (profile == null)
            return StepResult.Failed("logged out");

        if (_exporting)
        {
            var sale = _export.Step(now);
            if (sale.Outcome == StepOutcome.Running)
                return StepResult.Running($"selling: {sale.Detail}");

            _exporting = false;
            if (sale.Outcome == StepOutcome.Failed)
                return StepResult.Failed($"selling: {sale.Detail}");
            if (sale.Detail.StartsWith("nothing", StringComparison.Ordinal))
                _noExportUntil = now.AddMinutes(30); // cowries capped or nothing at the cap: don't walk back for a while

            StartGathering(context);
            return StepResult.Running($"sold ({sale.Detail}); back to gathering");
        }

        var full = _targets.Count(t => _catalog.Pouch.TryGetValue(t.Key, out var p) && _itemCount(p.ItemId) >= _stackSize(p.ItemId));
        var gather = _run.Step(now);
        var nothingLeft = gather.Outcome == StepOutcome.Succeeded;

        if (full > 0 && now >= _noExportUntil && (nothingLeft || full * 2 >= _targets.Count))
        {
            _run.Abort();
            _export.Begin(profile.Market.Keep, _reserved, now);
            _exporting = true;
            return StepResult.Running($"{full} item(s) full — off to the exporter");
        }

        if (nothingLeft)
            return StepResult.Succeeded(full > 0 ? "everything is full and the exporter can't take more (cowrie cap)" : gather.Detail);

        return gather;
    }

    public void Abort()
    {
        _run.Abort();
        _export.Abort();
    }

    private void StartGathering(TaskContext context)
    {
        var profile = _profile();
        _targets = profile == null ? new Dictionary<int, int>() : Targets(profile, context.Island);
        _run.Begin(_targets, context.UtcNow);
    }
}

/// <summary>Daily-loop task: sell everything at its Isleventory cap down to the keep amount (user rule 2026-10-17).</summary>
public sealed class ExportTask : IIslandTask
{
    private readonly Exports.ExportRun _run;
    private readonly Core.Workshop.CraftCatalog _catalog;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<uint, int> _itemCount;
    private readonly Func<uint, int> _stackSize;
    private readonly Func<int, int> _reserved;

    public ExportTask(Exports.ExportRun run, Core.Workshop.CraftCatalog catalog, Func<CharacterProfile?> profile, Func<uint, int> itemCount,
        Func<uint, int> stackSize, Func<int, int> reserved)
    {
        _run = run;
        _catalog = catalog;
        _profile = profile;
        _itemCount = itemCount;
        _stackSize = stackSize;
        _reserved = reserved;
    }

    public string Id => "exports.sell";
    public string Name => "Sell full materials";
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public int FullCount() => _catalog.Pouch.Values.Count(p => p.ItemId != 0 && _itemCount(p.ItemId) >= _stackSize(p.ItemId));

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        if (_profile() == null)
            return TaskCheck.Done("not logged in");
        if (FullCount() == 0)
            return TaskCheck.Done("nothing at its cap");
        if (ledger.IsCompleted(Id))
            return TaskCheck.Done("sold today");
        if (!island.InSanctuary)
            return TaskCheck.Blocked("not on the island");
        return TaskCheck.Ready($"{FullCount()} item(s) at the cap");
    }

    public void Begin(TaskContext context) => _run.Begin(_profile()?.Market.Keep ?? 100, _reserved, context.UtcNow);

    public StepResult Step(TaskContext context) => _run.Step(context.UtcNow);

    public void Abort() => _run.Abort();
}

/// <summary>Daily-loop task: gather toward this character's goals and the workshop plan's shortfall.</summary>
public sealed class GatherGoalsTask : IIslandTask
{
    private readonly GatherRun _run;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Func<int, int> _pouchCount;

    public GatherGoalsTask(GatherRun run, Func<CharacterProfile?> profile, Func<int, int> pouchCount)
    {
        _run = run;
        _profile = profile;
        _pouchCount = pouchCount;
    }

    public string Id => "gather.goals";
    public string Name => "Gather goals";
    public TimeSpan Timeout => TimeSpan.FromMinutes(30);

    /// <summary>Absolute targets: the larger of the character's goal and the workshop plan's need for each material.</summary>
    public Dictionary<int, int> Targets(CharacterProfile profile)
    {
        var targets = new Dictionary<int, int>(profile.Gathering.Goals);
        if (profile.Gathering.ForWorkshopPlan && profile.WorkshopWeek.Plan is { } plan)
        {
            foreach (var (row, total) in plan.GatherTargets)
                targets[row] = Math.Max(targets.GetValueOrDefault(row), total);
        }

        return targets;
    }

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        var profile = _profile();
        if (profile == null)
            return TaskCheck.Done("not logged in");

        var below = Targets(profile).Where(t => _pouchCount(t.Key) < t.Value).Select(t => t.Key).ToList();
        if (below.Count == 0)
            return TaskCheck.Done("all gathering goals met");
        var short_ = below.Count(row => _run.MissingTool(row) == null);
        if (short_ == 0)
            return TaskCheck.Blocked($"the goals left need tools: {string.Join(", ", below.Select(_run.MissingTool).Distinct())}");
        if (!island.InSanctuary)
            return TaskCheck.Blocked("not on the island");
        return TaskCheck.Ready($"{short_} material(s) below goal");
    }

    public void Begin(TaskContext context)
    {
        var profile = _profile();
        _run.Begin(profile == null ? new Dictionary<int, int>() : Targets(profile), context.UtcNow);
    }

    public StepResult Step(TaskContext context) => _run.Step(context.UtcNow);

    public void Abort() => _run.Abort();
}
