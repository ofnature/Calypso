using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Calypso.Core.Tasks;

namespace Calypso.Core.Status;

/// <summary>
/// The status payload behind IPC <c>Calypso.GetStatusJson</c> and the <c>calypso.status</c> relay channel.
/// Extend-only: add fields, never rename or repurpose one; bump <see cref="CurrentSchema"/> only for a breaking
/// change. Consumers must tolerate fields they don't know.
/// </summary>
public sealed class CalypsoStatus
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>"Name@World" — the same identity the Daedalus relay uses.</summary>
    public string Character { get; set; } = string.Empty;

    public bool Busy { get; set; }

    /// <summary>idle | running | waiting</summary>
    public string State { get; set; } = "idle";

    /// <summary>loop | single | "" when idle</summary>
    public string Mode { get; set; } = string.Empty;

    public string CurrentTaskId { get; set; } = string.Empty;
    public string CurrentTaskName { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string LastResult { get; set; } = string.Empty;

    public bool InSanctuary { get; set; }
    public int Rank { get; set; }
    public uint Xp { get; set; }

    /// <summary><see cref="IslandCycle.Key"/></summary>
    public string Cycle { get; set; } = string.Empty;

    public List<TaskStatus> Tasks { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static CalypsoStatus? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CalypsoStatus>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CalypsoStatus Build(
        TaskRunner runner,
        IslandSnapshot island,
        CycleLedger ledger,
        Func<string, bool> isEnabled,
        string character,
        string pluginVersion,
        IslandCycle cycle,
        DateTime utcNow)
    {
        return new CalypsoStatus
        {
            PluginVersion = pluginVersion,
            Character = character,
            Busy = runner.IsBusy,
            State = runner.State.ToString().ToLowerInvariant(),
            Mode = !runner.IsBusy ? string.Empty : runner.IsSingleRun ? "single" : "loop",
            CurrentTaskId = runner.Current?.Id ?? string.Empty,
            CurrentTaskName = runner.Current?.Name ?? string.Empty,
            Detail = runner.Detail,
            LastResult = runner.LastResult,
            InSanctuary = island.InSanctuary,
            Rank = island.Rank,
            Xp = island.Xp,
            Cycle = cycle.Key,
            Tasks = runner.Evaluate(island, ledger).Select(e => new TaskStatus
            {
                Id = e.Task.Id,
                Name = e.Task.Name,
                State = e.Check.State.ToString().ToLowerInvariant(),
                Reason = e.Check.Reason,
                Enabled = isEnabled(e.Task.Id),
                Failures = ledger.Failures(e.Task.Id),
            }).ToList(),
            UpdatedUtc = utcNow,
        };
    }
}

public sealed class TaskStatus
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>locked | done | ready | blocked</summary>
    public string State { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Failures { get; set; }
}
