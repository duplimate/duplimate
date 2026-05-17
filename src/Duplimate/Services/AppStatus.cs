using System;
using System.Threading;

namespace Duplimate.Services;

/// <summary>
/// What the app is currently doing — used by the shell to swap the taskbar
/// icon and the sidebar logo overlay so the user (and the Windows taskbar)
/// can see at a glance that a backup or restore is in progress.
/// </summary>
public enum AppActivity { Idle, Syncing }

/// <summary>
/// Broadcast of current app-wide activity. Ref-counted so nested or
/// concurrent operations don't fight: two parallel backups will each take a
/// lease, and the status stays <see cref="AppActivity.Syncing"/> until both
/// have disposed their leases.
///
/// Callers:
///   using var _ = AppStatus.BeginSyncing();
///   ... do work ...
/// </summary>
public static class AppStatus
{
    private static int _syncLeases;
    private static AppActivity _current = AppActivity.Idle;

    /// <summary>Current global activity state. Reads are free-threaded.</summary>
    public static AppActivity Current => _current;

    /// <summary>
    /// Raised whenever the state transitions. Handlers run on the thread
    /// that triggered the change; UI consumers must marshal to the UI thread
    /// themselves.
    /// </summary>
    public static event Action<AppActivity>? Changed;

    /// <summary>
    /// Take a lease that marks the app as syncing for its lifetime. Dispose
    /// the returned token when the op is done. Safe to nest / call from
    /// multiple threads simultaneously.
    /// </summary>
    public static IDisposable BeginSyncing()
    {
        if (Interlocked.Increment(ref _syncLeases) == 1)
        {
            _current = AppActivity.Syncing;
            Changed?.Invoke(_current);
        }
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref _syncLeases) == 0)
            {
                _current = AppActivity.Idle;
                Changed?.Invoke(_current);
            }
        }
    }
}
