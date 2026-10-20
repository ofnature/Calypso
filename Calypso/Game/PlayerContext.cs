using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Calypso.Game;

/// <summary>Who is logged in, and whether anything should hold the daily loop this frame.</summary>
public sealed unsafe class PlayerContext
{
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;

    public PlayerContext(IClientState clientState, IObjectTable objectTable, ICondition condition)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
    }

    public bool IsLoggedIn => _clientState.IsLoggedIn && _objectTable.LocalPlayer != null;

    public ulong ContentId
    {
        get
        {
            var ps = PlayerState.Instance();
            return ps == null ? 0 : ps->ContentId;
        }
    }

    /// <summary>"Name@World" — the identity the Daedalus relay and the fleet board use.</summary>
    public string Identity
    {
        get
        {
            var player = _objectTable.LocalPlayer;
            if (player == null)
                return string.Empty;

            var world = player.HomeWorld.IsValid ? player.HomeWorld.Value.Name.ExtractText() : "?";
            return $"{player.Name.TextValue}@{world}";
        }
    }

    /// <summary>Home world name — what Universalis prices are asked for.</summary>
    public string HomeWorld
    {
        get
        {
            var player = _objectTable.LocalPlayer;
            return player != null && player.HomeWorld.IsValid ? player.HomeWorld.Value.Name.ExtractText() : string.Empty;
        }
    }

    /// <summary>Why the loop must wait this frame, or null when it may act. Waiting never counts toward a task's timeout.</summary>
    public string? GlobalBlocker()
    {
        if (!IsLoggedIn)
            return "Not logged in";
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
            return "Zoning";
        if (_condition[ConditionFlag.OccupiedInCutSceneEvent] || _condition[ConditionFlag.WatchingCutscene] || _condition[ConditionFlag.WatchingCutscene78])
            return "In a cutscene";
        if (_condition[ConditionFlag.InCombat])
            return "In combat";
        if (_condition[ConditionFlag.Unconscious])
            return "Knocked out";
        return null;
    }
}
