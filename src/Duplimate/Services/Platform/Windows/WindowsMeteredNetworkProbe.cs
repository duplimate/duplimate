using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace Duplimate.Services.Platform.Windows;

/// <summary>
/// Windows-only WinRT implementation of the metered-network probe.
/// Lives under Services/Platform/Windows/ so the WinRT
/// <c>Windows.Networking.Connectivity</c> reference never reaches the
/// cross-platform TFM (which doesn't have it on the surface).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMeteredNetworkProbe
{
    /// <summary>
    /// Adaptive-cadence poll on top of the WinRT cost API. Mirrors the
    /// pre-refactor <c>MeteredNetworkWatchdog</c> verbatim — the only
    /// change is that "raise event" now goes through the
    /// platform-neutral <see cref="MeteredNetworkWatchdog"/> shell so
    /// callers see one consistent surface.
    /// </summary>
    public static async Task RunAsync(MeteredNetworkWatchdog host, CancellationToken ct)
    {
        bool? lastMetered = null;
        int interval = host.PollWhenUnmeteredSeconds;
        var evaluateLock = new object();
        var disposed = false;

        // CRITICAL: cache the delegate as a single instance and use
        // THAT for both += and -=. Previously the code wrote
        // `NetworkStatusChanged += NetworkInformation_StatusChanged`
        // and `-= NetworkInformation_StatusChanged` — each implicit
        // method-group conversion to NetworkStatusChangedEventHandler
        // created a NEW delegate instance. Under WinRT event semantics
        // (which Windows.Networking.Connectivity uses) the runtime
        // tracks subscribers by token rather than reference, but C#'s
        // unsubscribe path goes through `WindowsRuntimeMarshal.RemoveEventHandler`
        // which compares by delegate equality — distinct method-group
        // captures don't match, and the unsubscribe LEAKS. Caching
        // the delegate as a single instance fixes both += and -= to
        // use the same reference.
        NetworkStatusChangedEventHandler handler = NetworkInformation_StatusChanged;

        try
        {
            NetworkInformation.NetworkStatusChanged += handler;
            EvaluateLocked("Startup");

            while (!ct.IsCancellationRequested)
            {
                int sleepSeconds;
                lock (evaluateLock) sleepSeconds = interval;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(sleepSeconds), ct);
                }
                catch (TaskCanceledException) { break; }

                EvaluateLocked("Poll");
            }
        }
        finally
        {
            // Set BEFORE unsubscribe so a handler-in-flight observes
            // the flag even if it landed between this line and the
            // unsubscribe actually completing.
            lock (evaluateLock) disposed = true;
            NetworkInformation.NetworkStatusChanged -= handler;
        }

        void NetworkInformation_StatusChanged(object sender)
        {
            // Bail if RunAsync has already finished — defends against
            // a late-fire event landing on a captured Status delegate
            // whose consumer has moved on.
            lock (evaluateLock) { if (disposed) return; }
            EvaluateLocked("Event");
        }

        void EvaluateLocked(string reason)
        {
            lock (evaluateLock)
            {
                if (disposed) return;
                Evaluate(host, ref lastMetered, ref interval, reason);
            }
        }
    }

    private static void Evaluate(MeteredNetworkWatchdog host, ref bool? lastMetered, ref int interval, string reason)
    {
        bool anyMetered;
        string detail;
        try
        {
            (anyMetered, detail) = SampleMetered(host.IncludeBackgroundRestricted);
        }
        catch (Exception ex)
        {
            host.RaiseStatus($"[{reason}] probe failed: {ex.Message}");
            return;
        }

        host.RaiseStatus($"[{reason}] {detail}");

        if (lastMetered != anyMetered)
        {
            lastMetered = anyMetered;
            interval = anyMetered ? host.PollWhenMeteredSeconds : host.PollWhenUnmeteredSeconds;
            if (anyMetered)
            {
                host.Logger.Warning("Metered network detected — backup will be aborted: {Detail}", detail);
                host.RaiseMeteredDetected(detail);
            }
            else
            {
                host.Logger.Information("Network is unmetered: {Detail}", detail);
            }
        }
    }

    private static (bool metered, string detail) SampleMetered(bool includeBackgroundRestricted)
    {
        bool any = false;
        var details = new System.Text.StringBuilder("profiles: ");

        var internetProfile = NetworkInformation.GetInternetConnectionProfile();
        if (internetProfile is null)
            return (false, "no active profile");

        var seen = new System.Collections.Generic.HashSet<Guid>();
        var profiles = new System.Collections.Generic.List<ConnectionProfile> { internetProfile };
        foreach (var p in NetworkInformation.GetConnectionProfiles())
        {
            if (p is null) continue;
            Guid id = Guid.Empty;
            try { id = p.NetworkAdapter?.NetworkAdapterId ?? Guid.Empty; } catch { }
            if (id != Guid.Empty && !seen.Add(id)) continue;
            if (!ReferenceEquals(p, internetProfile)) profiles.Add(p);
        }

        foreach (var p in profiles)
        {
            var level = p.GetNetworkConnectivityLevel();
            if (level == NetworkConnectivityLevel.None) continue;

            var cost = p.GetConnectionCost();
            var meteredHere = cost.NetworkCostType != NetworkCostType.Unrestricted
                            || cost.Roaming
                            || cost.OverDataLimit
                            || (includeBackgroundRestricted && cost.BackgroundDataUsageRestricted);

            if (meteredHere) any = true;

            details.Append($"[{Safe(p.ProfileName)} cost={cost.NetworkCostType} ")
                   .Append($"roam={cost.Roaming} over={cost.OverDataLimit} bg={cost.BackgroundDataUsageRestricted}] ");
        }

        return (any, details.ToString().TrimEnd());
    }

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s;
}
