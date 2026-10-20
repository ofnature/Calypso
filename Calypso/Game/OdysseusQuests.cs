using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Calypso.Game;

/// <summary>Odysseus' QuestStatusDto (camelCase, extend-only): state ∈ Idle | Refused | Running | Done | Faulted | Stopped.</summary>
public sealed record OdysseusQuestStatus(int Schema, string State, uint QuestId, int Sequence, int Step, string Status, string? Reason)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static OdysseusQuestStatus? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OdysseusQuestStatus>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Real quests through Odysseus (contract agreed with the Odysseus session 2026-10-17, claude-memory.md "Odysseus.Quest.*"):
/// CanRun(uint) → Start(uint) → poll IsRunning / GetStatusJson until Done, Faulted or Stopped. Every gate fails open: a
/// missing gate means Odysseus isn't loaded (or is older), and the caller tells the player to do the quest by hand.
/// </summary>
public sealed class OdysseusQuests
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;

    public OdysseusQuests(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi = pi;
        _log = log;
    }

    public static bool IsComplete(uint questId) => QuestManager.IsQuestComplete(questId);

    /// <summary>Null when the gates aren't there (Odysseus absent or too old).</summary>
    public bool? CanRun(uint questId) => Call(() => _pi.GetIpcSubscriber<uint, bool>("Odysseus.Quest.CanRun").InvokeFunc(questId));

    public bool? Start(uint questId) => Call(() => _pi.GetIpcSubscriber<uint, bool>("Odysseus.Quest.Start").InvokeFunc(questId));

    public bool? IsRunning() => Call(() => _pi.GetIpcSubscriber<bool>("Odysseus.Quest.IsRunning").InvokeFunc());

    public OdysseusQuestStatus? Status()
    {
        try
        {
            return OdysseusQuestStatus.TryParse(_pi.GetIpcSubscriber<string>("Odysseus.Quest.GetStatusJson").InvokeFunc());
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        try
        {
            _pi.GetIpcSubscriber<object>("Odysseus.Quest.Stop").InvokeAction();
        }
        catch
        {
            // Odysseus gone — nothing of ours is running there.
        }
    }

    private bool? Call(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "Odysseus quest gate unavailable");
            return null;
        }
    }
}
