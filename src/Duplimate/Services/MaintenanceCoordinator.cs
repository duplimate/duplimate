using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Process-wide reader / writer gate around "sensitive" Duplicacy
/// operations — Erase-backup-from-destination, Wipe-entire-destination,
/// Prune-to-latest. While one of these is in flight, no backup or
/// restore may begin (and any backup currently running gets a
/// best-effort cancel + wait so it tears down before the destructive
/// op proceeds). The user reported: "ensure that NO backup or restore
/// at all can run (including those which may be scheduled, and/or
/// already running in the background: stop them first, or maybe kill
/// them all) while one of these sensitive operations is in progress."
///
/// Why an explicit coordinator rather than reusing the per-destination
/// gate: the per-destination semaphore only blocks runs targeting the
/// SAME destination. A user erasing one backup's storage may also be
/// triggering metadata operations against the storage chain that need
/// no other Duplicacy command running ANYWHERE in the process —
/// metadata lock contention is a real footgun even across different
/// backup ids. This coordinator escalates the guarantee to the entire
/// process.
///
/// Cross-process scope: this class is in-process only. The
/// per-backup-id and per-destination NAMED semaphores still gate
/// across processes; the coordinator adds a stricter in-process layer
/// on top so the same Duplimate instance never overlaps a backup /
/// restore with an erase / prune. A second Duplimate process would
/// still hit the existing named semaphores.
/// </summary>
public sealed class MaintenanceCoordinator
{
    private static ILogger _log => AppLogger.For<MaintenanceCoordinator>();

    /// <summary>One writer at a time. Backups + restores wait on this
    /// asynchronously; erase / prune Wait on it exclusively. Reentrancy
    /// is NOT supported — nested Acquire calls in the same async chain
    /// would self-deadlock. Don't do that.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Set to >0 while a maintenance op holds the gate.
    /// Read by <see cref="IsActive"/> and the polling in
    /// <see cref="WaitForReadAsync"/> so callers can short-circuit
    /// without taking a semaphore on the hot path.</summary>
    private int _activeMaintenance;

    /// <summary>Fires when maintenance starts and ends. Args:
    /// (active, reason). UI surfaces this to disable Run Now / Restore
    /// buttons during the window.</summary>
    public event Action<bool, string>? MaintenanceStateChanged;

    /// <summary>True iff a maintenance op is currently holding the
    /// gate. UI checks this to grey out backup/restore commands.</summary>
    public bool IsActive => Volatile.Read(ref _activeMaintenance) > 0;

    /// <summary>Human-readable description of what's running, when one is.
    /// Empty when idle. Useful for tooltips on disabled buttons.</summary>
    public string CurrentReason { get; private set; } = "";

    /// <summary>
    /// Block until no maintenance is in flight. Runs on a 100ms poll
    /// (cheap because the field read is unsynchronised). Cancellation
    /// honoured. Returns immediately when the gate is idle, which is
    /// the common case.
    /// </summary>
    public async Task WaitForReadAsync(CancellationToken ct)
    {
        while (IsActive)
        {
            ct.ThrowIfCancellationRequested();
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { throw; }
        }
    }

