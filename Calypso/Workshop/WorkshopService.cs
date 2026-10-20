using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Calypso.Core;
using Calypso.Core.Workshop;
using Calypso.Game;
using Dalamud.Plugin.Services;

namespace Calypso.Workshop;

/// <summary>
/// Glue between the pure solver and the game: builds a <see cref="PlannerInput"/> from the live island and the
/// character's week (on the framework thread), runs the search on a background thread, and hands the result back.
/// One plan at a time.
/// </summary>
public sealed class WorkshopService : IDisposable
{
    /// <summary>
    /// Rest days the game requires per week. ASSUMPTION from MJIManager.CraftworksRestDays having 4 entries (this week +
    /// next) — verify in game before trusting.
    /// </summary>
    public const int RestDaysPerWeek = 2;

    private readonly WorkshopData _data;
    private readonly WorkshopGame _game;
    private readonly IPluginLog _log;

    private Task<PlanResult>? _job;
    private CancellationTokenSource? _cancel;
    private (long Week, int Day) _jobFor;
    private Dictionary<int, int> _onHandAtBuild = new();

    public WorkshopService(WorkshopData data, WorkshopGame game, IPluginLog log)
    {
        _data = data;
        _game = game;
        _log = log;
    }

    public CraftCatalog Catalog => _data.Catalog;
    public WorkshopGame Game => _game;
    public bool IsPlanning => _job is { IsCompleted: false };
    public string LastError { get; private set; } = string.Empty;

    /// <summary>True when the character has at least one workshop — the gate for every workshop task.</summary>
    public static bool HasWorkshop(IslandSnapshot island) => island.Workshops.Any(w => w.IsBuilt);

    /// <summary>
    /// The planner's view of this character's week, planning from <paramref name="firstDay"/>.
    /// <paramref name="restMask"/> = the agenda's RestCycles when it was read, otherwise rest days come from MJIManager.
    /// </summary>
    public PlannerInput BuildInput(IslandSnapshot island, CharacterProfile profile, int firstDay, uint? restMask)
    {
        var week = profile.WorkshopWeek;
        var settings = profile.Workshop;
        var catalog = _data.Catalog;

        var materials = _game.PouchCounts(catalog);
        _onHandAtBuild = new Dictionary<int, int>(materials);
        if (settings.Objective == WorkshopObjective.RankProgress)
            materials = MaterialReservation.Available(materials, _data.UnlockedConstructions(island));

        var produced = week.ProducedBefore(firstDay, catalog.MaxCraftId);
        var favors = week.Week == settings.SkipFavorsWeek
            ? Array.Empty<FavorRequirement>()
            // Earlier days' items are already in the game's delivered count; only today's (the day before firstDay)
            // are made-but-not-yet-delivered. ASSUMPTION: favors count items as each day's crafts complete.
            : (System.Collections.Generic.IReadOnlyList<FavorRequirement>)_game.Favors(_data.FavorDefaultRequired, craft =>
                week.Produced.TryGetValue(firstDay - 1, out var today) && craft < today.Length ? today[craft] : 0);

        var restsTaken = restMask is { } mask
            ? Enumerable.Range(0, firstDay).Count(d => (mask & (1u << d)) != 0)
            : island.RestDays.Where(d => d < firstDay).Distinct().Count();

        return new PlannerInput
        {
            Catalog = catalog,
            Forecast = new SupplyForecast(catalog, week.ObservedByDay(), week.ProducedByDay()),
            Rank = island.Rank,
            WorkshopTiers = island.Workshops.Select(w => w.IsBuilt ? (int)w.Level : 0).ToArray(),
            PopularityRow = island.Popularity,
            MaxGroove = catalog.Rules.MaxGroove(island.LandmarkIds.Count(id => id != 0)),
            StartGroove = week.LastGroove,
            FirstDay = firstDay,
            RestDaysPerWeek = RestDaysPerWeek,
            RestsTaken = restsTaken,
            ProducedBefore = produced,
            Materials = materials,
            GatherCostPerUnit = settings.GatherCostPerUnit,
            Favors = favors,
        };
    }

    /// <summary>Start a background plan. False when one is already running.</summary>
    public bool StartPlan(PlannerInput input, long week, int day)
    {
        if (IsPlanning)
            return false;

        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _jobFor = (week, day);
        LastError = string.Empty;
        _job = Task.Run(() => WeekPlanner.Plan(input, token), token);
        return true;
    }

    /// <summary>The finished plan, once (subsequent calls return null until the next StartPlan).</summary>
    public WeekPlan? TakeResult()
    {
        if (_job is not { IsCompleted: true } job)
            return null;

        _job = null;
        if (job.IsFaulted || job.IsCanceled)
        {
            LastError = job.Exception?.GetBaseException().Message ?? "planning cancelled";
            _log.Error(job.Exception?.GetBaseException(), "Workshop planning failed");
            return null;
        }

        var plan = WeekPlan.FromResult(job.Result, _jobFor.Week, DateTime.UtcNow, _jobFor.Day);
        plan.GatherTargets = plan.GatherDemand.ToDictionary(kv => kv.Key, kv => _onHandAtBuild.GetValueOrDefault(kv.Key) + kv.Value);
        return plan;
    }

    public void Cancel() => _cancel?.Cancel();

    public void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
    }
}
