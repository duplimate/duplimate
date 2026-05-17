using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.ViewModels;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// The high-level "run this backup end-to-end" orchestrator.
/// Called by the GUI ("Run now") and by the unattended CLI entrypoint ("--run").
///
/// Responsibilities:
///   - Volume-mount check (skip if drive not mounted — not a failure)
///   - Start metered-network watchdog (if enabled)
///   - For each target destination: backup → prune → check
///   - Ping Healthchecks.io
///   - Send email notification
///   - Fire desktop notification
///   - Persist RunRecord
/// </summary>
public sealed class BackupOrchestrator
{
    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly DuplicacyRunner _runner;
    private readonly LogStore _logs;
    private readonly HealthcheckService _hc;
    private readonly MailService _mail;
    private readonly NotificationService _notify;
    private readonly BackupProgressService _progress;
    private static ILogger _log => AppLogger.For<BackupOrchestrator>();

    public BackupOrchestrator(
        ConfigStore config,
        SecretsStore secrets,
        DuplicacyRunner runner,
        LogStore logs,
        HealthcheckService hc,
        MailService mail,
        NotificationService notify,
        BackupProgressService progress)
    {
        _config = config;
        _secrets = secrets;
        _runner = runner;
        _logs = logs;
        _hc = hc;
        _mail = mail;
        _notify = notify;
        _progress = progress;
    }

    /// <summary>
    /// Fans the backup out across every (source × destination) cell.
    /// A backup with 2 sources and 2 destinations produces 4 cells, each
    /// with its own <see cref="DuplicacyRunner"/> invocation, its own
    /// repo, and its own retry budget. Cell failures are isolated — one
    /// cell timing out or hitting an auth error never cancels the rest.
    /// A pre-cancelled token or a metered-network trip <em>does</em>
    /// cancel the whole run; that's the one global signal.
    /// </summary>
    /// <summary>
    /// Registry of in-flight runs so callers (the delete flow, Dashboard,
    /// CLI shutdown) can cancel a backup that's currently underway. We
    /// link the caller's external token with our own internal cancel
    /// source on entry, store the source keyed by Backup.Id, and
    /// dispose it on exit. Process-local — see <see cref="MutexNameFor"/>
    /// for the cross-process gate that pairs with this dictionary.
    /// </summary>
    private readonly ConcurrentDictionary<string, RunningInfo> _runningRuns = new();

    /// <summary>Per-run state we keep alive while a run is in flight.
    /// Lets callers (LogsView's "current run" entry, BackupsView's
    /// hard-cancel watchdog) read more than just "is it running" — the
    /// start time so the LogsView's run dropdown can render an entry
    /// for the live run with a real "started …" label, and a reference
    /// to the synthesized record so the hard-cancel path can tag it as
    /// "stopped by user" before firing RunEnded.
    /// <see cref="CancelReason"/> is set by whoever signals the cancel
    /// (the user via Stop = "Manually skipped"; the metered-network
    /// watchdog = "Network became metered"; force-finalise =
    /// "force-finalised after timeout"). The cell-loop and BuildSummary
    /// read it back so the Skipped surface text tells the user WHY,
    /// instead of the previous generic "drive unplugged or network
    /// gone" line that was wrong for at least the manual-stop case.</summary>
    private sealed class RunningInfo
    {
        public CancellationTokenSource Cts { get; }
        public DateTime StartedUtc { get; }
        public RunRecord Record { get; }
        public string? CancelReason { get; set; }
        public RunningInfo(CancellationTokenSource cts, DateTime startedUtc, RunRecord record)
        {
            Cts = cts; StartedUtc = startedUtc; Record = record;
        }
    }