    /// <summary>
    /// Take the maintenance lock. The returned <see cref="IDisposable"/>
    /// releases it on Dispose; callers MUST wrap the work in
    /// <c>using var lease = await Maintenance.AcquireAsync(...)</c> so a
    /// throw still releases the gate.
    ///
    /// On acquire we:
    ///   1. Take the writeLock semaphore (waits for any prior maintenance to finish).
    ///   2. Cancel every in-flight backup run via the orchestrator.
    ///   3. Wait up to <paramref name="cancelGracePeriod"/> for those
    ///      runs to finalise (their finally blocks unregister from
    ///      _runningRuns). Force-finalises any holdouts so the lock is
    ///      truly exclusive.
    ///   4. Bump _activeMaintenance and fire MaintenanceStateChanged.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(
        string reason,
        IProgress<string>? progress = null,
        TimeSpan? cancelGracePeriod = null,
        CancellationToken ct = default)
    {
        var grace = cancelGracePeriod ?? TimeSpan.FromSeconds(30);

        progress?.Report("Waiting for the maintenance gate…");
        await _writeLock.WaitAsync(ct);
        try
        {
            // CRITICAL ORDERING: bump _activeMaintenance and stamp
            // CurrentReason BEFORE we start cancelling in-flight runs.
            // The cancel-and-wait loop below can take seconds; while
            // it runs, a brand-new backup could enter the orchestrator
            // and hit its own WaitForReadAsync. If _activeMaintenance
            // were still 0 at that moment, the new backup would slip
            // past the gate and start running concurrently with the
            // maintenance op we're about to begin.
            // Earlier shape (bump flag AFTER cancel loop) had this race
            // — found in code review. The flag MUST be set first so
            // any concurrent WaitForReadAsync waits.
            CurrentReason = reason;
            Interlocked.Increment(ref _activeMaintenance);
            try { MaintenanceStateChanged?.Invoke(true, reason); }
            catch (Exception ex) { _log.Warning(ex, "MaintenanceStateChanged handler threw"); }

            // Cancel-and-wait LOOP until the orchestrator's running
            // registry is empty. A single snap-then-wait pass had a
            // narrow but real race: a backup that was past
            // `WaitForReadAsync` (because IsActive was still false at
            // its check time) but hadn't yet hit `_runningRuns.TryAdd`
            // could miss the snapshot, register itself, and start
            // running cells concurrently with maintenance. Looping
            // until the snapshot returns empty closes that gap — by
            // the time we exit the loop, no run can still be entering
            // (any new attempt sees IsActive=true at WaitForReadAsync
            // and waits) AND no run is currently in the registry.
            var orch = ServiceLocator.Orchestrator;
            const int maxIterations = 5; // belt-and-braces against pathological re-entry
            for (int iter = 0; iter < maxIterations; iter++)
            {
                var inflight = orch.SnapshotRunningBackupIds();
                if (inflight.Count == 0) break;

                if (iter == 0)
                {
                    progress?.Report($"Stopping {inflight.Count} in-flight backup{(inflight.Count == 1 ? "" : "s")}…");
                    _log.Information(
                        "Maintenance «{Reason}» cancelling {Count} in-flight backup(s) first",
                        reason, inflight.Count);
                }
                else
                {
                    _log.Information(
                        "Maintenance «{Reason}» loop iter {Iter}: still {Count} in-flight (likely a registration that landed mid-cancel)",
                        reason, iter, inflight.Count);
                }

                foreach (var id in inflight)
                    orch.CancelRun(id, $"Stopped to make way for: {reason}");

                // Wait for each to leave the registry. The wait is a
                // best-effort poll loop — if the run is still in
                // _runningRuns after the grace period, force-finalise
                // it so the gate isn't held forever by a stuck run
                // (this matches the existing escape-hatch pattern in
                // BackupOrchestrator.ForceFinaliseRun).
                foreach (var id in inflight)
                {
                    await orch.WaitForRunCompletionAsync(id, grace, ct);
                    if (orch.IsRunning(id))
                    {
                        _log.Warning(
                            "Backup {Id} did not stop within {Grace}s — force-finalising before maintenance proceeds",
                            id, grace.TotalSeconds);
                        try { orch.ForceFinaliseRun(id, $"Force-stopped for: {reason}"); }
                        catch (Exception ex) { _log.Warning(ex, "ForceFinaliseRun threw for {Id}", id); }
                    }
                }
            }

            return new Lease(this);
        }
        catch
        {
            // Setup failed AFTER we bumped the maintenance flag —
            // unwind so the next caller isn't permanently locked
            // out AND so concurrent WaitForReadAsync callers don't
            // see a phantom-active maintenance forever. Order
            // matters: clear the flag first (so readers can wake),
            // THEN release the writeLock semaphore.
            CurrentReason = "";
            if (Interlocked.Decrement(ref _activeMaintenance) < 0)
                Interlocked.Increment(ref _activeMaintenance);  // never go negative
            try { MaintenanceStateChanged?.Invoke(false, reason); }
            catch (Exception ex) { _log.Warning(ex, "MaintenanceStateChanged (rollback) handler threw"); }
            try { _writeLock.Release(); } catch { }
            throw;
        }
    }

    private void Release()
    {
        Interlocked.Decrement(ref _activeMaintenance);
        var reason = CurrentReason;
        CurrentReason = "";
        try { _writeLock.Release(); } catch { }
        try { MaintenanceStateChanged?.Invoke(false, reason); }
        catch (Exception ex) { _log.Warning(ex, "MaintenanceStateChanged (release) handler threw"); }
    }

    private sealed class Lease : IDisposable
    {
        private readonly MaintenanceCoordinator _coord;
        private int _disposed;
        public Lease(MaintenanceCoordinator c) { _coord = c; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _coord.Release();
        }
    }
}
