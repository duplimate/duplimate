using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Models;
using Duplimate.Services;

namespace Duplimate.ViewModels;

public sealed partial class LogsViewModel : ViewModelBase, IDisposable
{
    private static Serilog.ILogger _log => AppLogger.For<LogsViewModel>();

    public ObservableCollection<string> BackupNames { get; } = new();
    public ObservableCollection<RunRecord> History { get; } = new();

    /// <summary>True iff there are no configured backups at all — the
    /// LogsView uses this to render an empty-state callout instead of
    /// the (otherwise empty) two ComboBoxes + two empty surface panes.
    /// </summary>
    public bool HasNoBackups => BackupNames.Count == 0;
    /// <summary>True iff a backup is selected but it has zero history
    /// entries (never run, or all history pruned). The dropdown
    /// already shows "Pick a run" placeholder, but the user benefits
    /// from a more explicit "this backup hasn't run yet" hint.
    /// </summary>
    public bool HasNoHistoryForSelected =>
        BackupNames.Count > 0 && SelectedBackup is not null && History.Count == 0;

    [ObservableProperty] private string? _selectedBackup;
    [ObservableProperty] private RunRecord? _selectedRun;
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _liveTail = "";
    [ObservableProperty] private bool _isLiveAttached;

    /// <summary>Raw, unfiltered text of the currently-loaded past-run
    /// log file. The bound <see cref="LogText"/> is materialized from
    /// this via <see cref="VerboseLogFilter.FilterForDisplay"/>, so
    /// toggling the verbose setting can re-render the past run
    /// without re-reading the file. Empty when nothing is loaded
    /// (live mode, empty selection, or load error).</summary>
    private string _logTextRaw = "";

    /// <summary>True when the unified log pane should display the live
    /// tail buffer rather than a stored per-run log file. We surface
    /// Live in two cases:
    ///   • the synthetic in-flight RunRecord is selected (there is no
    ///     log file yet — the run is still writing it), and
    ///   • no run is selected at all (the user picked a backup but
    ///     hasn't drilled into a specific past run, so the "default"
    ///     view is the live stream — same defaulting as the prior
    ///     side-by-side layout where the Live pane was always
    ///     visible).
    /// </summary>
    public bool IsShowingLive =>
        SelectedRun is null
        || SelectedRun is { EndedUtc: null, Status: BackupRunStatus.Running };

    /// <summary>Header label for the unified log pane. "Live" while
    /// the live buffer is what's on screen, otherwise a friendly
    /// timestamp + status for the selected past run.</summary>
    public string DisplayLabel
    {
        get
        {
            var r = SelectedRun;
            if (r is null) return "Live";
            if (IsShowingLive) return $"Live — {r.BackupName}";
            var stamp = r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var status = r.Status switch
            {
                BackupRunStatus.Success => "Success",
                BackupRunStatus.Warning => "Completed with warnings",
                BackupRunStatus.Failed  => "Failed",
                BackupRunStatus.Skipped => "Skipped",
                BackupRunStatus.Running => "Running",
                _                       => r.Status.ToString(),
            };
            // Kind label so the title bar tells the user whether
            // they're reading a backup, a restore, or a test-restore
            // log — same vocabulary as the dropdown row prefix.
            var kindLabel = r.Kind switch
            {
                RunKind.Restore     => "Restore",
                RunKind.TestRestore => "Test restore",
                _                   => "Run",
            };
            return $"{kindLabel} from {stamp} — {status}";
        }
    }

    /// <summary>The text actually shown in the unified log pane: the
    /// live tail while in live mode, otherwise the loaded per-run log
    /// text.</summary>
    public string DisplayText => IsShowingLive ? LiveTail : LogText;

    /// <summary>Coalesces a flurry of Config.Changed firings into a
    /// single Refresh on the UI thread.</summary>
    private readonly UiThreadDebouncer _refreshDebouncer;

    /// <summary>
    /// Programmatic selector used by the click-to-logs notification
    /// action. Looks up the backup name by id and selects the most
    /// recent run; if the backup or run can't be resolved, no-ops
    /// rather than throw (the toast was opportunistic).
    /// </summary>
    public void SelectBackupById(string backupId)
    {
        var b = ServiceLocator.Config.Current.Backups.FirstOrDefault(x => x.Id == backupId);
        if (b is null) return;
        if (!BackupNames.Contains(b.Name)) Refresh();
        SelectedBackup = b.Name;
        SelectedRun = History.FirstOrDefault();
    }