    /// <summary>
    /// Cross-process run gate name for a given backup id. Two different
    /// Duplimate processes (typically: the GUI and a scheduled-task
    /// <c>--run</c> CLI invocation) must not run the same backup at the
    /// same time — both would spawn <c>duplicacy.exe backup</c> against
    /// the same storage URL and could interleave snapshot revisions,
    /// corrupting the chain.
    ///
    /// The name is <c>Local\Duplimate-Run-{sha1(backup.Id)[..16]}</c>
    /// so it scopes to the current Windows session+user (matching the
    /// <see cref="SingleInstanceCoordinator"/> convention) and is stable
    /// for a given backup across processes and across builds.
    ///
    /// Internal so the regression test can assert determinism without
    /// reaching into reflection.
    /// </summary>
    internal static string MutexNameFor(string backupId)
    {
        var h = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(backupId ?? ""));
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
        return $@"Local\Duplimate-Run-{sb}";
    }

    /// <summary>
    /// Cross-process gate name for one (destination, storage) pair.
    /// Two backups that share a target QUEUE on this name instead of
    /// racing — Duplicacy's prune phase is documented as preferring
    /// exclusive access to a storage, so serialising at this boundary
    /// is the cheapest way to guarantee we can't pile two prunes onto
    /// the same destination at once. Mirrors <see cref="MutexNameFor"/>'s
    /// hash-truncation pattern so the name is short, stable, and
    /// session-scoped (Local\) for multi-user safety.
    ///
    /// Internal so the regression test can pin determinism without
    /// reflection.
    /// </summary>
    internal static string MutexNameForDestination(string destinationId, string storageName)
    {
        var key = $"{destinationId ?? ""}|{storageName ?? ""}";
        var h = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(key));
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
        return $@"Local\Duplimate-Dest-{sb}";
    }

    /// <summary>
    /// Per-destination polling cadence while we wait for the gate.
    /// 250ms keeps the wait responsive to cancellation and to a peer
    /// run finishing without thrashing the kernel handle.
    /// </summary>
    private static readonly TimeSpan DestinationGatePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Acquire (in deterministic order) one named cross-process
    /// semaphore per (DestinationId, StorageName) the backup writes
    /// to. Blocks asynchronously until each is free; surfaces a
    /// "Queued — waiting for …" status on the run record while
    /// waiting so the BackupCard label flips to it. Honours
    /// <paramref name="ct"/> cancellation mid-wait so a user clicking
    /// Stop on a queued run drops out cleanly.
    ///
    /// Acquisition order matters: two backups whose targets share a
    /// SUBSET of destinations would deadlock if one acquired
    /// {Dest1,Dest2} and the other acquired {Dest2,Dest1}. Sorting
    /// by key makes the order globally consistent and removes that
    /// hazard.
    /// </summary>
    private async Task AcquireDestinationGatesAsync(
        Backup backup,
        RunRecord runRecord,
        List<Semaphore> acquired,
        CancellationToken ct)
    {
        // Distinct + sorted target keys. Distinct collapses a backup
        // that lists the same destination twice (config rounding) so
        // we don't try to take the same lock twice and self-deadlock.
        var keys = backup.Targets
            .Select(t => (DestId: t.DestinationId ?? "", Storage: t.StorageName ?? "default"))
            .Distinct()
            .OrderBy(k => k.DestId, StringComparer.Ordinal)
            .ThenBy(k => k.Storage, StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0) return;

        foreach (var (destId, storage) in keys)
        {
            var name = MutexNameForDestination(destId, storage);
            Semaphore sem;
            try
            {
                sem = new Semaphore(initialCount: 1, maximumCount: 1, name: name);
            }
            catch (Exception ex)
            {
                // Restricted environment — degrade gracefully and
                // continue without the cross-process gate for this
                // destination. Runs in OTHER processes might still
                // race, but the GUI won't crash. Logged so it shows
                // up in support reports.
                _log.Warning(ex,
                    "Cross-process destination gate unavailable for {Name}/{Dest}/{Storage}; running unguarded",
                    backup.Name, destId, storage);
                continue;
            }

            // Fast path: try once non-blocking. If we get it, great.
            if (sem.WaitOne(0))
            {
                // Defensive: if `acquired.Add(sem)` throws (OOM
                // adding to the List), we've already taken the
                // semaphore (count → 0) but never recorded
                // ownership, so ReleaseDestinationGates would skip
                // it and the named semaphore would stay locked from
                // every other process's view forever. Release+
                // Dispose on the failure path to undo the WaitOne.
                try { acquired.Add(sem); }
                catch { try { sem.Release(); } catch { } try { sem.Dispose(); } catch { } throw; }
                continue;
            }

            // Slow path: someone else holds it — surface "queued"
            // and poll until it's free or the user cancels.
            var destName = ResolveDestinationDisplayName(destId);
            var reason = $"waiting for «{destName}» — another backup is using it";
            runRecord.Summary = $"Queued — {reason}";
            _log.Information(
                "Run {Name} queued on destination {Dest}/{Storage}", backup.Name, destId, storage);
            try { RunQueuedReasonChanged?.Invoke(backup.Id, reason); }
            catch (Exception ex) { _log.Warning(ex, "RunQueuedReasonChanged handler threw for {Name}", backup.Name); }

            try
            {
                // Poll asynchronously: WaitOne(0) is non-blocking
                // (just probes the kernel handle). Between probes,
                // Task.Delay yields the thread so we don't pin a
                // thread-pool worker for the entire wait. ct
                // cancellation cuts the wait short within one poll
                // interval (250ms) without bouncing off WaitOne's
                // timer.
                var taken = false;
                while (!ct.IsCancellationRequested)
                {
                    if (sem.WaitOne(0))
                    {
                        // Same defensive pattern as the fast path —
                        // a List.Add throw after we've already
                        // taken the count must Release before we
                        // surface the throw, otherwise the cross-
                        // process semaphore stays locked forever.
                        try { acquired.Add(sem); }
                        catch { try { sem.Release(); } catch { } throw; }
                        taken = true;
                        break;
                    }
                    try { await Task.Delay(DestinationGatePollInterval, ct); }
                    catch (OperationCanceledException) { break; }
                }
                if (!taken)
                {
                    sem.Dispose();
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                // Don't keep a zombie handle if we throw after a
                // failed acquisition.
                try { sem.Dispose(); } catch { }
                throw;
            }
        }

        // Acquired everything — clear the queued banner and flip the
        // run record back to "Running…". Safe to do unconditionally:
        // if we never queued, the summary was already "Running…".
        runRecord.Summary = "Running…";
        try { RunQueuedReasonChanged?.Invoke(backup.Id, ""); }
        catch (Exception ex) { _log.Warning(ex, "RunQueuedReasonChanged (clear) handler threw for {Name}", backup.Name); }
    }

    /// <summary>Release + dispose every destination semaphore we
    /// acquired in <see cref="AcquireDestinationGatesAsync"/>. Called
    /// from the run's finally so a thrown body still frees the gates
    /// for the next queued backup.</summary>
    private static void ReleaseDestinationGates(List<Semaphore> acquired)
    {
        for (int i = acquired.Count - 1; i >= 0; i--)
        {
            try { acquired[i].Release(); } catch { /* not held — fine */ }
            try { acquired[i].Dispose(); } catch { }
        }
        acquired.Clear();
    }

    /// <summary>Look up a destination's friendly name from the live
    /// config for the queued-status message. Falls back to the id
    /// when the destination has been deleted between scheduling and
    /// the gate acquire (rare but possible).</summary>
    private string ResolveDestinationDisplayName(string destinationId)
    {
        if (string.IsNullOrEmpty(destinationId)) return "destination";
        var d = _config.Current.Destinations.FirstOrDefault(x => x.Id == destinationId);
        return d?.Name ?? destinationId;
    }

    /// <summary>Fired on the thread that started the run, after the run
    /// passed the concurrency gate. ViewModels subscribe to flip rows
    /// into a "running" state with a cancel button. Argument is Backup.Id.</summary>
    public event Action<string>? RunStarted;

    /// <summary>Fired when a run leaves the registry — success, failure,
    /// or skipped/cancelled. Carries the final RunRecord so subscribers
    /// can refresh history listings without reloading from disk.</summary>
    public event Action<string, RunRecord>? RunEnded;

    /// <summary>
    /// Fired when a run's queued-status changes — empty string means
    /// "no longer queued / now running", non-empty is a human-readable
    /// reason like "waiting for «Daily Photos» on Dropbox". The
    /// BackupCard subscribes so its inline label flips between
    /// "Running…" and "Queued — waiting for X" without polling. Also
    /// fired (with empty) when the gate is acquired so the card can
    /// switch back. First arg is the queued backup's id; second is
    /// the reason (or empty).
    /// </summary>
    public event Action<string, string>? RunQueuedReasonChanged;

    /// <summary>True if a run for this backup is currently in flight.</summary>
    public bool IsRunning(string backupId) => _runningRuns.ContainsKey(backupId);

    /// <summary>
    /// Snapshot of an in-flight run for the LogsView "current run" entry.
    /// Returns true and writes <paramref name="startedUtc"/> when the
    /// backup is currently running; false otherwise. The start time is
    /// the moment the orchestrator added the entry to its registry —
    /// not the wall-clock time of the user's click — so multiple
    /// observers see a consistent "this run started at T" anchor.
    /// </summary>
    public bool TryGetRunningInfo(string backupId, out DateTime startedUtc)
    {
        if (_runningRuns.TryGetValue(backupId, out var info))
        {
            startedUtc = info.StartedUtc;
            return true;
        }
        startedUtc = default;
        return false;
    }

    /// <summary>
    /// Stable reference to the in-flight RunRecord for a backup. Used by
    /// LogsView's "current run" entry — earlier the view synthesised a
    /// fresh RunRecord on every reload, which broke ComboBox selection
    /// (the dropdown bound to SelectedRun ended up pointing at a record
    /// that was no longer in the ItemsSource after the next reload, so
    /// the selection silently went blank). Returning the orchestrator's
    /// own backing instance makes the synthetic dropdown entry a stable
    /// object across reloads, so SelectedRun continues to match.
    /// </summary>
    public bool TryGetRunningRecord(string backupId, out RunRecord record)
    {
        if (_runningRuns.TryGetValue(backupId, out var info))
        {
            record = info.Record;
            return true;
        }
        record = null!;
        return false;
    }

    /// <summary>
    /// Signal an in-flight run for <paramref name="backupId"/> to cancel.
    /// Returns immediately; the run itself will tear down at the next
    /// cancellation checkpoint (between cells, within a cell waiting on
    /// retry, or inside duplicacy.exe via the spawned process being
    /// killed). Use <see cref="WaitForRunCompletionAsync"/> if you need
    /// to know it's finished. Optional <paramref name="reason"/> is
    /// recorded on the running-info so the run's final Summary surfaces
    /// it ("Manually skipped", "Network became metered", etc.) instead
    /// of the previous generic skip text.
    /// </summary>
    public void CancelRun(string backupId, string? reason = null)
    {
        if (_runningRuns.TryGetValue(backupId, out var info))
        {
            // First-writer-wins for the reason so a user-clicked Stop
            // doesn't overwrite a metered-network reason that fired
            // moments earlier (or vice versa).
            if (!string.IsNullOrEmpty(reason) && string.IsNullOrEmpty(info.CancelReason))
                info.CancelReason = reason;
            try { info.Cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    /// <summary>
    /// Hard-cancel safety net for runs that are stuck in the registry
    /// despite an earlier <see cref="CancelRun(string)"/>. The natural
    /// path is: CancelRun → run hits its next cancellation checkpoint →
    /// finally block fires RunEnded → registry clears. Real-world bug
    /// (user-reported 2026-04-29): a run failed silently in init, the
    /// run-thread got stuck in a non-cancellable wait, and the card
    /// stayed on "Running…" with the Stop button doing nothing.
    /// This method is the escape hatch — synthesise a Failed RunRecord,
    /// fire <see cref="RunEnded"/> so subscribed VMs flip back to idle,
    /// then drop the registry entry. The original run thread is left
    /// alive (we can't safely abort an arbitrary Task); if it ever
    /// resumes its own finally will be a no-op (TryRemove already
    /// removed the entry, RunEnded subscribers see a no-op record).
    /// </summary>
    public void ForceFinaliseRun(string backupId, string reason)
    {
        if (!_runningRuns.TryRemove(backupId, out var info)) return;
        try { info.Cts.Cancel(); } catch { }

        // Snapshot the live record into a fresh instance instead of
        // mutating it in place. The orchestrator's RunningInfo.Record is
        // also handed to LogsViewModel via TryGetRunningRecord and lives
        // in an ObservableCollection — a concurrent ComboBox enumeration
        // of Errors/Cells could see torn state if we flipped Status from
        // Running → Skipped mid-bind. The snapshot lets the live record
        // stay immutable to its current observers; the new instance is
        // what gets persisted and surfaced via RunEnded.
        var live = info.Record;
        var record = new RunRecord
        {
            Id = live.Id,
            BackupId = live.BackupId,
            BackupName = live.BackupName,
            StartedUtc = live.StartedUtc,
            EndedUtc = DateTime.UtcNow,
            // The user's request was a Stop, not a generic Failure —
            // surface as Skipped so the card / banner / Logs view all
            // read "Manually skipped" rather than "Failed", which would
            // suggest the backup itself broke.
            Status = BackupRunStatus.Skipped,
            Summary = string.IsNullOrEmpty(live.Summary) || live.Summary == "Running…"
                ? reason
                : $"{live.Summary} · {reason}",
            LogPath = live.LogPath,
            RevisionNumber = live.RevisionNumber,
            BytesUploaded = live.BytesUploaded,
            FilesNew = live.FilesNew,
            FilesModified = live.FilesModified,
            FilesRemoved = live.FilesRemoved,
            Errors = new List<string>(live.Errors) { reason },
            Warnings = new List<string>(live.Warnings),
            // CellOutcome is a class, so a list-copy still aliases each
            // element. The natural-completion path can mutate live.Cells
            // entries after force-finalise persists; without per-cell
            // deep-copy, any subsequent mutation back-propagates into
            // the persisted record AND tears any LogsView binding still
            // pointing at the live record. Clone each cell explicitly.
            Cells = live.Cells.Select(c => new CellOutcome
            {
                SourcePath = c.SourcePath,
                DestinationName = c.DestinationName,
                StorageName = c.StorageName,
                Status = c.Status,
                Summary = c.Summary,
                AttemptsMade = c.AttemptsMade,
                LastError = c.LastError,
                Duration = c.Duration,
                LogPath = c.LogPath,
            }).ToList(),
        };
        _log.Warning("Force-finalising stuck run for backupId={Id}: {Reason}", backupId, reason);

        // Persist the synthetic outcome to the log store + Backup.LastRun
        // so a stuck-and-force-finalised run leaves the same audit trail
        // a natural completion does. Without this, the run is invisible
        // to the LogsView history and the Backup model still claims the
        // PRIOR run's status — so the card would briefly flip back to
        // "Manually skipped" then revert to whatever was there before
        // on the next config refresh.
        var backup = _config.Current.Backups.FirstOrDefault(b => b.Id == backupId);
        if (backup is not null)
        {
            try { _logs.AppendRun(record); }
            catch (Exception ex) { _log.Warning(ex, "AppendRun threw for force-finalise of {Id}", backupId); }
            try { UpdateBackupLastRun(backup, record); }
            catch (Exception ex) { _log.Warning(ex, "UpdateBackupLastRun threw for force-finalise of {Id}", backupId); }
        }

        try { _progress.ReportRunFinished(backupId); }
        catch (Exception ex) { _log.Warning(ex, "ReportRunFinished threw for force-finalise of {Id}", backupId); }
        try { RunEnded?.Invoke(backupId, record); }
        catch (Exception ex) { _log.Warning(ex, "RunEnded handler threw for force-finalise of {Id}", backupId); }
    }

    /// <summary>
    /// Blocks until the in-flight run for <paramref name="backupId"/> has
    /// removed itself from the registry, or the timeout elapses. Used by
    /// the delete flow to make sure the orchestrator isn't still writing
    /// repo metadata while we're deleting it.
    /// </summary>
    public async Task WaitForRunCompletionAsync(string backupId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_runningRuns.ContainsKey(backupId) && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { await Task.Delay(100, ct); } catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Snapshot of every backup id currently registered as in flight.
    /// Used by <see cref="MaintenanceCoordinator"/> to fan-out
    /// cancellation before taking the maintenance gate. The dictionary
    /// is mutated by RunEnded fanout; iterating it directly would
    /// throw, so we materialise a list first.
    /// </summary>
    public IReadOnlyList<string> SnapshotRunningBackupIds() =>
        _runningRuns.Keys.ToArray();

    public async Task<RunRecord> RunBackupAsync(Backup backup, CancellationToken ct)
    {
        using var scope = Serilog.Context.LogContext.PushProperty("Backup", backup.Name);

        // Maintenance gate (Erase / Wipe / Prune). While one of these
        // sensitive ops holds the lock, NO backup may run — they need
        // exclusive access to the storage chain to avoid metadata
        // corruption. Wait synchronously here; the user explicitly
        // asked: "ensure that NO backup or restore at all can run …
        // while one of these sensitive operations is in progress."
        // The coordinator returns immediately when no maintenance is
        // in flight (the common case), so this is free 99% of the
        // time.
        // Cancellation here surfaces as a Skipped RunRecord, matching
        // the contract callers downstream rely on (OCE inside
        // RunBackupCoreAsync's cell loop is converted to a Skipped
        // record via ClassifyFromCells; OCE inside
        // AcquireDestinationGatesAsync is similarly converted via the
        // catch at line ~750-ish). Letting an OCE escape from this
        // entry-point WaitForReadAsync would surprise schedulers and
        // tests that await RunBackupAsync expecting a record back
        // even on cancellation.
        try
        {
            await ServiceLocator.Maintenance.WaitForReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return new RunRecord
            {
                BackupId = backup.Id,
                BackupName = backup.Name,
                StartedUtc = DateTime.UtcNow,
                EndedUtc = DateTime.UtcNow,
                Status = BackupRunStatus.Skipped,
                Summary = "Cancelled before run started.",
            };
        }

        // Cross-process gate. The GUI and a scheduled-task `--run` invocation
        // can race to back up the same id; both would spawn duplicacy.exe
        // against the same storage URL and could interleave snapshot
        // revisions, corrupting the chain on the next prune/restore.
        // Named Semaphore (NOT Mutex) because the await below may resume
        // on a different thread-pool thread, and Mutex.ReleaseMutex from
        // a non-acquiring thread would throw — Semaphore has no thread
        // affinity.
        Semaphore? crossProc = null;
        var crossProcAcquired = false;
        try
        {
            crossProc = new Semaphore(initialCount: 1, maximumCount: 1, name: MutexNameFor(backup.Id));
        }
        catch (Exception ex)
        {
            // Named-handle CREATION can fail in restricted environments
            // (locked-down sandboxes, exotic permission setups). Fall
            // through to the in-process gate alone — better than refusing
            // every backup. We deliberately DON'T catch a WaitOne failure
            // below the same way: a WaitOne throw means the kernel object
            // exists but we couldn't acquire (e.g. AbandonedMutex-class
            // states, handle-table exhaustion). Treating that as
            // "acquired" would let a duplicate cross-process run through
            // the gate. Better to bubble up and fail the run.
            _log.Warning(ex, "Cross-process run gate unavailable for {Name}; using in-process gate only", backup.Name);
            crossProcAcquired = true;
        }
        if (crossProc is not null)
        {
            // WaitOne is OUTSIDE the catch above on purpose — see
            // commentary. Any throw here propagates and the outer
            // semantics (refuse the run) are correct.
            crossProcAcquired = crossProc.WaitOne(0);
        }

        if (!crossProcAcquired)
        {
            crossProc?.Dispose();
            _log.Warning(
                "Run for {Name} already in progress in another process — refusing duplicate request",
                backup.Name);
            return new RunRecord
            {
                BackupId = backup.Id,
                BackupName = backup.Name,
                StartedUtc = DateTime.UtcNow,
                EndedUtc = DateTime.UtcNow,
                Status = BackupRunStatus.Skipped,
                Summary = "Skipped — another run for this backup is already in progress (scheduled task or another window).",
            };
        }

        // From here on, the semaphore IS held. Wrap everything in a single
        // try/finally so a throw between this point and the inner try block
        // (e.g. a CTS allocation OOM, AppStatus throwing, _runningRuns
        // somehow throwing) cannot leak the semaphore across the user's
        // entire session. Earlier versions had a release-only-in-the-inner-
        // finally pattern that left a window where an exception would
        // permanently brick that backup id until process exit.
        try
        {
            // In-process gate. The cross-process semaphore alone would suffice
            // for serialisation, but the dictionary is what carries the CTS
            // for cancellation routing (CancelRun / WaitForRunCompletionAsync)
            // — the semaphore can't carry per-run state.
            // The Syncing lease is taken AFTER both gates so a refused duplicate
            // doesn't briefly flip the app's syncing icon for ~1µs.
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Synthesize an in-flight Running record up-front; replace
            // it with the real outcome on success. This guarantees the
            // RunEnded event always carries a populated record, even if
            // RunBackupCoreAsync throws before returning one. The same
            // instance is also stashed in the running-info registry so
            // ForceFinaliseRun can mutate it when the user hard-cancels
            // a stuck run.
            //
            // EndedUtc MUST stay null while the run is in flight —
            // LogsViewModel.IsShowingLive checks for the (Status =
            // Running, EndedUtc = null) shape to pivot DisplayText to
            // LiveTail during a backup. Setting EndedUtc up-front made
            // the predicate fail, so the terminal pane stayed empty for
            // the entire run and only the persisted log appeared after
            // RunEnded swapped SelectedRun to the finished entry. The
            // user reported: "the terminal still doesn't show any logs
            // when running a backup; if I stop in the middle, then
            // it'll show log entries all at once."
            // The catch / finally branches below set EndedUtc to
            // DateTime.UtcNow when the run actually ends, so the
            // RunEnded payload is always a properly closed record.
            RunRecord result = new()
            {
                BackupId = backup.Id,
                BackupName = backup.Name,
                StartedUtc = DateTime.UtcNow,
                EndedUtc = null,
                Status = BackupRunStatus.Running,
                Summary = "Running…",
            };
            var runningInfo = new RunningInfo(localCts, result.StartedUtc, result);
            if (!_runningRuns.TryAdd(backup.Id, runningInfo))
            {
                _log.Warning("Run for {Name} already in progress — refusing duplicate request", backup.Name);
                return new RunRecord
                {
                    BackupId = backup.Id,
                    BackupName = backup.Name,
                    StartedUtc = DateTime.UtcNow,
                    EndedUtc = DateTime.UtcNow,
                    Status = BackupRunStatus.Skipped,
                    Summary = "Skipped — another run for this backup is already in progress.",
                };
            }

            // Re-check the maintenance gate now that we're registered.
            // The earlier WaitForReadAsync at the top of this method
            // was a snapshot read — between its return and the TryAdd
            // above, a concurrent Erase / Wipe / Prune could have
            // started its Acquire. The maintenance Acquire's snap-and-
            // cancel loop will see us in the registry and cancel us,
            // but that takes some time during which we'd otherwise be
            // doing setup work concurrent with the maintenance op.
            // Fast-path skip: if maintenance is now active, unwind
            // ourselves cleanly and return Skipped without waiting
            // for the cancel-loop to chase us. The maintenance
            // Acquire's NEXT loop iteration sees the empty registry
            // and proceeds.
            if (ServiceLocator.Maintenance.IsActive)
            {
                _runningRuns.TryRemove(backup.Id, out _);
                _log.Information(
                    "Run for {Name} aborting — a storage-maintenance task started during run startup",
                    backup.Name);
                return new RunRecord
                {
                    BackupId = backup.Id,
                    BackupName = backup.Name,
                    StartedUtc = DateTime.UtcNow,
                    EndedUtc = DateTime.UtcNow,
                    Status = BackupRunStatus.Skipped,
                    Summary = "Skipped — storage maintenance is in progress.",
                };
            }
            using var _syncing = AppStatus.BeginSyncing();
            ct = localCts.Token;
            try { RunStarted?.Invoke(backup.Id); }
            catch (Exception ex) { _log.Warning(ex, "RunStarted handler threw for {Name}", backup.Name); }

            // Per-destination QUEUEING. Two backups whose targets
            // overlap on a (DestId, StorageName) pair would otherwise
            // run their `prune` phases concurrently against the same
            // Duplicacy storage — Duplicacy's fossil-collection prune
            // is documented as preferring exclusive access, so two
            // simultaneous prunes can race on fossil deletion criteria.
            // Cross-process named semaphores let us serialise BOTH
            // the GUI's runs AND any --run scheduled-task invocations
            // that happen to fire at the same moment. The user
            // reported the underlying concern: "if both are scheduled
            // at the same time, can you confirm they'll be queued?"
            //
            // Acquired in deterministic key order so two backups that
            // share a SUBSET of destinations don't deadlock on
            // ABBA acquisition.
            var destinationSemaphores = new List<Semaphore>(backup.Targets.Count);
            try
            {
                await AcquireDestinationGatesAsync(backup, result, destinationSemaphores, ct);

                // Pass in `result` so RunBackupCoreAsync mutates the
                // SAME instance the registry is holding — TryGetRunningRecord
                // and ForceFinaliseRun then see live progress instead of
                // a stale "Running…" snapshot.
                await RunBackupCoreAsync(backup, result, ct);
                return result;
            }
            catch (OperationCanceledException)
            {
                // Cancellation while waiting on the destination gate
                // (or any other awaited cancellation point pre-cells).
                // Convert to a clean Skipped RunRecord so callers that
                // await RunBackupAsync see the same record-shaped result
                // as a cancelled mid-cells run, instead of having to
                // catch OCE separately. RunBackupCoreAsync's own
                // cancellation path (cells already running) classifies
                // the same way via ClassifyFromCells.
                result.Status = BackupRunStatus.Skipped;
                result.Summary = "Cancelled before run started.";
                result.EndedUtc = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                result.Summary = "Run threw before completing.";
                result.EndedUtc = DateTime.UtcNow;
                throw;
            }
            finally
            {
                // Release destination gates FIRST so a queued backup
                // can start as soon as the run actually finishes —
                // before we do the persist + alerts work below
                // (which can take a couple of seconds for slow disks
                // or remote toast services). The persisted history
                // file lives in AppData and doesn't touch the
                // destination, so this re-ordering is safe.
                ReleaseDestinationGates(destinationSemaphores);

                // ATOMIC OWNERSHIP TRANSFER. TryRemove returns the
                // authoritative answer: this thread (true) or
                // ForceFinaliseRun (false) owns finalisation. Whoever
                // owns it does:
                //   1. _logs.AppendRun(record)         — persist history
                //   2. UpdateBackupLastRun(backup, …)  — update LastRun
                //   3. _progress.ReportRunFinished(id) — clear progress
                //   4. RunEnded?.Invoke(id, record)    — UI fanout
                //   5. SendAlertsAsync(backup, …)      — toast/email
                // The loser does nothing — there's no TOCTOU window
                // because TryRemove is the decision point.
                var weOwnFinalisation = _runningRuns.TryRemove(backup.Id, out _);
                if (weOwnFinalisation)
                {
                    var duration = (result.EndedUtc ?? DateTime.UtcNow) - result.StartedUtc;
                    _log.Information("Run ended: {Name} status={Status} duration={DurationSec:F1}s summary={Summary}",
                        backup.Name, result.Status, duration.TotalSeconds, result.Summary ?? "");
                    try { _logs.AppendRun(result); }
                    catch (Exception ex) { _log.Warning(ex, "AppendRun threw for {Name}", backup.Name); }
                    try { UpdateBackupLastRun(backup, result); }
                    catch (Exception ex) { _log.Warning(ex, "UpdateBackupLastRun threw for {Name}", backup.Name); }
                    try { _progress.ReportRunFinished(backup.Id); }
                    catch (Exception ex) { _log.Warning(ex, "ReportRunFinished threw for {Name}", backup.Name); }
                    try { RunEnded?.Invoke(backup.Id, result); }
                    catch (Exception ex) { _log.Warning(ex, "RunEnded handler threw for {Name}", backup.Name); }
                    try { await SendAlertsAsync(backup, result); }
                    catch (Exception ex) { _log.Warning(ex, "SendAlertsAsync threw for {Name}", backup.Name); }
                }
                else
                {
                    _log.Information("Run for {Name} was force-finalised before natural completion — skipping duplicate persist + RunEnded + alerts", backup.Name);
                }
            }
        }
        finally
        {
            // Always release + dispose the semaphore, even on the
            // early-return-Skipped path. Any throw between WaitOne(0)
            // succeeding and the inner try winds up here.
            try { crossProc?.Release(); } catch { /* not held — fine */ }
            try { crossProc?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Mutates <paramref name="final"/> with the run's outcome (status,
    /// cells, errors, summary, ended-utc) and returns the same instance.
    /// Earlier this method allocated a fresh RunRecord internally and
    /// returned it, leaving the caller's RunningInfo.Record reference
    /// pointing at the original synthetic forever — TryGetRunningRecord
    /// kept reporting "Running…" with no cells, and ForceFinaliseRun
    /// cloned the stale synthetic to persist instead of the real
    /// outcome's cell breakdown. Mutating the caller's instance keeps
    /// the registry's view in sync as the run progresses.
    /// </summary>
    private async Task<RunRecord> RunBackupCoreAsync(Backup backup, RunRecord final, CancellationToken ct)
    {

        var sources = backup.SourcePaths;
        _log.Information(
            "Run starting: sources={SourceCount} targets={TargetCount} threads={Threads} vss={Vss}",
            sources.Count, backup.Targets.Count, backup.Threads, backup.UseVss);

        // Both early-return paths below leave persist + alerts to the
        // outer RunBackupAsync's natural finally (gated on TryRemove
        // ownership). Calling them here would double-persist if force-
        // finalise hasn't fired and would race with force-finalise if
        // it has — same TOCTOU class as the main path. The early-return
        // is an empty-config fast-fail; the user gets one history
        // entry + one alert from the finally.
        if (sources.Count == 0)
        {
            final.EndedUtc = DateTime.UtcNow;
            final.Status = BackupRunStatus.Failed;
            final.Errors.Add("Backup has no source paths configured.");
            final.Summary = "No sources to back up.";
            return final;
        }

        if (backup.Targets.Count == 0)
        {
            // Fast-fail when the backup has zero destinations (e.g. the
            // user removed the only destination this backup pointed at
            // via the Destinations tab). Without this the orchestrator
            // walked an empty target loop and reported Success on a
            // run that didn't actually back anything up — a silent
            // data-loss footgun. The status text mentions exactly what
            // the user needs to do so they can fix it from the toast.
            final.EndedUtc = DateTime.UtcNow;
            final.Status = BackupRunStatus.Failed;
            final.Errors.Add("Backup has no destinations. Add one (or delete this backup) before running again.");
            final.Summary = "No destinations configured.";
            return final;
        }

        if (backup.HealthcheckId is { } hcUuid)
            await _hc.PingStartAsync(hcUuid, ct);

        // Pre-populate the (source × destination) cell matrix in the
        // progress service so the Overall denominator reflects the FULL
        // workload from t=0 — not just the cell that's currently
        // running. Without this, a 2-source × 2-destination run with
        // the first cell at 21% and three not-yet-started cells
        // displays Overall=21% (only one cell exists in the dictionary
        // → 21/1 = 21). With the matrix pre-registered we correctly
        // get 21/4 ≈ 5%, and per-source rolls up as "fraction of
        // destinations done" matching the user's mental model
        // (50% when one of two destinations finishes).
        var plannedDestNames = backup.Targets
            .Select(t => _config.Current.Destinations.FirstOrDefault(d => d.Id == t.DestinationId)?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (plannedDestNames.Count > 0)
        {
            // Best-effort byte-weights: read from SourceSizeProbe's
            // cache so we don't burn IO walking each source tree at
            // run start (the user explicitly asked us not to). The
            // BackupCard's source-size labels populate this cache as
            // a side-effect of opening the Backups view, so for the
            // common case the weights are available. Sources without
            // a cached size silently fall back to equal weight inside
            // RecomputeOverall.
            var weights = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in sources)
            {
                var cached = ServiceLocator.SizeProbe.TryGetCached(src);
                if (cached is { Bytes: > 0 } r) weights[src] = r.Bytes;
            }
            _progress.RegisterRunCells(backup.Id, sources, plannedDestNames, weights);
        }

        // Run-wide cancellation: user cancel OR metered-network watchdog.
        // Cell failures never trigger this — they're isolated below.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? watchdogTask = null;
        if (backup.AbortOnMeteredNetwork)
        {
            var watchdog = new MeteredNetworkWatchdog();
            watchdog.MeteredDetected += _ =>
            {
                // Record the reason on the running-info BEFORE signalling
                // cancellation so the cell loop sees it when its
                // OperationCanceledException fires.
                if (_runningRuns.TryGetValue(backup.Id, out var info)
                    && string.IsNullOrEmpty(info.CancelReason))
                {
                    info.CancelReason = "Network became metered (mobile / tethered) — backup paused per backup setting.";
                }
                try { linked.Cancel(); } catch { }
            };
            watchdogTask = Task.Run(() => watchdog.RunAsync(linked.Token), linked.Token);
        }

        try
        {
            // Outer loop: sources. Inner loop: targets. Order matters
            // only cosmetically — we run linearly because a multi-GB
            // backup already saturates bandwidth; parallel cells would
            // just trash the disk + network queue.
            foreach (var sourcePath in sources)
            {
                if (linked.IsCancellationRequested) break;

                foreach (var target in backup.Targets)
                {
                    if (linked.IsCancellationRequested) break;

                    var dest = _config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
                    if (dest is null)
                    {
                        _log.Warning("Target references missing destination {DestId}", target.DestinationId);
                        final.Cells.Add(new CellOutcome
                        {
                            SourcePath = sourcePath,
                            DestinationName = "(missing)",
                            StorageName = target.StorageName,
                            Status = BackupRunStatus.Failed,
                            LastError = $"Destination {target.DestinationId} not found in config.",
                        });
                        final.Errors.Add($"Destination {target.DestinationId} not found");
                        continue;
                    }

                    // Volume-mount check for local-like destinations.
                    // A missing drive is Skipped (not Failed) — the
                    // unattended CLI treats that as a graceful outcome.
                    if (dest.IsLocalLike)
                    {
                        var vc = VolumeDetector.Check(dest.ExpectedDriveLetter, dest.ExpectedVolumeLabel);
                        if (vc.Verdict != VolumeDetector.Verdict.Mounted)
                        {
                            _log.Information("Skipping cell {Src}→{Dest}: {Detail}", sourcePath, dest.Name, vc.Detail);
                            final.Cells.Add(new CellOutcome
                            {
                                SourcePath = sourcePath,
                                DestinationName = dest.Name,
                                StorageName = target.StorageName,
                                Status = BackupRunStatus.Skipped,
                                Summary = vc.Detail,
                            });
                            final.Warnings.Add($"[{sourcePath} → {dest.Name}] skipped: {vc.Detail}");
                            continue;
                        }
                    }

                    // Per-cell progress lifecycle: report start → forward
                    // every stdout line through the progress service so
                    // the BackupCard can render a live percentage → mark
                    // cell ended on completion. The line callback is
                    // passed AS A PARAMETER all the way to the runner
                    // (rather than via the runner's shared LineWritten
                    // event) so concurrent runs against the same runner
                    // singleton can't cross-attribute lines: cell A's
                    // handler sees only cell A's stdout. The runner
                    // still raises LineWritten for the global "live
                    // tail" subscriber (LogsViewModel) which wants the
                    // union of every active backup.
                    var capturedSourcePath = sourcePath;
                    var capturedDestName = dest.Name;
                    _progress.ReportCellStarted(backup.Id, sourcePath, capturedDestName);
                    Action<string> lineHandler = line =>
                        _progress.ReportLine(backup.Id, capturedSourcePath, capturedDestName, line);
                    // Initialise with a Failed sentinel rather than null! so an
                    // exception escaping RunCellWithRetryAsync (it shouldn't
                    // — it has its own catch — but defensively) leaves a
                    // valid CellOutcome the finally block and the cell list
                    // can both reference.
                    var cellOutcome = new CellOutcome
                    {
                        SourcePath = sourcePath,
                        DestinationName = dest.Name,
                        StorageName = target.StorageName,
                        Status = BackupRunStatus.Failed,
                        Summary = "did not start",
                    };
                    try
                    {
                        cellOutcome = await RunCellWithRetryAsync(backup, sourcePath, dest, target, linked.Token, lineHandler);
                    }
                    catch (Exception ex)
                    {
                        // Defensive: any unexpected exception (NRE in our own
                        // code, OOM, etc.) is recorded as a cell-level
                        // failure with the message so the run summary and
                        // the per-run log surface what happened.
                        _log.Error(ex, "Cell {Src}→{Dest} threw an unexpected exception", sourcePath, dest.Name);
                        cellOutcome.LastError = ex.Message;
                        cellOutcome.Summary = "unexpected error";
                    }
                    finally
                    {
                        _progress.ReportCellEnded(backup.Id, sourcePath, capturedDestName,
                            success: cellOutcome.Status == BackupRunStatus.Success);
                    }
                    final.Cells.Add(cellOutcome);
                    MergeCellInto(final, cellOutcome);

                    _log.Information(
                        "Cell {Src}→{Dest} finished: status={Status} attempts={Attempts} {Summary}",
                        sourcePath, dest.Name, cellOutcome.Status, cellOutcome.AttemptsMade, cellOutcome.Summary);
                }
            }
        }
        finally
        {
            try { linked.Cancel(); } catch { }
            if (watchdogTask is not null) { try { await watchdogTask; } catch { } }
        }

        final.EndedUtc = DateTime.UtcNow;
        final.Status = ClassifyFromCells(final, ct);
        // Pass the orchestrator's recorded cancel reason as a fallback
        // so the run-level Summary still reads "Manually skipped after
        // 1m 12s" even when the user clicked Stop BEFORE any cell got
        // a chance to attach its own per-cell skip reason. Without this,
        // an early Stop produced a generic "Skipped after 1m 12s" line
        // that hid which actor cancelled (user / metered watchdog /
        // force-finalise).
        var orchestratorReason = _runningRuns.TryGetValue(backup.Id, out var info)
            ? info.CancelReason
            : null;
        final.Summary = BuildSummary(final, orchestratorReason);

        // Persistence + alerts are NOT done here — they're now done in
        // the outer RunBackupAsync's finally block, gated on the
        // TryRemove that authoritatively decides who owns finalisation
        // (this thread or ForceFinaliseRun). The previous implementation
        // had a TOCTOU window where a `ContainsKey` check here could
        // return true, ForceFinaliseRun could TryRemove + persist, and
        // we would then ALSO persist — producing two history entries
        // with the same StartedUtc and a duplicate alert.
        return final;
    }

    /// <summary>
    /// Per-cell retry cascade. Transient failures (network blip, remote
    /// rate-limit, brief locked-file contention) deserve up to 3 tries
    /// with 5s → 20s → 60s backoff. Post-3 we mark the cell Failed and
    /// move on — we do <em>not</em> cancel the enclosing run.
    /// </summary>
    private async Task<CellOutcome> RunCellWithRetryAsync(
        Backup backup,
        string sourcePath,
        Destination dest,
        BackupTarget target,
        CancellationToken ct,
        Action<string>? lineSink = null)
    {
        var cell = new CellOutcome
        {
            SourcePath = sourcePath,
            DestinationName = dest.Name,
            StorageName = target.StorageName,
            Status = BackupRunStatus.Running,
        };

        // Caps + backoff. First try runs immediately; subsequent tries
        // wait the cascade duration. Trivial to extend if someone needs
        // longer cascades — pair of arrays.
        var backoffs = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60) };

        var started = DateTime.UtcNow;
        RunRecord? lastRun = null;
        string? lastError = null;

        for (int attempt = 0; attempt < backoffs.Length; attempt++)
        {
            if (ct.IsCancellationRequested) break;

            if (backoffs[attempt] > TimeSpan.Zero)
            {
                try { await Task.Delay(backoffs[attempt], ct); }
                catch (TaskCanceledException) { break; }
            }

            cell.AttemptsMade = attempt + 1;

            try
            {
                lastRun = await _runner.BackupPruneCheckAsync(backup, sourcePath, dest, target.StorageName, ct, lineSink);
            }
            catch (OperationCanceledException)
            {
                cell.Status = BackupRunStatus.Skipped;
                cell.Summary = ResolveCancelReason(backup.Id);
                cell.Duration = DateTime.UtcNow - started;
                return cell;
            }
            catch (Exception ex) when (ex is INonRetriable)
            {
                // Fail-fast for known non-retriable errors (init failures,
                // bad preferences, decrypt mismatches). Earlier we'd burn
                // 91s of 5s/20s/60s backoff repeating the same config error
                // — see the user's 2026-04-28 PREFERENCE_OPEN failure log.
                // Surface the underlying exception's message verbatim so
                // the failure toast / email / Activity & logs render the
                // captured duplicacy stdout, not a generic "failed".
                lastError = ex.Message;
                cell.Status = BackupRunStatus.Failed;
                cell.LastError = ex.Message;
                cell.Summary = "failed (non-retriable)";
                cell.Duration = DateTime.UtcNow - started;
                // DuplicacyInitException carries the per-cell log path
                // so the LogsView's "Selected run" pane can show what
                // duplicacy itself printed before exiting non-zero.
                if (ex is DuplicacyInitException die && !string.IsNullOrEmpty(die.LogPath))
                    cell.LogPath = die.LogPath!;
                _log.Warning(ex,
                    "Cell {Src}→{Dest} hit non-retriable error — skipping retry cascade",
                    sourcePath, dest.Name);
                return cell;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _log.Warning(ex, "Cell {Src}→{Dest} attempt {Attempt} threw", sourcePath, dest.Name, attempt + 1);
                continue;
            }

            // Success or benign warning → commit and return.
            if (lastRun.Status == BackupRunStatus.Success || lastRun.Status == BackupRunStatus.Warning)
            {
                cell.Status = lastRun.Status;
                cell.Summary = lastRun.Summary;
                cell.Duration = DateTime.UtcNow - started;
                // Carry the per-attempt log file path up so the
                // LogsView's "Selected run" pane has something to
                // render. Earlier this was only set on init failure
                // (DuplicacyInitException), so a successful run AND a
                // retry-exhausted failure both surfaced empty logs in
                // the UI even though the duplicacy stdout was on
                // disk. The user reported: "I am running the Hello
                // backup but the log for this run is empty."
                cell.LogPath = lastRun.LogPath ?? "";
                // Promote per-cell metrics into the caller's RunRecord
                // via MergeCellInto on the way out; here we record the
                // cell-level numbers.
                return cell;
            }

            // Skipped → don't retry; treat as a cell-level decision.
            if (lastRun.Status == BackupRunStatus.Skipped)
            {
                cell.Status = BackupRunStatus.Skipped;
                cell.Summary = lastRun.Summary;
                cell.Duration = DateTime.UtcNow - started;
                cell.LogPath = lastRun.LogPath ?? "";
                return cell;
            }

            lastError = lastRun.Errors.Count > 0 ? lastRun.Errors[^1] : null;
            _log.Warning(
                "Cell {Src}→{Dest} attempt {Attempt} failed — will retry ({Remaining} left)",
                sourcePath, dest.Name, attempt + 1, backoffs.Length - attempt - 1);
        }

        cell.Status = ct.IsCancellationRequested ? BackupRunStatus.Skipped : BackupRunStatus.Failed;
        cell.Summary = cell.Status == BackupRunStatus.Skipped
            ? ResolveCancelReason(backup.Id)
            : "failed after retries";
        cell.LastError = lastError;
        cell.Duration = DateTime.UtcNow - started;
        // Final attempt's log is the most useful for diagnosing why
        // the cascade gave up — surface it on the cell so the
        // history.json record carries it through to the LogsView.
        if (lastRun is not null && !string.IsNullOrEmpty(lastRun.LogPath))
            cell.LogPath = lastRun.LogPath;
        return cell;
    }

    /// <summary>Read the cancel-reason recorded on the running-info for
    /// this backup, or fall back to a generic "Cancelled" if no caller
    /// stamped a reason. Used by every cell-level Skipped summary so
    /// the LogsView and the card status text both surface the WHY of
    /// the skip rather than the previous generic "cancelled" line.
    /// </summary>
    private string ResolveCancelReason(string backupId)
    {
        if (_runningRuns.TryGetValue(backupId, out var info)
            && !string.IsNullOrEmpty(info.CancelReason))
            return info.CancelReason!;
        return "Cancelled";
    }

    /// <summary>
    /// Roll cell-level metrics up into the run's top-line counters so
    /// the dashboard and summary line still work. Cells retain the
    /// per-cell breakdown for the detail view.
    /// </summary>
    private static void MergeCellInto(RunRecord run, CellOutcome cell)
    {
        // We don't have per-cell bytes/files in CellOutcome today —
        // they live on the RunRecord returned from the runner, which
        // RunCellWithRetryAsync currently discards. If we need per-cell
        // upload stats in the UI later, thread them through CellOutcome;
        // for now the tile shows aggregate + per-cell Status only.
        if (cell.Status == BackupRunStatus.Failed && cell.LastError is { } err)
            run.Errors.Add($"[{cell.SourcePath} → {cell.DestinationName}] {err}");

        // Propagate the first cell's log path up to the run record so
        // the LogsView can locate something to render in the Selected
        // run pane. Multi-cell runs share a backup-name log dir; one
        // path is enough to navigate the user there.
        if (string.IsNullOrEmpty(run.LogPath) && !string.IsNullOrEmpty(cell.LogPath))
            run.LogPath = cell.LogPath;
    }

    private static BackupRunStatus ClassifyFromCells(RunRecord r, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return BackupRunStatus.Skipped;
        if (r.Cells.Count == 0) return BackupRunStatus.Failed;

        var hasFailure = r.Cells.Any(c => c.Status == BackupRunStatus.Failed);
        var hasWarning = r.Cells.Any(c => c.Status == BackupRunStatus.Warning);
        var hasSkipped = r.Cells.Any(c => c.Status == BackupRunStatus.Skipped);
        var hasSuccess = r.Cells.Any(c => c.Status == BackupRunStatus.Success);

        if (hasFailure) return BackupRunStatus.Failed;
        if (hasWarning) return BackupRunStatus.Warning;
        // All cells skipped, no failure → overall Skipped (e.g. every
        // external drive unplugged).
        if (!hasSuccess && hasSkipped) return BackupRunStatus.Skipped;
        return BackupRunStatus.Success;
    }

    private async Task SendAlertsAsync(Backup backup, RunRecord record)
    {
        var severity = record.Status switch
        {
            BackupRunStatus.Success => NotificationSeverity.Success,
            BackupRunStatus.Warning => NotificationSeverity.Warning,
            BackupRunStatus.Failed  => NotificationSeverity.Failure,
            BackupRunStatus.Skipped => NotificationSeverity.Info,
            _                       => NotificationSeverity.Info,
        };

        // Toast title: lead with the app name so the user sees who the
        // notification is from in the action-center list (Windows just
        // shows the title in the strip, not the app icon's owner). The
        // backup's own name + verdict goes in the title body.
        var verdict = record.Status switch
        {
            BackupRunStatus.Success => "completed",
            BackupRunStatus.Warning => "completed with warnings",
            BackupRunStatus.Failed  => "FAILED",
            BackupRunStatus.Skipped => "skipped",
            _                       => record.Status.ToString().ToLowerInvariant(),
        };
        var title = $"Duplimate — «{backup.Name}» {verdict}";

        // Toast body: useful one-liner. For failures, surface the most
        // common reason instead of the generic summary, plus a hint
        // that the user can see logs in Activity & logs.
        string detail;
        if (record.Status == BackupRunStatus.Failed)
        {
            var firstError = record.Errors.Count > 0 ? record.Errors[0] : null;
            detail = firstError is null
                ? $"{record.Summary}. Open Duplimate → Activity & logs for details."
                : $"{firstError}\nOpen Duplimate → Activity & logs for the full run history.";
            // Cap toast detail length — Windows truncates aggressively.
            if (detail.Length > 350) detail = detail[..347] + "...";
        }
        else
        {
            detail = $"{record.Summary}";
        }

        // Click-to-logs: clicking the toast body navigates to the
        // Backups → Activity & logs tab and pre-selects this backup's
        // most recent run. We capture backup.Id by value so the closure
        // is safe across runs. AppNavigator.Go marshals to UI thread,
        // and SelectBackupById is a no-op when the run isn't available
        // yet (e.g. config hasn't been re-read), so the action degrades
        // gracefully if anything's mid-flight.
        var backupIdForToast = backup.Id;
        Action onClick = () =>
        {
            AppNavigator.Go(NavItem.Logs);
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l
                    && l.MainWindow is { } mw)
                {
                    // Bring the main window to the foreground so the
                    // user actually sees the tab swap. WindowState =
                    // Normal handles a minimized window; Activate
                    // pulls it forward.
                    if (mw.WindowState == Avalonia.Controls.WindowState.Minimized)
                        mw.WindowState = Avalonia.Controls.WindowState.Normal;
                    mw.Show();
                    mw.Activate();
                    if (mw.DataContext is ViewModels.MainWindowViewModel vm)
                        vm.Logs.SelectBackupById(backupIdForToast);
                }
            }
            catch (Exception ex) { _log.Warning(ex, "Click-to-logs handler threw"); }
        };
        // Fire-and-forget the toast: NotifyAsync's "Failure" path
        // shows a topmost alert whose Task completes only when the
        // user dismisses it. Awaiting that pinned RunBackupAsync's
        // own Task open, which in turn pinned UI state ("Running…",
        // "Verifying…", "Queued — …") until the user closed the
        // alert. The user reported: "Notification is fire and
        // forget, it should not prevent the UI from being updated."
        // Errors land in the app log via the lambda's catch.
        _ = Task.Run(async () =>
        {
            try { await _notify.NotifyAsync(title, detail, severity, onClick); }
            catch (Exception ex) { _log.Warning(ex, "NotifyAsync threw for {Name}", backup.Name); }
        });

        // Email — respect per-category preferences.
        var m = _config.Current.Mail;
        var shouldEmail = record.Status switch
        {
            BackupRunStatus.Success => m.OnSuccess,
            BackupRunStatus.Warning => m.OnFailure, // warnings are close enough to failure
            BackupRunStatus.Failed  => m.OnFailure,
            BackupRunStatus.Skipped => m.OnSkip,
            _ => false,
        };
        if (shouldEmail)
            await _mail.SendAsync(title, BuildEmailBody(backup, record), CancellationToken.None);

        // Healthchecks ping.
        if (backup.HealthcheckId is { } hcUuid)
        {
            var body = BuildEmailBody(backup, record);
            if (record.Status is BackupRunStatus.Success or BackupRunStatus.Warning)
                await _hc.PingSuccessAsync(hcUuid, body, CancellationToken.None);
            else if (record.Status == BackupRunStatus.Failed)
                await _hc.PingFailureAsync(hcUuid, body, CancellationToken.None);
            // "Skipped" intentionally pings neither — the drive wasn't there; that isn't a health signal.
        }
    }

    private void UpdateBackupLastRun(Backup backup, RunRecord record)
    {
        // UpdateQuiet (NOT Update) — Config.Changed fanout is redundant
        // here because BackupsViewModel.OnRunEnded already handles the
        // per-card update directly off the orchestrator's RunEnded
        // event. Without the suppression, every run-completion fired
        // both events and forced a full Refresh on every subscribed VM
        // (Backups, Destinations, Logs, Restore) for state they had no
        // reason to re-read. The save still happens atomically under
        // the gate; only the broadcast is skipped.
        // Read the per-source size hints the progress service captured
        // from BACKUP_STATS lines BEFORE the run-finished purge clears
        // them. We persist them on the Backup so the BackupCard's
        // sources column can render "C:\Jts (1.2 GB)" inline next to
        // each path even after the run is long done. The user reported:
        // "in the sources column, once a successful backup has been
        // completed, after each Path it should indicate the size of
        // the source within parentheses" — surfaced from the figure
        // duplicacy reports at completion.
        //
        // Only mutate on Success / Warning runs: a Failed/Skipped run
        // either never finished a backup phase or never reached
        // BACKUP_STATS, so any size hint would be stale or partial.
        Dictionary<string, long>? freshSizes = null;
        if (record.Status is BackupRunStatus.Success or BackupRunStatus.Warning)
        {
            try
            {
                var snap = _progress.Snapshot(backup.Id);
                if (snap is not null && snap.SourceWeightBytes.Count > 0)
                {
                    freshSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (path, bytes) in snap.SourceWeightBytes)
                        if (bytes > 0) freshSizes[path] = bytes;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Reading per-source size snapshot threw for {Name}", backup.Name);
            }
        }

        _config.UpdateQuiet(cfg =>
        {
            var b = cfg.Backups.FirstOrDefault(x => x.Id == backup.Id);
            if (b is null) return;
            b.LastRunStartUtc = record.StartedUtc;
            b.LastRunEndUtc = record.EndedUtc;
            b.LastRunStatus = record.Status;
            b.LastRunSummary = record.Summary;
            if (freshSizes is not null)
            {
                foreach (var (path, bytes) in freshSizes)
                    b.SourceLastSizeBytes[path] = bytes;
                // Drop entries for paths the user has since removed
                // from the backup so the card never shows phantom
                // sizes for paths that aren't in the source list.
                var live = new HashSet<string>(b.SourcePaths, StringComparer.OrdinalIgnoreCase);
                var stale = b.SourceLastSizeBytes.Keys.Where(k => !live.Contains(k)).ToList();
                foreach (var k in stale) b.SourceLastSizeBytes.Remove(k);
            }
        });
    }

    internal static string BuildSummary(RunRecord r, string? orchestratorCancelReason = null)
    {
        var d = (r.EndedUtc ?? DateTime.UtcNow) - r.StartedUtc;
        var dur = DuplicacyRunner.HumanDuration(d);
        if (r.Status == BackupRunStatus.Skipped)
        {
            // Pick the most informative cell summary as the run-level
            // skip reason — every Skipped cell carries a precise reason
            // (manual stop, metered network, drive unplugged, etc.) set
            // by the cell loop. Falls back to the orchestrator's recorded
            // CancelReason when no cell ran (early Stop, metered-watchdog
            // before the first cell). Without that fallback an early
            // Stop produced a generic "Skipped in {dur}" line that hid
            // which actor cancelled the run.
            var cellReason = r.Cells
                .Where(c => c.Status == BackupRunStatus.Skipped && !string.IsNullOrWhiteSpace(c.Summary))
                .Select(c => c.Summary)
                .FirstOrDefault();
            var reason = !string.IsNullOrWhiteSpace(cellReason)
                ? cellReason
                : (string.IsNullOrWhiteSpace(orchestratorCancelReason) ? null : orchestratorCancelReason);
            if (string.IsNullOrEmpty(reason))
                return $"Skipped after {dur}";

            // Fold redundant phrasing so the row doesn't read
            // "Skipped · Manually skipped · skipped after 5s" — the
            // user reported three different "skipped" tokens stacking
            // up. Strip a leading "Manually skipped"/"Skipped"/etc.
            // and keep the remaining context (e.g. the cause) before
            // the duration. Examples:
            //   "Manually skipped"          → "Manually skipped after 5s"
            //   "Skipped — drive unplugged" → "Skipped — drive unplugged · 5s"
            //   "Network became metered"    → "Network became metered · skipped after 5s"
            // We only collapse when the reason already contains
            // "skipped" (case-insensitive) — otherwise the cell-level
            // reason has its own vocabulary and we still need to spell
            // out that the run was skipped.
            var trimmedReason = reason.TrimEnd('.', ' ', '·');
            var saysSkipped = trimmedReason.Contains("skipped", StringComparison.OrdinalIgnoreCase);
            return saysSkipped
                ? $"{trimmedReason} after {dur}"
                : $"{trimmedReason} · skipped after {dur}";
        }
        var parts = new System.Collections.Generic.List<string>();
        if (r.BytesUploaded > 0) parts.Add($"{DuplicacyRunner.HumanSize(r.BytesUploaded)} uploaded");
        if (r.FilesNew + r.FilesModified + r.FilesRemoved > 0)
            parts.Add($"{r.FilesNew} new / {r.FilesModified} mod / {r.FilesRemoved} rem");
        if (r.Errors.Count > 0)    parts.Add($"{r.Errors.Count} errors");
        if (r.Warnings.Count > 0)  parts.Add($"{r.Warnings.Count} warnings");
        // No-op success runs (everything already up-to-date — no
        // bytes uploaded, no file changes) used to surface as
        // "in 6m 10s", which the BackupCard rendered as
        // "LAST in 6m 10s" — reads confusingly since "LAST" is the
        // row label not the verb. The user explicitly asked for
        // "LAST Completed in 4m 10s" instead. Skipped runs already
        // get their own "Manually skipped after Xs" phrasing in the
        // branch above; here we cover the success-no-op case
        // symmetrically. Runs that DID upload or change files keep
        // the bare "in {dur}" suffix because the leading parts
        // already describe what happened.
        parts.Add(parts.Count == 0 ? $"Completed in {dur}" : $"in {dur}");
        return string.Join(" · ", parts);
    }

    private static string BuildEmailBody(Backup b, RunRecord r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Backup:   {b.Name}");
        if (b.SourcePaths.Count == 1)
        {
            sb.AppendLine($"Source:   {b.SourcePaths[0]}");
        }
        else
        {
            sb.AppendLine($"Sources:  {b.SourcePaths.Count}");
            foreach (var s in b.SourcePaths) sb.AppendLine($"            {s}");
        }
        sb.AppendLine($"Machine:  {Environment.MachineName}");
        sb.AppendLine($"Started:  {r.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (r.EndedUtc is DateTime e) sb.AppendLine($"Ended:    {e.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Status:   {r.Status}");
        sb.AppendLine($"Summary:  {r.Summary}");

        // Per-cell breakdown — for a multi-source or multi-target
        // backup this is where the user actually learns what happened.
        if (r.Cells.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Details by source × destination:");
            foreach (var c in r.Cells)
            {
                // Path.GetFileName returns "" for a drive root like
                // "C:\" (TrimEnd → "C:" → GetFileName("C:") → ""). Use
                // the trimmed full path as the fallback so the email
                // renders "[Status   ] C: → Dest" instead of
                // "[Status   ]   → Dest" — the previous `??` over the
                // non-nullable GetFileName return was dead code.
                var trimmedSrc = c.SourcePath.TrimEnd('\\', '/');
                var srcLabel = Path.GetFileName(trimmedSrc);
                if (string.IsNullOrEmpty(srcLabel)) srcLabel = trimmedSrc;
                if (string.IsNullOrEmpty(srcLabel)) srcLabel = c.SourcePath;
                var line = $"  [{c.Status,-8}] {srcLabel} → {c.DestinationName}";
                if (c.AttemptsMade > 1) line += $" (after {c.AttemptsMade} attempts)";
                if (!string.IsNullOrWhiteSpace(c.Summary)) line += $" — {c.Summary}";
                sb.AppendLine(line);
                if (!string.IsNullOrWhiteSpace(c.LastError))
                    sb.AppendLine($"             error: {c.LastError}");
            }
        }

        if (r.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Errors:");
            foreach (var l in r.Errors) sb.AppendLine("  " + l);
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var l in r.Warnings) sb.AppendLine("  " + l);
        }
        sb.AppendLine();
        sb.AppendLine($"Full log: {r.LogPath}");
        return sb.ToString();
    }
}
