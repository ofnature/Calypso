using System;
using Calypso.Core;
using Calypso.Core.Tasks;
using Calypso.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Calypso.Progression;

/// <summary>
/// Daily-loop task, FIRST in the loop (user request 2026-10-19): unlock Island Sanctuary by running "Seeking Sanctuary"
/// (Quest 70179, AktKua101 — prerequisite 70000, the end of the Endwalker MSQ) through Odysseus. Done once the quest is
/// complete. Without Odysseus' quest gates it says to do the quest by hand.
/// </summary>
public sealed class UnlockTask : IIslandTask
{
    /// <summary>Seeking Sanctuary (validated against the Quest sheet at load).</summary>
    public const uint SeekingSanctuary = 70179;

    private readonly OdysseusQuests _quests;
    private readonly string _name;
    private DateTime _start;
    private bool _started;

    // Check runs every frame (UI, status JSON): ask Odysseus at most every 5 s.
    private bool? _canRun;
    private DateTime _canRunAt = DateTime.MinValue;

    public UnlockTask(OdysseusQuests quests, IDataManager data, IPluginLog log)
    {
        _quests = quests;
        _name = data.GetExcelSheet<Quest>().GetRowOrDefault(SeekingSanctuary)?.Name.ExtractText() ?? string.Empty;
        if (_name.Length == 0)
        {
            _name = "Seeking Sanctuary";
            log.Warning("Quest {0} not in the Quest sheet — the island unlock check may be wrong", SeekingSanctuary);
        }
    }

    public string Id => "progress.unlock";
    public string Name => "Unlock the island";
    public TimeSpan Timeout => TimeSpan.FromMinutes(30);
    public string QuestName => _name;

    public static bool Unlocked => OdysseusQuests.IsComplete(SeekingSanctuary);

    public TaskCheck Check(IslandSnapshot island, CycleLedger ledger)
    {
        if (Unlocked)
            return TaskCheck.Done($"{_name} is complete");

        if (DateTime.UtcNow - _canRunAt > TimeSpan.FromSeconds(5))
        {
            _canRun = _quests.CanRun(SeekingSanctuary);
            _canRunAt = DateTime.UtcNow;
        }

        return _canRun switch
        {
            null => TaskCheck.Blocked($"do \"{_name}\" by hand — Odysseus (with its quest gates) isn't loaded"),
            false => TaskCheck.Blocked($"Odysseus can't run \"{_name}\" yet (the Endwalker story must be finished first)"),
            true => TaskCheck.Ready($"run \"{_name}\" through Odysseus"),
        };
    }

    public void Begin(TaskContext context)
    {
        _start = context.UtcNow;
        _started = false;
    }

    public StepResult Step(TaskContext context)
    {
        if (Unlocked)
            return StepResult.Succeeded($"{_name} complete — the island is unlocked");

        if (!_started)
        {
            if (_quests.Start(SeekingSanctuary) != true)
                return StepResult.Failed($"Odysseus refused \"{_name}\": {_quests.Status()?.Reason ?? "see the Odysseus log"}");
            _started = true;
            return StepResult.Running($"Odysseus is running \"{_name}\"");
        }

        var status = _quests.Status();
        switch (status?.State)
        {
            case "Done":
                return StepResult.Succeeded($"{_name} complete — the island is unlocked");
            case "Faulted" or "Stopped" or "Refused":
                return StepResult.Failed($"\"{_name}\" {status.State.ToLowerInvariant()}: {status.Reason ?? status.Status}");
        }

        if (_quests.IsRunning() == false && context.UtcNow - _start > TimeSpan.FromSeconds(10))
            return StepResult.Failed($"Odysseus stopped running \"{_name}\"");

        return StepResult.Running(string.IsNullOrEmpty(status?.Status) ? $"Odysseus is running \"{_name}\"" : status.Status);
    }

    public void Abort()
    {
        if (_started)
            _quests.Stop();
    }
}