    public LogsViewModel()
    {
        _refreshDebouncer = new UiThreadDebouncer(Refresh);
        Refresh();
        ServiceLocator.Config.Changed += OnConfigChanged;

        // Live-attach to the runner so any currently-running backup streams here.
        _flushCts = new System.Threading.CancellationTokenSource();
        ServiceLocator.Runner.LineWritten += OnLineWritten;
        IsLiveAttached = true;

        // Auto-refresh the history pane when a run ends — without this
        // the user would see a fresh log file appear in the live tail
        // but the History dropdown would still show the previous run as
        // most-recent until they navigated away and back.
        ServiceLocator.Orchestrator.RunEnded += OnOrchestratorRunEnded;
        // RunStarted: surface the in-flight run as a synthetic entry at
        // the top of the history dropdown so the user can find a live
        // run from the LogsView without waiting for it to finish. The
        // entry has no log path yet (the runner writes to a per-cell log
        // not the per-run history file); selecting it leaves "Selected
        // run" empty — the live log lives in the LiveTail pane.
        ServiceLocator.Orchestrator.RunStarted += OnOrchestratorRunStarted;

        // Non-backup live activity (e.g. the Restore wizard entering
        // Progress) — flip into Live mode so the streaming output is
        // visible regardless of which past run was selected. Without
        // this hook, navigating to Activity & Logs during a restore
        // showed the previously-selected past backup run instead of
        // the live restore output.
        AppNavigator.LiveActivityStarted += OnLiveActivityStarted;

        // RunRecord persisted (Backup / Restore / Test-restore) →
        // refresh the dropdown so the new entry appears without the
        // user having to flip selection. Earlier the user reported
        // "I had to select another backup and then again for the
        // lists to refresh" after a restore — root cause: the only
        // signals the dropdown listened to were Orchestrator's
        // RunStarted / RunEnded, which the restore path doesn't
        // emit. LogStore.RunAppended fires for every kind.
        ServiceLocator.Logs.RunAppended += OnLogStoreRunAppended;

        // In-flight Restore + Test-restore activity. The orchestrator
        // already exposes its own _runningRuns map for backups; this
        // is the symmetrical signal for the other two RunKinds — fired
        // by RunActivityTracker.Register/Unregister so the dropdown
        // can show / hide a synthetic Running entry the moment a
        // restore wizard or test-restore drill begins/ends.
        ServiceLocator.Activity.RunningChanged += OnActivityRunningChanged;
    }

