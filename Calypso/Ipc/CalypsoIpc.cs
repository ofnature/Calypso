using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Calypso.Ipc;

/// <summary>
/// IPC surface for the rest of the suite (agreed contract, claude-memory.md "Phase 1 → 2 decisions").
///
/// | Gate                     | Shape               | Notes                                                        |
/// |--------------------------|---------------------|--------------------------------------------------------------|
/// | Calypso.GetVersion       | Func&lt;int&gt;          | IPC contract version (<see cref="ApiVersion"/>), not the plugin version |
/// | Calypso.IsBusy           | Func&lt;bool&gt;         | daily loop or a single task is running (incl. waiting)       |
/// | Calypso.GetStatusJson    | Func&lt;string&gt;       | <c>CalypsoStatus</c> JSON — extend-only, check "schema"      |
/// | Calypso.StartDailyLoop   | Func&lt;bool&gt;         | false = refused (busy / not logged in)                       |
/// | Calypso.RunTask          | Func&lt;string, bool&gt; | run one task by id; false = busy / unknown id / not logged in |
/// | Calypso.Stop             | Action              | stop whatever is running; no-op when idle                    |
///
/// Every gate is answered on the framework thread (Dalamud IPC is synchronous in the caller's thread; callers
/// in this suite call from Framework.Update).
/// </summary>
public sealed class CalypsoIpc : IDisposable
{
    /// <summary>Bump only for a breaking change to a gate's shape or meaning.</summary>
    public const int ApiVersion = 1;

    private readonly ICallGateProvider<int> _getVersion;
    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<string> _getStatusJson;
    private readonly ICallGateProvider<bool> _startDailyLoop;
    private readonly ICallGateProvider<string, bool> _runTask;
    private readonly ICallGateProvider<object?> _stop;

    public CalypsoIpc(
        IDalamudPluginInterface pluginInterface,
        Func<bool> isBusy,
        Func<string> statusJson,
        Func<bool> startDailyLoop,
        Func<string, bool> runTask,
        Action stop)
    {
        _getVersion = pluginInterface.GetIpcProvider<int>("Calypso.GetVersion");
        _isBusy = pluginInterface.GetIpcProvider<bool>("Calypso.IsBusy");
        _getStatusJson = pluginInterface.GetIpcProvider<string>("Calypso.GetStatusJson");
        _startDailyLoop = pluginInterface.GetIpcProvider<bool>("Calypso.StartDailyLoop");
        _runTask = pluginInterface.GetIpcProvider<string, bool>("Calypso.RunTask");
        _stop = pluginInterface.GetIpcProvider<object?>("Calypso.Stop");

        _getVersion.RegisterFunc(() => ApiVersion);
        _isBusy.RegisterFunc(isBusy);
        _getStatusJson.RegisterFunc(statusJson);
        _startDailyLoop.RegisterFunc(startDailyLoop);
        _runTask.RegisterFunc(runTask);
        _stop.RegisterAction(stop);
    }

    public void Dispose()
    {
        _getVersion.UnregisterFunc();
        _isBusy.UnregisterFunc();
        _getStatusJson.UnregisterFunc();
        _startDailyLoop.UnregisterFunc();
        _runTask.UnregisterFunc();
        _stop.UnregisterAction();
    }
}
