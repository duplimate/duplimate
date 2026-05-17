using System;
using System.Threading;
using Avalonia.Threading;

namespace Duplimate.ViewModels;

/// <summary>
/// Coalesces a stream of "do this on the UI thread" requests into at
/// most one execution per debounce window. Used by view-models whose
/// Refresh() is expensive (BackupsViewModel rebuilds N cards;
/// LogsViewModel re-walks every history file) and which subscribe to
/// <c>ConfigStore.Changed</c> — a single user action like saving a
/// backup edit fires Changed once but a multi-step flow (e.g. add a
/// backup + apply a schedule) can fire it twice in quick succession.
///
/// Without coalescing the VM does its full work for each event,
/// dropping ScrollViewer position and re-creating per-card event
/// subscriptions for the second-and-later events. With coalescing the
/// work runs ONCE, after a short quiet period.
///
/// Implementation: the first <see cref="Schedule"/> after a quiet
/// period arms a DispatcherTimer for <see cref="DebounceMs"/>. Further
/// Schedule() calls inside that window are no-ops — the timer is
/// already armed. When the timer ticks, the action runs on the UI
/// thread and the gate is released.
///
/// Disposal cancels any pending timer; safe to call from any thread.
/// </summary>
public sealed class UiThreadDebouncer : IDisposable
{
    private readonly Action _action;
    private readonly DispatcherTimer _timer;
    private int _disposed;
    /// <summary>Mirrors <see cref="DispatcherTimer.IsEnabled"/> as a
    /// volatile int so off-UI-thread callers (HasPendingFlush from a
    /// background Config.Changed handler) can read the armed state
    /// without touching the dispatcher-affine timer. Updated whenever
    /// _timer.Start/Stop is called from the UI thread.</summary>
    private int _timerArmed;

    public UiThreadDebouncer(Action action, int debounceMs = 50)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(debounceMs), DispatcherPriority.Background,
            (_, _) => Fire());
        _timer.Stop();
    }

    /// <summary>True iff the debouncer has an armed timer that hasn't
    /// fired yet. Useful for "skip rebuild while save is pending"
    /// guards (SettingsViewModel uses it to avoid clobbering in-flight
    /// typing on a Config.Changed reload).</summary>
    public bool HasPendingFlush
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            // Read the volatile mirror, NOT _timer.IsEnabled — the
            // DispatcherTimer is UI-thread-affine and reading its
            // properties from a background thread isn't documented
            // as safe. The mirror gives us a thread-safe view.
            return Volatile.Read(ref _timerArmed) != 0;
        }
    }

    /// <summary>Request a single execution within the next debounce
    /// window. Multiple calls before the timer fires collapse into one.
    /// Safe to call from any thread.
    ///
    /// When invoked FROM the UI thread the action runs immediately
    /// (synchronous), matching the prior <c>OnUiThread(Refresh)</c>
    /// fast-path semantics — tests that mutate config from the UI
    /// thread can assert state changes without waiting on a timer, and
    /// existing UI-thread call sites see no behavioural change. Off-UI-
    /// thread invocations (the typical Config.Changed path from a
    /// worker mutating from a scheduled-task callback or the
    /// orchestrator's persistence) get the actual debounce.</summary>
    public void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            // Synchronous fast-path. Tests rely on this; UI-thread call
            // sites that fire Config.Changed from a click handler also
            // benefit from immediate-state-change semantics.
            //
            // Stop any already-armed timer BEFORE running the action so
            // a previous off-UI-thread Schedule()'s pending tick won't
            // fire the action a second time after we've just run it.
            StopTimer();
            try { _action(); } catch { /* swallow — caller's responsibility */ }
            return;
        }
        // Background thread — debounce. DispatcherTimer can only be
        // started from the UI thread, so we Post the arming step.
        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!_timer.IsEnabled) StartTimer();
        }, DispatcherPriority.Background);
    }

    /// <summary>UI-thread-only. Starts the timer AND publishes the
    /// armed state to the volatile mirror so HasPendingFlush sees
    /// the right value from any thread.</summary>
    private void StartTimer()
    {
        _timer.Start();
        Volatile.Write(ref _timerArmed, 1);
    }

    /// <summary>UI-thread-only. Stops the timer AND clears the mirror.</summary>
    private void StopTimer()
    {
        _timer.Stop();
        Volatile.Write(ref _timerArmed, 0);
    }

    /// <summary>Run the action immediately on the UI thread, cancelling
    /// any pending debounce. Use when a state-flip needs to be
    /// observable synchronously (e.g. tests).</summary>
    public void FlushNow()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            StopTimer();
            try { _action(); } catch { /* swallow — caller's responsibility */ }
        }, DispatcherPriority.Send);
    }

    private void Fire()
    {
        StopTimer();
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _action(); } catch { /* swallow — caller's responsibility */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Clear the armed mirror eagerly so HasPendingFlush returns
        // false immediately, even if the dispatcher post below races.
        Volatile.Write(ref _timerArmed, 0);
        try
        {
            // Stop must run on the UI thread because DispatcherTimer's
            // backing list is UI-thread-affine; posting handles the
            // background-thread Dispose case (e.g. finalizer).
            if (Dispatcher.UIThread.CheckAccess()) _timer.Stop();
            else Dispatcher.UIThread.Post(_timer.Stop, DispatcherPriority.Send);
        }
        catch { /* shutdown — best-effort */ }
    }
}
