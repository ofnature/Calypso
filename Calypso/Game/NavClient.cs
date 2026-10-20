using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Calypso.Game;

/// <summary>
/// Movement through the agreed fallback chain: Ariadne first, then vnavmesh (both expose the same
/// <c>SimpleMove.PathfindAndMoveCloseTo</c> / <c>Path.*</c> shapes — Ariadne/docs/consumer-ipc.md), then nothing
/// (callers report "no navigation" instead of moving). Calypso-native recorded routes are the third link and arrive
/// with gathering routes.
///
/// Island notes (claude-memory.md "Navigation"): no mesh for 1055 exists until Ariadne or vnavmesh has been on the
/// island once, and Ariadne never auto-flies there (its CanFlyHere is false on the island), so Calypso passes
/// <c>fly</c> explicitly — false until island flight is known to be unlocked.
/// </summary>
public sealed class NavClient
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;

    public NavClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pi = pluginInterface;
        _log = log;
    }

    /// <summary>"Ariadne", "vnavmesh", or "" when neither is loaded and ready.</summary>
    public string Provider
    {
        get
        {
            if (Loaded("Ariadne") && Try(() => _pi.GetIpcSubscriber<bool>("Ariadne.IsConnected").InvokeFunc())
                                  && Try(() => _pi.GetIpcSubscriber<int>("Ariadne.ZoneStatus").InvokeFunc() is 2 or 3))
                return "Ariadne";
            if (Loaded("vnavmesh") && Try(() => _pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc()))
                return "vnavmesh";
            return string.Empty;
        }
    }

    /// <summary>Why there is no provider, for status lines.</summary>
    public string Unavailable =>
        Loaded("Ariadne") || Loaded("vnavmesh")
            ? "navmesh not ready for this zone yet (first visit builds it)"
            : "no navigation plugin (Ariadne or vnavmesh) loaded";

    public bool IsMoving
    {
        get
        {
            var p = Provider;
            if (p.Length == 0)
                return false;
            return Try(() => _pi.GetIpcSubscriber<bool>($"{p}.Path.IsRunning").InvokeFunc())
                   || Try(() => _pi.GetIpcSubscriber<bool>($"{p}.SimpleMove.PathfindInProgress").InvokeFunc());
        }
    }

    /// <summary>Pathfind and walk (or fly) to within <paramref name="range"/> of <paramref name="destination"/>. False = refused / no provider.</summary>
    public bool MoveCloseTo(Vector3 destination, float range, bool fly = false)
    {
        var p = Provider;
        if (p.Length == 0)
            return false;

        return Try(() => _pi.GetIpcSubscriber<Vector3, bool, float, bool>($"{p}.SimpleMove.PathfindAndMoveCloseTo").InvokeFunc(destination, fly, range));
    }

    public void Stop()
    {
        foreach (var p in new[] { "Ariadne", "vnavmesh" })
        {
            if (!Loaded(p))
                continue;
            try
            {
                _pi.GetIpcSubscriber<object>($"{p}.Path.Stop").InvokeAction();
            }
            catch
            {
                // fail-open
            }
        }
    }

    private bool Loaded(string internalName) =>
        _pi.InstalledPlugins.Any(p => p.IsLoaded && (p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase)
                                                    || p.Name.Equals(internalName, StringComparison.OrdinalIgnoreCase)));

    private bool Try(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "Nav IPC call failed");
            return false;
        }
    }
}