    private void OnActivityRunningChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // A new in-flight run appeared (or finished) — re-load
            // the dropdown so the synthetic Running entry shows up
            // (or, if it just unregistered, gets cleaned up before
            // the persisted record lands via OnLogStoreRunAppended).
            ReloadHistoryForSelected();
            // If the user is on the same backup the activity belongs
            // to AND nothing is selected, auto-pin to the freshly-
            // appeared Running entry so they see it streaming
            // instead of staring at "Live" with no row highlighted.
            if (SelectedRun is null
                && History.FirstOrDefault() is { Status: BackupRunStatus.Running, EndedUtc: null } running)
            {
                SelectedRun = running;
            }
        });
    }

    private void OnLogStoreRunAppended(RunRecord record)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Auto-flip SelectedBackup to whichever backup the new
            // record belongs to and select the freshly-persisted
            // entry — same UX pattern as backup runs (the user
            // wants to see what just happened, not have to hunt
            // for it).
            if (!string.Equals(SelectedBackup, record.BackupName, StringComparison.Ordinal))
            {
                SelectedBackup = record.BackupName;
                // OnSelectedBackupChanged will reload History and
                // pick the latest entry as SelectedRun — which IS
                // the one we just appended.
            }
            else
            {
                ReloadHistoryForSelected();
                // Move focus to the new entry (matched by
                // StartedUtc — record IDs are guids and the file
                // round-trip can produce a different instance).
                // Belt-and-braces: clear SelectedRun first so the
                // assignment ALWAYS fires OnSelectedRunChanged, even
                // when the matching History entry happens to be the
                // same reference as the prior selection (synthetic
                // Running). Without this the terminal kept the
                // previous run's content on screen.
                if (History.FirstOrDefault(r => r.StartedUtc == record.StartedUtc) is { } match)
                {
                    SelectedRun = null;
                    SelectedRun = match;
                }
            }
        });
    }

    private void OnLiveActivityStarted(string? backupName)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Reset the live-tail buffer for the same reason
            // OnOrchestratorRunStarted does — see ResetLiveTail's
            // commentary. Restore wizard runs and test-restore drills
            // also need a fresh terminal pane so the new activity's
            // lines aren't blended with whatever was streaming before.
            ResetLiveTail();

            // If the caller knows which backup the activity belongs
            // to (Restore + Test restore both pass it), pivot the
            // dropdown to that backup. Once the run ends and the
            // persisted RunRecord lands in that backup's history,
            // the user is already viewing it and sees the new entry
            // appear without having to switch.
            if (!string.IsNullOrEmpty(backupName)
                && BackupNames.Contains(backupName)
                && !string.Equals(SelectedBackup, backupName, StringComparison.Ordinal))
            {
                SelectedBackup = backupName;
                // Setter triggered ReloadHistoryForSelected which
                // already merged tracker entries — fall through to
                // pin to the synthetic Running row.
            }
            else
            {
                // Same backup already selected: refresh the dropdown
                // so the just-registered tracker entry shows up.
                ReloadHistoryForSelected();
            }
            // Pin SelectedRun to the synthetic Running entry so the
            // user sees the run in the RUN dropdown (not just in
            // Live with no row highlighted) — the user reported
            // "Doing a test restore shows in the Live view instead
            // of as a Running test restore run (in RUN)." Falls back
            // to clearing SelectedRun (Live mode) when there's no
            // matching synthetic entry, e.g. when the activity
            // signal arrived without backupName context.
            var synthetic = History.FirstOrDefault(r =>
                r is { Status: BackupRunStatus.Running, EndedUtc: null });
            SelectedRun = synthetic;
            // SelectedRun setter already nudges DisplayText/IsShowingLive.
            // When synthetic is null (no in-flight registered yet),
            // SelectedRun = null still flips IsShowingLive → Live.
        });
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _refreshDebouncer.Schedule();
        // Verbose may have just toggled — re-materialize the visible
        // surfaces from their raw backing buffers so the change
        // applies retroactively. Cheap: a single FilterForDisplay
        // pass on each buffer (≤400 KB each).
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LiveTail = VerboseLogFilter.FilterForDisplay(_liveBuffer.ToString());
            if (!string.IsNullOrEmpty(_logTextRaw))
                LogText = VerboseLogFilter.FilterForDisplay(_logTextRaw);
            OnPropertyChanged(nameof(DisplayText));
        });
    }

    public void Dispose()
    {
        ServiceLocator.Config.Changed -= OnConfigChanged;
        ServiceLocator.Runner.LineWritten -= OnLineWritten;
        ServiceLocator.Orchestrator.RunStarted -= OnOrchestratorRunStarted;
        ServiceLocator.Orchestrator.RunEnded -= OnOrchestratorRunEnded;
        ServiceLocator.Logs.RunAppended -= OnLogStoreRunAppended;
        ServiceLocator.Activity.RunningChanged -= OnActivityRunningChanged;
        AppNavigator.LiveActivityStarted -= OnLiveActivityStarted;
        IsLiveAttached = false;
        // Cancel any pending flush Task.Delay so its continuation
        // doesn't run after Dispose and mutate LiveTail on a defunct
        // VM. Then null out _flushCts so OnLineWritten short-circuits
        // for any line that arrives between this point and the runner
        // unsubscribe completing.
        var cts = System.Threading.Interlocked.Exchange(ref _flushCts, null);
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
        _refreshDebouncer.Dispose();
    }

    private void OnOrchestratorRunStarted(string backupId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var b = ServiceLocator.Config.Current.Backups.FirstOrDefault(x => x.Id == backupId);
            if (b is null) return;

            // Reset the live-tail buffer so the new run's terminal
            // pane starts blank — without this, lines from the
            // PREVIOUS run (a backup, a restore, or a test-restore
            // drill that just finished) stayed pinned on screen and
            // got blended with the new run's first lines. The user
            // reported: "I did a test restore, it appeared in the
            // logs. But then I started a backup of another backup,
            // and in the Running entry it seemed to show the lines
            // of the former test restore!"
            ResetLiveTail();

            // Be aggressive: if the user has the Logs tab open, they
            // almost always want to see the run that just started.
            // Earlier we only flipped into Live mode when
            // SelectedBackup happened to match — silent no-op
            // otherwise. The user reported "the terminal still
            // doesn't show any logs when running a backup" with the
            // Hello backup; their default-selected backup was
            // alphabetically first (a different one), so this hook
            // never fired. Switch SelectedBackup to the running one
            // unconditionally — the user can still pick a past run
            // afterwards, and a scheduled/background run firing
            // while they're reading old logs is the rare case.
            var prevName = SelectedBackup;
            if (!string.Equals(prevName, b.Name, StringComparison.Ordinal))
                SelectedBackup = b.Name;
            else
                ReloadHistoryForSelected();

            // Pin SelectedRun to the new in-flight Running entry so
            // IsShowingLive flips true and DisplayText pivots to
            // LiveTail.
            if (History.Count > 0
                && History[0] is { Status: BackupRunStatus.Running, EndedUtc: null } running)
            {
                SelectedRun = running;
            }
        });
    }

    /// <summary>Clear the raw live-tail buffer AND any pending
    /// uncommitted lines, then re-materialize the bound LiveTail. Used
    /// at the start of every fresh run (backup / restore /
    /// test-restore) so the new run begins with a clean terminal pane.
    /// Also drains <see cref="_pendingLines"/> so a chunk from the
    /// PREVIOUS run that was sitting in the 50ms debounce window
    /// doesn't land in the FRESH buffer milliseconds later.</summary>
    private void ResetLiveTail()
    {
        // Cancel + recreate the flush CTS so any in-flight Task.Delay
        // continuation from the PRIOR run drops out before it can
        // post a chunk into our freshly-cleared buffer. Without this,
        // an OnLineWritten landing in the 50ms window between
        // ResetLiveTail and the new run's first line could carry
        // over a tail line from the prior run. Tokens from the old
        // CTS see ct.IsCancellationRequested at the dispatcher post
        // site and bail.
        var oldCts = System.Threading.Interlocked.Exchange(ref _flushCts, new System.Threading.CancellationTokenSource());
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch { }

        lock (_pendingLock)
        {
            _pendingLines.Clear();
            // Reset the schedule flag too — a stale "true" left by
            // the cancelled flush would prevent the FIRST line of
            // the new run from scheduling its own debounce.
            _flushScheduled = false;
        }
        _liveBuffer.Clear();
        LiveTail = "";
        OnPropertyChanged(nameof(DisplayText));
    }

    private void OnOrchestratorRunEnded(string backupId, RunRecord record)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Switch to the just-finished backup unconditionally so
            // the user navigating to the Logs tab AFTER a run sees
            // its persisted log without having to manually pick the
            // backup. The user reported: "I have to reselect the
            // run." Symmetrical with OnOrchestratorRunStarted.
            if (!string.Equals(SelectedBackup, record.BackupName, System.StringComparison.Ordinal))
            {
                SelectedBackup = record.BackupName;
                // Setter triggers OnSelectedBackupChanged → reloads
                // history → SelectedRun = History.FirstOrDefault()
                // (the just-persisted entry).
            }
            else
            {
                ReloadHistoryForSelected();
                // Move the selection to the just-finished run so
                // its persisted log loads (otherwise SelectedRun
                // stays on the synthetic Running entry which has
                // no LogPath). Same null-then-set pattern as
                // OnLogStoreRunAppended above — see commentary
                // there for why a force-fire is required.
                if (History.FirstOrDefault(r => r.StartedUtc == record.StartedUtc) is { } finished)
                {
                    SelectedRun = null;
                    SelectedRun = finished;
                }
            }
        });
    }

    private void ReloadHistoryForSelected()
    {
        if (string.IsNullOrEmpty(SelectedBackup)) return;
        var previouslySelectedRunStarted = SelectedRun?.StartedUtc;
        History.Clear();
        // Look up by Backup.Id (not Name) so a renamed backup still
        // shows its complete run timeline. Per-name history files are
        // a legacy of LogStore's filesystem layout — runs persisted
        // before a rename live under the OLD name's folder. The id-
        // keyed loader stitches every history file together into a
        // single id-filtered timeline.
        var recs = LoadAllRunsForCurrentBackup().OrderByDescending(r => r.StartedUtc);
        foreach (var r in recs) History.Add(r);
        // Preserve the user's selection if it still exists; otherwise
        // jump to the freshest entry (typical "I just ran a backup,
        // show me what just happened").
        SelectedRun = previouslySelectedRunStarted is DateTime t
            ? History.FirstOrDefault(r => r.StartedUtc == t) ?? History.FirstOrDefault()
            : History.FirstOrDefault();
    }

    /// <summary>Resolve the SelectedBackup name to its Backup.Id and
    /// load every run from every history file matching that id —
    /// covers the rename-orphan case where post-rename runs landed in
    /// a different name's folder. Prepends a synthetic "Running…"
    /// entry when the orchestrator reports an in-flight run for this
    /// backup, so the user can see a live run in the dropdown instead
    /// of waiting for it to finish before it appears.</summary>
    private IEnumerable<RunRecord> LoadAllRunsForCurrentBackup()
    {
        if (string.IsNullOrEmpty(SelectedBackup)) return Array.Empty<RunRecord>();
        var b = ServiceLocator.Config.Current.Backups
            .FirstOrDefault(x => string.Equals(x.Name, SelectedBackup, StringComparison.Ordinal));

        IReadOnlyList<RunRecord> persisted;
        if (b is null)
        {
            persisted = ServiceLocator.Logs.LoadHistory(SelectedBackup);
        }
        else
        {
            var byId = ServiceLocator.Logs.LoadHistoryByBackupId(b.Id);
            persisted = byId.Count > 0 ? byId : ServiceLocator.Logs.LoadHistory(SelectedBackup);
        }

        // In-flight run synthesis. We surface the tracker's OWN
        // RunRecord instance (not a fresh copy) so the dropdown's
        // SelectedItem keeps matching across reloads — earlier each
        // reload created a new synthetic instance, which silently broke
        // selection when RunStarted/RunEnded triggered a reload while
        // the user had the running entry selected (the SelectedRun
        // reference no longer existed in History → ComboBox went blank).
        //
        // Two registries to merge:
        //   • Orchestrator._runningRuns (Backup runs)
        //   • RunActivityTracker (Restore + Test-restore)
        // Combined and de-duped against `persisted` by Id (same Id is
        // re-used across the synth → persisted swap so the row stays
        // stable when the user clicks it mid-run).
        if (b is null) return persisted;

        var inFlight = new List<RunRecord>(2);
        if (ServiceLocator.Orchestrator.TryGetRunningRecord(b.Id, out var backupLive))
            inFlight.Add(backupLive);
        foreach (var r in ServiceLocator.Activity.GetRunningForBackup(b.Id))
            inFlight.Add(r);

        if (inFlight.Count == 0) return persisted;

        // Drop any in-flight entry whose Id (or fallback StartedUtc)
        // already appears in persisted — e.g. a recently-finished run
        // still in the tracker's grace window between Unregister and
        // the AppendRun callback would otherwise show twice.
        var persistedIds = new HashSet<string>(persisted.Where(r => !string.IsNullOrEmpty(r.Id)).Select(r => r.Id));
        var deduped = inFlight
            .Where(r => string.IsNullOrEmpty(r.Id) || !persistedIds.Contains(r.Id))
            .Where(r => !persisted.Any(p => p.StartedUtc == r.StartedUtc))
            .ToList();
        if (deduped.Count == 0) return persisted;
        return deduped.Concat(persisted);
    }

    // Batched live-tail updates. Two concerns drove this design:
    //   1. A verbose backup fires LineWritten 5-10k times per second.
    //      One Dispatcher.Post per line saturates the UI queue.
    //   2. The previous design did `LiveTail += chunk` on every flush,
    //      which is O(n) string copy in the bound length — for a long
    //      run that's hundreds of MB churned through Gen2 GC.
    // The new design holds the live tail in a single StringBuilder
    // (the binding-visible LiveTail string is rebuilt from it only at
    // flush time, when the value actually changes anyway). The buffer
    // is hard-capped at LiveTailHardCap chars; on overflow we keep the
    // bottom half via Remove(0, n) — a single in-place shift, no new
    // allocation for the chars that survive.
    private const int LiveTailHardCap = 400_000;
    private const int LiveTailKeepAfterTrim = 200_000;
    // 50ms balances "verbose-run UI doesn't drown in property-change
    // notifications" against "user sees init lines BEFORE a fast-fail
    // run ends". Earlier value of 150ms made fail-fast init errors
    // (e.g. STORAGE_NOT_CONFIGURED — finishes in <500ms) effectively
    // post-run only — the user reported "terminal didn't show any
    // logs while running, only at the end".
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);
    private readonly System.Text.StringBuilder _liveBuffer = new(capacity: 64 * 1024);
    private readonly System.Text.StringBuilder _pendingLines = new();
    private readonly object _pendingLock = new();
    private bool _flushScheduled;
    private System.Threading.CancellationTokenSource? _flushCts;

    private void OnLineWritten(string line)
    {
        // Snapshot the CTS reference AND its Token in one atomic
        // observation. The Token is a struct (just a reference to the
        // CTS internals), so capturing it now avoids a later
        // `cts.Token` dereference that would throw
        // ObjectDisposedException if Dispose ran between the null-
        // check and the Token property read. Token captured here
        // continues to function even after the underlying CTS is
        // disposed (token.IsCancellationRequested still works on most
        // .NET versions; the catch in the Posted action handles the
        // edge case).
        var cts = System.Threading.Volatile.Read(ref _flushCts);
        if (cts is null) return;
        CancellationToken ct;
        try { ct = cts.Token; }
        catch (ObjectDisposedException) { return; }

        bool needSchedule;
        lock (_pendingLock)
        {
            _pendingLines.AppendLine(line);
            needSchedule = !_flushScheduled;
            if (needSchedule) _flushScheduled = true;
        }
        if (!needSchedule) return;
        _ = System.Threading.Tasks.Task.Delay(FlushInterval, ct).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            string chunk;
            lock (_pendingLock)
            {
                chunk = _pendingLines.ToString();
                _pendingLines.Clear();
                _flushScheduled = false;
            }
            if (chunk.Length == 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Bail if Dispose has run since the flush was
                // scheduled. The _flushCts field is set to null in
                // Dispose; checking it here covers BOTH the case
                // where the captured ct is disposed (would throw on
                // IsCancellationRequested) AND the case where the VM
                // has moved on but ct is still valid. Without this
                // guard, a post-Dispose flush would mutate _liveBuffer
                // and fire OnPropertyChanged on a defunct VM —
                // wasted work and a faint leak risk.
                if (System.Threading.Volatile.Read(ref _flushCts) is null) return;
                bool cancelled;
                try { cancelled = ct.IsCancellationRequested; }
                catch (ObjectDisposedException) { return; }
                if (cancelled) return;
                _liveBuffer.Append(chunk);
                if (_liveBuffer.Length > LiveTailHardCap)
                {
                    var drop = _liveBuffer.Length - LiveTailKeepAfterTrim;
                    _liveBuffer.Remove(0, drop);
                }
                // The buffer holds the RAW duplicacy stream — verbose
                // is a display filter only, so the buffer keeps every
                // line for "Copy live" and the eventual persistence
                // path. The bound LiveTail strips DEBUG/TRACE when
                // verbose is off (toggling the setting re-materializes
                // via OnConfigChanged).
                LiveTail = VerboseLogFilter.FilterForDisplay(_liveBuffer.ToString());
                // Always notify DisplayText. Earlier we gated this on
                // IsShowingLive, but the DisplayText getter itself
                // pivots on IsShowingLive at read time — gating the
                // notification just produced races where a user toggling
                // selection between live and a finished run wouldn't
                // see the live pane refresh until the next line landed.
                OnPropertyChanged(nameof(DisplayText));
            });
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    public void Refresh()
    {
        BackupNames.Clear();
        foreach (var b in ServiceLocator.Config.Current.Backups.OrderBy(b => b.Name))
            BackupNames.Add(b.Name);
        // Synthetic [Destinations] bucket: surface whenever the
        // user has ever deleted a destination (StorageCleaner writes
        // those runs under this bucket name). Sentinel sorts to the
        // top with leading "[" so it's never visually mixed with
        // real backup names. Detection is filesystem-level — we
        // don't track the bucket in config, so it survives across
        // app sessions naturally.
        var destBucketDir = AppPaths.LogDirForBackup(StorageCleaner.DestinationsBucketName);
        if (System.IO.Directory.Exists(destBucketDir)
            && System.IO.File.Exists(System.IO.Path.Combine(destBucketDir, "history.json")))
        {
            BackupNames.Insert(0, StorageCleaner.DestinationsBucketName);
        }
        if (SelectedBackup is null && BackupNames.Count > 0)
            SelectedBackup = BackupNames[0];
        OnPropertyChanged(nameof(HasNoBackups));
        OnPropertyChanged(nameof(HasNoHistoryForSelected));
    }

    partial void OnSelectedBackupChanged(string? value)
    {
        History.Clear();
        if (string.IsNullOrEmpty(value))
        {
            OnPropertyChanged(nameof(HasNoHistoryForSelected));
            return;
        }
        // Use the same id-keyed lookup as ReloadHistoryForSelected so a
        // renamed backup gets its complete history. See
        // LoadAllRunsForCurrentBackup for the rationale.
        var recs = LoadAllRunsForCurrentBackup().OrderByDescending(r => r.StartedUtc);
        foreach (var r in recs) History.Add(r);
        SelectedRun = History.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoHistoryForSelected));
    }

    /// <summary>Programmatic command bound to the LogsView empty-state
    /// "Go to Backups" button. Routes the user to the Backups tab via
    /// the shared <see cref="AppNavigator"/> so the empty state isn't a
    /// dead-end.</summary>
    [RelayCommand]
    private void GoToBackups() => AppNavigator.Go(NavItem.Backups);

    /// <summary>Hard cap for in-memory log display. A single backup run
    /// can produce 100 MB+ of stdout — loading that into a TextBox
    /// freezes the UI thread for seconds and blows up GC pressure.
    /// 4 MB is enough to debug almost anything; if the user needs the
    /// full file, the path is right there in the row record.</summary>
    private const long LogTailBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Monotonically-increasing tag for the in-flight log-file load.
    /// Each <see cref="OnSelectedRunChanged"/> bumps it; the async
    /// continuation captures its own copy and only commits its read
    /// result when the captured tag still matches the current
    /// generation. Without this, two clicks in quick succession
    /// (A → B) where A's read is slower can have A complete AFTER
    /// B's started — both have the same LogPath if the user clicked
    /// back to A meanwhile, but B's read result would be silently
    /// overwritten by A's (probably-identical) text. The
    /// LogPath-equality guard at the commit site is necessary but
    /// not sufficient; the generation tag is the strict ordering.
    /// </summary>
    private int _logLoadGen;

    partial void OnSelectedRunChanged(RunRecord? value)
    {
        // Notify the dependent computed properties so the unified log
        // pane swaps between LiveTail and LogText cleanly.
        OnPropertyChanged(nameof(IsShowingLive));
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(DisplayText));

        // Bump the load generation up-front so any in-flight async
        // load from a PRIOR SelectedRun is invalidated and won't
        // commit its (now-stale) text. Even paths that early-return
        // below need this — a click that lands us on the
        // "Running" / "no LogPath" branch must still cancel any
        // pending read against the previously-selected run.
        var thisGen = System.Threading.Interlocked.Increment(ref _logLoadGen);

        // In-flight run: nothing to load — DisplayText pivots to
        // LiveTail via IsShowingLive, so leave LogText alone.
        if (value is not null && value.EndedUtc is null
            && value.Status == BackupRunStatus.Running)
        {
            return;
        }
        if (value is null || string.IsNullOrEmpty(value.LogPath))
        {
            _logTextRaw = "";
            LogText = "";
            OnPropertyChanged(nameof(DisplayText));
            return;
        }
        if (!File.Exists(value.LogPath))
        {
            // Per-cell logs from old runs may have been pruned. Surface
            // a plain message rather than rendering as empty so the
            // user knows the run-row is real but the file is gone.
            // The "missing file" notice is a UI message, not a
            // duplicacy log line, so it doesn't go through the
            // verbose filter.
            _logTextRaw = "";
            LogText =
                "[The log file for this run is no longer on disk.]\n\n" +
                $"Expected at: {value.LogPath}\n" +
                "Older logs are pruned automatically by the retention policy.";
            OnPropertyChanged(nameof(DisplayText));
            return;
        }

        // Show "loading…" placeholder immediately so the user knows we
        // got the click; the actual read runs off the UI thread.
        _logTextRaw = "";
        LogText = "Loading…";
        var path = value.LogPath;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            // Outer try/catch so anything past the read (Dispatcher.Post
            // during app shutdown, OOM on the concat) can't escape into
            // the unobserved-task path and crash the process.
            try
            {
                string text;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > LogTailBytes)
                    {
                        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Seek(-LogTailBytes, SeekOrigin.End);

                        // The seek may have landed mid-character on a multi-byte
                        // UTF-8 sequence. Skip forward up to 3 bytes until we
                        // hit a valid UTF-8 leading byte (top bit clear, OR
                        // top two bits = 11). That guarantees the StreamReader
                        // doesn't see a half-character header.
                        for (int i = 0; i < 3; i++)
                        {
                            var b = fs.ReadByte();
                            if (b < 0) break;
                            if ((b & 0x80) == 0 || (b & 0xC0) == 0xC0)
                            {
                                fs.Position--;
                                break;
                            }
                        }

                        using var sr = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8);
                        var tail = sr.ReadToEnd();
                        var truncated = info.Length - LogTailBytes;
                        text = $"[log truncated — showing last {LogTailBytes / (1024 * 1024)} MB of ~{info.Length / (1024 * 1024)} MB; {truncated:N0} bytes hidden at the top]\n…\n" + tail;
                    }
                    else
                    {
                        text = File.ReadAllText(path);
                    }
                }
                catch (System.Exception ex)
                {
                    _log.Warning(ex, "Failed to read run log file {Path}", path);
                    text = $"[could not read log file: {ex.Message}]";
                }
                try
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // Strict ordering via the generation tag: ONLY
                        // commit if WE are the most-recent load. The
                        // older LogPath-equality check could pass for
                        // a stale read if the user clicked A → B → A
                        // (same LogPath A both times) and the first
                        // read of A completed last. The generation
                        // tag bumps on every assignment so out-of-
                        // order completions can't clobber.
                        if (_logLoadGen != thisGen) return;
                        if (SelectedRun?.LogPath != path) return;
                        // Persisted log files are RAW (every persistence
                        // path writes the unfiltered duplicacy stream).
                        // Stash the raw text so a later verbose toggle
                        // can re-materialize without re-reading the
                        // file, and assign the filtered view to
                        // LogText.
                        _logTextRaw = text;
                        LogText = VerboseLogFilter.FilterForDisplay(text);
                        OnPropertyChanged(nameof(DisplayText));
                    });
                }
                catch (System.Exception ex)
                {
                    // Dispatcher rejected the post (e.g., app is shutting
                    // down). Nothing useful to do — swallow.
                    _log.Debug(ex, "Dispatcher.Post rejected log-text update for {Path}", path);
                }
            }
            catch (System.Exception ex)
            {
                _log.Error(ex, "Unexpected failure in OnSelectedRunChanged background read for {Path}", path);
            }
        });
    }

    [RelayCommand]
    private void ClearLive()
    {
        // Clear both the binding-visible string AND the backing buffer;
        // otherwise the next OnLineWritten flush would re-emit the
        // existing buffered tail.
        _liveBuffer.Clear();
        LiveTail = "";
        OnPropertyChanged(nameof(DisplayText));
    }

    /// <summary>
    /// Copy whatever the user is currently looking at — live tail when
    /// in live mode, the loaded run's full text otherwise. Bound to
    /// the unified log pane's "Copy" button.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task CopyDisplayed() =>
        await CopyToClipboardAsync(DisplayText);

    /// <summary>Copy the live tail to the clipboard. No-op if the tail
    /// is empty or the clipboard is unavailable (headless test runs).</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task CopyLive() =>
        await CopyToClipboardAsync(LiveTail);

    /// <summary>Copy the selected run's full log text to the clipboard.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task CopySelectedRun() =>
        await CopyToClipboardAsync(LogText);

    private static async System.Threading.Tasks.Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l
                && l.MainWindow is { } mw)
            {
                var clip = Avalonia.Controls.TopLevel.GetTopLevel(mw)?.Clipboard;
                if (clip is not null) await clip.SetTextAsync(text);
            }
        }
        catch { /* clipboard unavailable in headless / locked-down env — silent */ }
    }
}
