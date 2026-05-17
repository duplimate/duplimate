using System;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Services.Platform;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Cross-platform metered-network watchdog. Adaptive-cadence poll:
/// every 60s on unmetered, every 2s when metered is observed. Fires
/// <see cref="MeteredDetected"/> exactly once per transition into
/// metered state so the caller can cancel the running backup.
///
/// <para>
/// Windows: a real probe via <c>Windows.Networking.Connectivity</c>
/// — the only OS that exposes a first-class "is this network
/// metered?" API. macOS / Linux: no equivalent OS-level signal, so
/// the watchdog runs as a no-op (returns immediately when started)
/// and the user-facing toggle remains in the UI for parity but
/// has no effect. A future enhancement could add manual "treat
/// these SSIDs / interface names as metered" allow/deny lists.
/// </para>
///
/// Runs only for the duration of a RunAsync call; no global lifecycle.
/// </summary>
public sealed class MeteredNetworkWatchdog
{
    private static ILogger _log => AppLogger.For<MeteredNetworkWatchdog>();

    public int PollWhenMeteredSeconds { get; init; } = 2;
    public int PollWhenUnmeteredSeconds { get; init; } = 60;

    /// <summary>Include "BackgroundDataUsageRestricted" as a metered signal.</summary>
    public bool IncludeBackgroundRestricted { get; init; } = true;

    /// <summary>
    /// True iff this platform exposes a cost/metered signal we can
    /// observe. Drives whether the orchestrator bothers spinning up a
    /// watchdog — and lets the UI render the "Abort on metered network"
    /// toggle disabled with an explanatory tooltip on macOS / Linux
    /// rather than silently doing nothing.
    /// </summary>
    public static bool IsSupportedOnThisPlatform => PlatformInfo.IsWindows;

    /// <summary>Invoked when we transition into metered state.</summary>
    public event Action<string>? MeteredDetected;

    /// <summary>Invoked every evaluation with a human-readable status line.</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Starts watching and returns a Task that completes when <paramref name="ct"/> fires.
    /// The call is cooperative: cancel <paramref name="ct"/> to stop.
    ///
    /// On non-Windows platforms this returns immediately (the OS-level
    /// metered-network signal isn't available there) — see
    /// <see cref="IsSupportedOnThisPlatform"/>.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return Platform.Windows.WindowsMeteredNetworkProbe.RunAsync(this, ct);
#endif
        _log.Debug("Metered-network probe is unavailable on {Platform}; watchdog is inert.", PlatformInfo.DisplayPlatform);
        return Task.CompletedTask;
    }

    // ---- internal hooks for the per-platform probe ----

    internal void RaiseMeteredDetected(string detail) => MeteredDetected?.Invoke(detail);
    internal void RaiseStatus(string detail) => Status?.Invoke(detail);
    internal ILogger Logger => _log;
}
