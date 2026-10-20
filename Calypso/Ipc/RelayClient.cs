using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Calypso.Ipc;

/// <summary>
/// Cross-client messaging through the Daedalus LAN relay (<c>Daedalus.Relay.Publish</c> /
/// <c>Daedalus.Relay.Message</c>) — the same gates Charon uses. Calypso opens no socket of its own.
///
/// Relay semantics: the publisher never hears its own frame; Publish is a silent no-op while Daedalus is
/// absent or its LAN coordinator is off; frames arrive on the framework thread.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private readonly ICallGateSubscriber<string, string, object?> _publish;
    private readonly ICallGateSubscriber<string, string, object?> _message;
    private readonly IPluginLog _log;

    /// <summary>(channel, json) for every frame from another client.</summary>
    public event Action<string, string>? OnMessage;

    public RelayClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _publish = pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Publish");
        _message = pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Message");
        _message.Subscribe(OnRelayMessage);
    }

    public void Publish(string channel, string json)
    {
        try
        {
            _publish.InvokeAction(channel, json);
        }
        catch
        {
            // Daedalus not loaded — each box still runs on its own.
        }
    }

    private void OnRelayMessage(string channel, string json)
    {
        try
        {
            OnMessage?.Invoke(channel, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Relay handler failed (channel '{0}')", channel);
        }
    }

    public void Dispose()
    {
        _message.Unsubscribe(OnRelayMessage);
    }
}
