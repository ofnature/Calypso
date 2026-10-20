using System;
using System.Collections.Generic;

namespace Calypso.Core.Tasks;

/// <summary>
/// Per-character record of the CURRENT island day, persisted in the character's profile. It holds only what
/// the game itself does not show — "exports were done this cycle", how often a task failed — because
/// everything observable is re-derived from the live <see cref="IslandSnapshot"/> on every evaluation
/// (the Theseus resume model: resuming is just running the loop again).
///
/// Rolls over automatically: the first access with a different <see cref="IslandCycle"/> wipes the entries.
/// </summary>
public sealed class CycleLedger
{
    /// <summary><see cref="IslandCycle.Key"/> these entries belong to; empty = never used.</summary>
    public string CycleKey { get; set; } = string.Empty;

    public Dictionary<string, LedgerEntry> Entries { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Switch to <paramref name="cycle"/>; returns true when that wiped yesterday's entries.</summary>
    public bool Roll(IslandCycle cycle)
    {
        if (CycleKey == cycle.Key)
            return false;

        CycleKey = cycle.Key;
        Entries.Clear();
        return true;
    }

    public bool IsCompleted(string taskId) => Entries.TryGetValue(taskId, out var e) && e.Completed;

    public int Failures(string taskId) => Entries.TryGetValue(taskId, out var e) ? e.Failures : 0;

    public LedgerEntry? Get(string taskId) => Entries.GetValueOrDefault(taskId);

    public void MarkCompleted(string taskId, DateTime utc)
    {
        var e = GetOrAdd(taskId);
        e.Completed = true;
        e.UpdatedUtc = utc;
    }

    public void RecordFailure(string taskId, string reason, DateTime utc)
    {
        var e = GetOrAdd(taskId);
        e.Failures++;
        e.LastError = reason;
        e.UpdatedUtc = utc;
    }

    private LedgerEntry GetOrAdd(string taskId)
    {
        if (!Entries.TryGetValue(taskId, out var e))
        {
            e = new LedgerEntry();
            Entries[taskId] = e;
        }

        return e;
    }
}

public sealed class LedgerEntry
{
    public bool Completed { get; set; }
    public int Failures { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
