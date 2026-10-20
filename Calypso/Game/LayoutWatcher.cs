using System;
using System.Linq;
using System.Threading.Tasks;
using Calypso.Core;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Calypso.Game;

/// <summary>
/// Keeps Ariadne's island navmesh in step with the island as it's built. When the layout fingerprint differs from the
/// one this character's mesh was last captured with — and has been steady for a few seconds with the character idle
/// (no cutscene, no menus) — it calls <c>Ariadne.CaptureZone</c> (Func&lt;Task&lt;bool&gt;&gt;: capture the live scene and
/// rebuild this zone even when a mesh is cached). Nothing happens without Ariadne.
///
/// Until Ariadne keys island meshes by layout, all islands share one cached mesh, so the last capture serves every
/// character (claude-memory.md "Navmesh vs island layout").
/// </summary>
public sealed class LayoutWatcher
{
    private const string Capture = "Ariadne.CaptureZone";
    private static readonly TimeSpan Steady = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private readonly IDalamudPluginInterface _pi;
    private readonly IslandActions _act;
    private readonly IPluginLog _log;
    private readonly Action _save;

    private string? _seen;
    private DateTime _steadySince;
    private DateTime _nextAttempt;
    private Task<bool>? _pending;
    private string _pendingLayout = string.Empty;

    public LayoutWatcher(IDalamudPluginInterface pi, IslandActions act, IPluginLog log, Action save)
    {
        _pi = pi;
        _act = act;
        _log = log;
        _save = save;
    }

    /// <summary>The last capture's outcome, for the Debug tab.</summary>
    public string Status { get; private set; } = "idle";

    public void Tick(IslandSnapshot island, CharacterProfile profile, DateTime now)
    {
        if (_pending is { IsCompleted: true } done)
        {
            var ok = done.Status == TaskStatus.RanToCompletion && done.Result;
            if (ok)
            {
                _log.Information("Ariadne recaptured the island ({0} → {1})", Empty(profile.MeshLayout), _pendingLayout);
                profile.MeshLayout = _pendingLayout;
                _save();
            }

            Status = ok ? $"captured {_pendingLayout}" : $"Ariadne refused the capture ({done.Exception?.GetBaseException().Message ?? "zone not ready"}); retrying";
            _pending = null;
        }

        if (!island.InSanctuary || !island.Available)
        {
            _seen = null;
            return;
        }

        var layout = IslandLayoutFingerprint.Of(island);
        if (layout != _seen)
        {
            // Raw bytes beside the decoded layout, so the MJIWorkshops reading can be checked against a known island.
            _log.Information("Island layout {0}; IslandState{1}", layout, IslandStateReader.RawBuildingBytes());
            _seen = layout;
            _steadySince = now;
        }

        if (layout == profile.MeshLayout || _pending != null || now < _nextAttempt || now - _steadySince < Steady || _act.Busy || _act.WaitingMenu() != null)
            return;
        if (!_pi.InstalledPlugins.Any(p => p.InternalName == "Ariadne" && p.IsLoaded))
        {
            Status = "Ariadne isn't loaded — the island mesh may not match the island";
            return;
        }

        _nextAttempt = now + RetryAfter;
        try
        {
            _pending = _pi.GetIpcSubscriber<Task<bool>>(Capture).InvokeFunc();
            _pendingLayout = layout;
            Status = $"asking Ariadne to recapture ({layout})";
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "{0} unavailable", Capture);
            Status = "this Ariadne has no CaptureZone";
        }
    }

    private static string Empty(string s) => s.Length == 0 ? "first capture" : s;
}
