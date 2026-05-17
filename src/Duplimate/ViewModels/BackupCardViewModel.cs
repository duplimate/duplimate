using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Models;
using Duplimate.Services;

namespace Duplimate.ViewModels;

/// <summary>
/// Standardised "what you need to know about one backup at a glance"
/// view-model. Powers both the unified Backups view (status + actions +
/// live progress) and the Restore wizard's pick-a-backup step (no
/// progress, no actions).
///
/// One VM per backup row. Subscribes to <see cref="BackupProgressService.Updated"/>
/// while a run is in flight and projects per-source progress into the
/// observable bag the UI binds to. Disposed eagerly when the parent
/// list refreshes — see <see cref="Dispose"/>.
/// </summary>
public sealed partial class BackupCardViewModel : ObservableObject, IDisposable
{
    private readonly bool _showActions;
    private readonly bool _showProgress;
    private readonly Action<string>? _progressHandler;

    public string Id { get; }
    public Backup Snapshot { get; private set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _statusClass = "idle";
    [ObservableProperty] private string _statusLabel = "Idle";
    [ObservableProperty] private string _lastRunLabel = "Never run";
    [ObservableProperty] private string _nextRunLabel = "";
    [ObservableProperty] private string _verifyLabel = "Never verified";
    [ObservableProperty] private string _verifyClass = "never";
    /// <summary>
    /// Tone class for the NEXT-row badge. "scheduled" when the backup
    /// has auto-run enabled (accent blue clock); "manual" when only
    /// run-on-demand (gray pause). The card's metadata strip uses this
    /// in lockstep with StatusClass / VerifyClass to drive an icon-led
    /// row design where the user can read "ok / fail / scheduled" at a
    /// glance without parsing the value text.
    /// </summary>
    [ObservableProperty] private string _nextRunClass = "scheduled";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isVerifying;
    /// <summary>True while the run is registered (IsRunning) but
    /// parked on the orchestrator's per-destination queue waiting
    /// for a sibling backup to free up the storage. The card's
    /// top-right indicator swaps "Running…" for "Queued…" when this
    /// is true so the user can see at a glance which of the two
    /// states the backup is actually in. Set by
    /// <see cref="SetQueuedReason"/>; cleared by it once the gate
    /// releases.</summary>
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private double _overallPercent;
    /// <summary>
    /// Phase chip text shown next to the Overall progress bar while
    /// running — "Backing up", "Pruning", "Checking", etc.
    /// Without this, a backup that finished uploading but was still
    /// in its post-pass prune/check phase looked stuck at 100% with
    /// no explanation. Empty when idle, populated by
    /// <see cref="ApplyProgressSnapshot"/>.
    /// </summary>
    [ObservableProperty] private string _currentPhaseLabel = "";

    /// <summary>
    /// Tooltip shown on the Delete button (and used as the running-state
    /// branch by the per-action tooltip properties below). Empty string
    /// when the backup is idle so Avalonia hides the tooltip altogether
    /// for Delete (the trash icon + "Delete" label is unambiguous on its
    /// own — no idle hover hint is needed).
    /// </summary>
    public string RunningActionTooltip => IsRunning
        ? "This backup is currently running. Stop it first (Stop button above) to make changes, restore files, or run a test restore."
        : "";

    /// <summary>Hover hint for the Edit button. While running, explain
    /// why it's locked. While idle, briefly state what Edit opens — the
    /// previous binding reused <see cref="RunningActionTooltip"/>, which
    /// is empty when idle and rendered as an empty grey rectangle on
    /// hover.</summary>
    public string EditTooltip => IsRunning
        ? RunningActionTooltip
        : "Edit this backup's name, schedule, sources, destinations, or filters.";

    /// <summary>Hover hint for the Restore Files button. Same idle/running
    /// split as <see cref="EditTooltip"/>.</summary>
    public string RestoreFilesTooltip => IsRunning
        ? RunningActionTooltip
        : "Restore files from a previous revision of this backup to a folder you choose.";

    /// <summary>Hover hint for the Delete button. Earlier the button bound
    /// to <see cref="RunningActionTooltip"/> directly — empty when idle,
    /// which Avalonia rendered as an empty grey tooltip rectangle on
    /// hover. Same idle/running split as <see cref="EditTooltip"/>: when
    /// running, surface the "stop first" explanation; when idle, briefly
    /// describe what Delete will do so the user has a clear sense of the
    /// destructive scope before they click.</summary>
    public string DeleteTooltip => IsRunning
        ? RunningActionTooltip
        : "Delete this backup's definition (name, schedule, sources, destinations) and remove its scheduled task. Backed-up data on the destinations is left untouched.";

    /// <summary>Negated form of <see cref="IsRunning"/> for XAML bindings
    /// — Avalonia's binding-path parser refuses <c>!IsRunning</c> when
    /// it sits after a complex parent-relative path
    /// (<c>$parent[ItemsControl].((vm:Foo)DataContext).!IsRunning</c>),
    /// which is exactly how the Destinations ItemsControl reaches up to
    /// the card's IsRunning flag. Bind to this instead.</summary>
    public bool CanEditWhileIdle => !IsRunning;

    /// <summary>
    /// Populated by the parent <see cref="BackupsViewModel"/> when
    /// another currently-running backup shares one of this card's
    /// destinations — Duplicacy serialises writes to a single storage,
    /// so two backups racing on the same target risk corrupting the
    /// snapshot chain. Empty string when there's no conflict (the Run
    /// button is enabled). Surfaced as a tooltip on the disabled Run
    /// button so the user understands WHY they're blocked, instead of
    /// the previous silent-disable behaviour.
    /// </summary>
    [ObservableProperty] private string _runBlockedReason = "";

    /// <summary>True iff Run-now is allowed: the backup itself isn't
    /// running AND no sibling run is holding a destination this backup
    /// also targets. Drives the Run button's IsEnabled gate.</summary>
    public bool CanRunNow => !IsRunning && string.IsNullOrEmpty(RunBlockedReason);

    /// <summary>Tooltip shown on the Run button — the conflict reason
    /// when blocked, otherwise a brief description so a hover on the
    /// idle button explains what it does.</summary>
    public string RunNowTooltip => string.IsNullOrEmpty(RunBlockedReason)
        ? "Start a backup run now."
        : RunBlockedReason;

    partial void OnRunBlockedReasonChanged(string value)
    {
        OnPropertyChanged(nameof(CanRunNow));
        OnPropertyChanged(nameof(RunNowTooltip));
    }

    /// <summary>Test restore is gated on BOTH "not running" (a verify
    /// during a real run pulls bytes the live backup is racing
    /// against) and "not already verifying" (the obvious one).</summary>
    public bool TestRestoreEnabled => !IsRunning && !IsVerifying;
    public string TestRestoreTooltip => IsRunning
        ? "This backup is currently running. Stop it first to run a test restore."
        : "Restores 10 random files to a temp folder and checks they match the originals. Recommended monthly.";

    /// <summary>Positive-named negation of <see cref="IsRunning"/>. The
    /// per-source SourceLine bindings inside an ItemsControl can't use
    /// <c>!$parent[...].IsRunning</c> — Avalonia's parser doesn't accept
    /// the leading <c>!</c> after a $parent indexer (the user has been
    /// bitten by this before; see the Avalonia binding-negation memory
    /// note). Exposing the negated form as its own observable property
    /// makes the SourceLine "show size after backup ends" binding
    /// trivial.</summary>
    public bool IsIdle => !IsRunning;

    /// <summary>True iff the orchestrator picked up the run AND has
    /// already cleared the destination queue, i.e. the backup is
    /// actually executing duplicacy (not parked waiting). Drives the
    /// card's top-right "Running…" inline indicator; the alternative
    /// state, <see cref="IsQueued"/>, drives a parallel "Queued…"
    /// indicator. Mutually-exclusive within IsRunning=true.</summary>
    public bool IsActuallyRunning => IsRunning && !IsQueued;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditWhileIdle));
        OnPropertyChanged(nameof(RunningActionTooltip));
        OnPropertyChanged(nameof(EditTooltip));
        OnPropertyChanged(nameof(RestoreFilesTooltip));
        OnPropertyChanged(nameof(DeleteTooltip));
        OnPropertyChanged(nameof(TestRestoreEnabled));
        OnPropertyChanged(nameof(TestRestoreTooltip));
        OnPropertyChanged(nameof(CanRunNow));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsActuallyRunning));
    }
    partial void OnIsQueuedChanged(bool value) =>
        OnPropertyChanged(nameof(IsActuallyRunning));
    partial void OnIsVerifyingChanged(bool value)
    {
        OnPropertyChanged(nameof(TestRestoreEnabled));
    }

    /// <summary>Sources rendered as a scrollable per-row list. The row
    /// shows path + (when running) a thin progress bar.</summary>
    public ObservableCollection<SourceLine> Sources { get; } = new();

    /// <summary>Destinations rendered as small pills with the per-kind
    /// class icon + name. Each pill is clickable — opens the matching
    /// Edit Destination dialog. Backed by a per-pill VM so the icon
    /// can bind directly to the destination's Kind.</summary>
    public ObservableCollection<DestinationPill> Destinations { get; } = new();

    /// <summary>"1 DESTINATION" / "2 DESTINATIONS" — proper plural,
    /// upper-case so the BackupCard view renders it with the same
    /// label-caps style as SOURCES.</summary>
    public string DestinationsCountLabel =>
        Destinations.Count == 1 ? "1 DESTINATION" : $"{Destinations.Count} DESTINATIONS";

    /// <summary>"1 SOURCE" / "2 SOURCES" matching the destination
    /// counterpart — replaces the static SOURCES label so a singular
    /// source reads naturally.</summary>
    public string SourcesCountLabel =>
        Sources.Count == 1 ? "1 SOURCE" : $"{Sources.Count} SOURCES";

    /// <summary>Hover-tooltip text listing every destination on its own
    /// line — surfaced in the card by the consuming view.</summary>
    public string DestinationsTooltip => string.Join(Environment.NewLine, Destinations.Select(d => d.Name));

    /// <summary>Hover-tooltip text listing every source on its own line.</summary>
    public string SourcesTooltip => string.Join(Environment.NewLine, Sources.Select(s => s.Path));

    /// <summary>True iff the consuming view should render the action
    /// strip (Run/Stop/Edit/Test/Delete). False when this VM is used by
    /// the Restore wizard pick-step.</summary>
    public bool ShowActions => _showActions;

    /// <summary>True iff the consuming view should render live progress
    /// bars while a run is in flight. Restore wizard uses false; main
    /// list uses true.</summary>
    public bool ShowProgress => _showProgress;

    /// <param name="showActions">Wire Run / Stop / Edit / Delete / Test buttons.</param>
    /// <param name="showProgress">Subscribe to <see cref="BackupProgressService"/>
    /// and surface per-source live progress.</param>
    public BackupCardViewModel(Backup b, bool showActions, bool showProgress)
    {
        Id = b.Id;
        Snapshot = b;
        _showActions = showActions;
        _showProgress = showProgress;
        Apply(b);
        IsRunning = ServiceLocator.Orchestrator.IsRunning(b.Id);

        // If the orchestrator's maintenance gate is currently held
        // (Erase / Wipe / Prune in progress), stamp the
        // RunBlockedReason on this card up-front. Without this, a
        // brand-new card created mid-maintenance (e.g. config change
        // adding a new backup right after the user clicked Erase)
        // would have an empty RunBlockedReason and the Run Now
        // button would be enabled — directly contradicting the
        // global "no backups during maintenance" contract. The
        // BackupsViewModel.OnMaintenanceStateChanged broadcaster
        // already covers cards that exist when maintenance starts;
        // this constructor branch covers cards that come into
        // existence DURING maintenance.
        if (ServiceLocator.Maintenance.IsActive)
        {
            var reason = ServiceLocator.Maintenance.CurrentReason;
            RunBlockedReason = string.IsNullOrEmpty(reason)
                ? "A storage-maintenance task is running. Please wait."
                : $"A storage-maintenance task is running ({reason}). Please wait.";
        }

        // Note: this VM no longer subscribes to Orchestrator.RunStarted /
        // RunEnded itself. With N cards on screen, the previous per-card
        // subscription meant every event walked N delegates (each doing
        // an `if (backupId != Id) return;` early-return). The hosts —
        // <see cref="BackupsViewModel"/> for the unified list,
        // <see cref="RestoreViewModel"/> for the wizard's pick-step —
        // subscribe ONCE each and route the event to the matching card
        // by id, dropping multicast cost from O(N) to O(1).
        if (showProgress)
        {
            _progressHandler = OnProgressUpdated;
            ServiceLocator.Progress.Updated += _progressHandler;
            ApplyProgressSnapshot();
        }
    }

    public void Apply(Backup b)
    {
        Snapshot = b;
        Name = b.Name;

        // Sources column. Preserve existing entries by path so the
        // progress-bar binding doesn't flicker on each parent refresh.
        // Fast path: if the source list is identical to what we already
        // show, skip the rebuild entirely. The orchestrator's RunEnded
        // calls Apply(b) then Apply(record) back-to-back; without this
        // a 10-source backup fires 22 CollectionChanged events per run
        // for zero structural change.
        bool sourcesUnchanged =
            Sources.Count == b.SourcePaths.Count
            && SourcesMatch(Sources, b.SourcePaths);
        if (!sourcesUnchanged)
        {
            var existing = Sources.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
            Sources.Clear();
            foreach (var p in b.SourcePaths)
            {
                if (!existing.TryGetValue(p, out var line))
                    line = new SourceLine(p);
                Sources.Add(line);
            }
            OnPropertyChanged(nameof(SourcesTooltip));
            OnPropertyChanged(nameof(SourcesCountLabel));
        }

        // Per-source size labels from the last successful run. Always
        // refreshed (not just on structural change) so a Config.Changed
        // landing AFTER a successful run picks up the new sizes
        // immediately. SourceLine.SizeLabel uses HumanSize formatting
        // — empty string when no size has been captured yet so the
        // BackupCard doesn't render "(0 B)" for never-run backups.
        foreach (var line in Sources)
        {
            line.SizeLabel = b.SourceLastSizeBytes.TryGetValue(line.Path, out var bytes) && bytes > 0
                ? DuplicacyRunner.HumanSize(bytes)
                : "";
        }

        // Destinations column. Each pill carries the class kind so the
        // pill view can bind the per-kind icon, AND the destination id
        // so a click can open the matching Edit Destination dialog.
        // The tooltip is per-pill so hovering shows the actual location
        // (e.g. "Local folder · D:\Backups · Click to edit") instead of
        // the parent card's RunningActionTooltip, which is empty when
        // the backup is idle and rendered as a blank tooltip.
        // Same fast-path-skip as Sources above.
        var desiredPills = new List<DestinationPill>(b.Targets.Count);
        foreach (var t in b.Targets)
        {
            var d = ServiceLocator.Config.Current.Destinations.FirstOrDefault(x => x.Id == t.DestinationId);
            if (d is not null)
                desiredPills.Add(new DestinationPill(d.Id, d.Name, d.Kind, BuildDestinationTooltip(d)));
            else
                desiredPills.Add(new DestinationPill(
                    "", "(missing)", DestinationKind.LocalFolder,
                    "This destination has been deleted from the app's destinations list. Edit the backup to remove it or pick a different destination."));
        }
        if (!DestinationsMatch(Destinations, desiredPills))
        {
            // Carry over any in-flight PercentComplete by destination Id
            // so a Config.Changed-driven rebuild (e.g. tooltip refresh
            // because the underlying Destination's path was edited)
            // doesn't visibly snap the pill back to 0% mid-run before
            // the next progress flush. PercentComplete isn't part of
            // DestinationsMatch — we only rebuild on identity / kind /
            // tooltip changes, none of which invalidate the live
            // percent. Use TryAdd-style insertion so a (theoretically
            // impossible but defensively-handled) duplicate Id doesn't
            // throw — the first occurrence wins, matching the order
            // the user added the targets.
            var priorPct = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in Destinations)
            {
                if (!string.IsNullOrEmpty(p.Id))
                    priorPct.TryAdd(p.Id, p.PercentComplete);
            }
            Destinations.Clear();
            foreach (var p in desiredPills)
            {
                if (!string.IsNullOrEmpty(p.Id) && priorPct.TryGetValue(p.Id, out var pct))
                    p.PercentComplete = pct;
                Destinations.Add(p);
            }
            OnPropertyChanged(nameof(DestinationsCountLabel));
            OnPropertyChanged(nameof(DestinationsTooltip));
        }

        // Status badges + labels. The status-icon class set drives both
        // the leading PathIcon (check / warn-triangle / x-circle / pause /
        // spinner / empty-circle) AND its colour via styled selectors —
        // single source of truth so renaming a class doesn't desync the
        // glyph from the tone.
        switch (b.LastRunStatus)
        {
            case BackupRunStatus.Success:  StatusClass = "ok";      StatusLabel = "OK";      break;
            case BackupRunStatus.Warning:  StatusClass = "warn";    StatusLabel = "Warning"; break;
            case BackupRunStatus.Failed:   StatusClass = "fail";    StatusLabel = "Failed";  break;
            case BackupRunStatus.Skipped:  StatusClass = "skipped"; StatusLabel = "Skipped"; break;
            case BackupRunStatus.Running:  StatusClass = "running"; StatusLabel = "Running"; break;
            default:                       StatusClass = "idle";    StatusLabel = "Idle";    break;
        }
        LastRunLabel = b.LastRunEndUtc is DateTime end
            ? $"{NormaliseSuccessSummary(b.LastRunSummary, b.LastRunStatus)} · {HumanAgo(end)}"
            : "Never run";
        NextRunLabel = !b.Enabled
            ? "Manual only — auto-run is off"
            : b.Schedule.DescribeNextRun(DateTime.Now);
        NextRunClass = b.Enabled ? "scheduled" : "manual";
        ApplyVerifyState(b);
    }

    public void ApplyVerifyState(Backup b)
    {
        if (b.LastVerifyUtc is not DateTime when || b.LastVerifyPass is null)
        {
            // New verify-row layout puts the Test-restore button at the
            // far left, then the status dot + this label. With the
            // button no longer inline-glued to the label, the wording
            // reads as a standalone state ("Not yet tested") instead
            // of the previous "Never verified — try a [Test restore]"
            // sentence-completion idiom.
            VerifyLabel = "Not yet tested";
            VerifyClass = "never";
            return;
        }
        var age = DateTime.UtcNow - when;
        var ago = HumanAgo(when);
        if (b.LastVerifyPass == false)
        {
            VerifyLabel = $"FAILED · {ago}";
            VerifyClass = "fail";
        }
        else if (age > BackupVerifier.VerifyStaleAfter)
        {
            VerifyLabel = $"Succeeded · {ago} — overdue (>30 days)";
            VerifyClass = "stale";
        }
        else
        {
            VerifyLabel = $"Succeeded · {ago}";
            VerifyClass = "ok";
        }
    }

    public void SetRunning()
    {
        IsRunning = true;
        IsQueued = false;
        StatusClass = "running";
        StatusLabel = "Running";
        LastRunLabel = "Running…";
    }

    /// <summary>Flip the inline label to a "Queued — {reason}" message
    /// while the orchestrator waits for a shared destination to free
    /// up. Called from <see cref="BackupsViewModel"/>'s
    /// RunQueuedReasonChanged handler. Empty reason flips back to
    /// "Running…" — that's the orchestrator signalling that the
    /// destination gate was just acquired and the run is now actually
    /// executing. Also flips <see cref="IsQueued"/> so the card's
    /// top-right indicator can swap "Running…" for "Queued…" — the
    /// user explicitly asked for the indicator to differentiate
    /// "actually running" from "waiting in line".</summary>
    public void SetQueuedReason(string reason)
    {
        if (!IsRunning) return; // shouldn't happen — orchestrator only fires after RunStarted
        var queued = !string.IsNullOrEmpty(reason);
        IsQueued = queued;
        LastRunLabel = queued ? $"Queued — {reason}" : "Running…";
    }

    public void Apply(RunRecord r)
    {
        StatusClass = r.Status switch
        {
            BackupRunStatus.Success => "ok",
            BackupRunStatus.Warning => "warn",
            BackupRunStatus.Failed  => "fail",
            BackupRunStatus.Skipped => "skipped",
            _                       => "idle",
        };
        StatusLabel = r.Status.ToString();
        LastRunLabel = r.Summary;
        IsRunning = false;
        IsQueued = false;
        OverallPercent = 0;
        // Clear the phase chip alongside the bars so a finished run
        // doesn't leave a stale "Pruning…" pill flashing the moment a
        // new run kicks off (the IsRunning gate hides it post-run, but
        // the next ApplyProgressSnapshot would briefly re-surface the
        // old phase before the orchestrator emits its first marker).
        CurrentPhaseLabel = "";
        foreach (var s in Sources) { s.PercentComplete = 0; s.IsRunning = false; s.Detail = ""; }
        foreach (var d in Destinations) d.PercentComplete = 0;
    }

    private void OnProgressUpdated(string backupId)
    {
        if (backupId != Id) return;
        if (_disposed) return;
        // Progress events fire on the runner's stdout pump thread; the
        // VM's observable mutations need the UI thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(ApplyProgressSnapshot);
    }

    private void ApplyProgressSnapshot()
    {
        // A dispatcher Post scheduled BEFORE Dispose may run AFTER it.
        // Mutating Sources / Destinations on a disposed card visibly
        // resets percent on parent cards that re-create with the same
        // Id later (BackupsViewModel diffs by Id, so a deleted+
        // recreated backup with the same name lands here). Cheap
        // guard, no-op when alive.
        if (_disposed) return;
        var snap = ServiceLocator.Progress.Snapshot(Id);
        if (snap is null)
        {
            OverallPercent = 0;
            CurrentPhaseLabel = "";
            foreach (var s in Sources) { s.PercentComplete = 0; s.IsRunning = false; s.Detail = ""; }
            foreach (var d in Destinations) d.PercentComplete = 0;
            return;
        }
        OverallPercent = snap.OverallPercent;
        foreach (var s in Sources)
        {
            if (snap.Sources.TryGetValue(s.Path, out var sp))
            {
                s.PercentComplete = sp.PercentComplete;
                s.IsRunning = sp.IsRunning;
                s.Detail = sp.Detail;
            }
        }
        // Per-destination progress for the pill bar fill. Match by
        // Name (the same key the orchestrator passes to
        // ReportCellStarted), and leave pills not yet seen at 0%.
        foreach (var d in Destinations)
        {
            d.PercentComplete = snap.Destinations.TryGetValue(d.Name, out var dp)
                ? dp.PercentComplete
                : 0;
        }
        // Phase chip: take the first running cell's phase (most
        // common case is all cells in lockstep, but if they're not
        // we surface the loudest one). Empty when no cell is live —
        // typically the brief gap between cells.
        var livePhase = snap.Sources.Values
            .SelectMany(s => s.Cells.Values)
            .FirstOrDefault(c => c.IsRunning && !string.IsNullOrEmpty(c.Phase))
            ?.Phase ?? "";
        CurrentPhaseLabel = livePhase;
    }

    /// <summary>Volatile so the read in <see cref="OnProgressUpdated"/>
    /// (which fires on the runner's stdout pump thread, not the UI
    /// thread) is guaranteed to observe the write from
    /// <see cref="Dispose"/> on weak memory models like ARM64 — without
    /// the keyword the JIT could re-order or cache, leaving the pump
    /// thread posting to a defunct VM after Dispose ran.</summary>
    private volatile bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        if (_progressHandler is not null)
            ServiceLocator.Progress.Updated -= _progressHandler;
    }

    /// <summary>
    /// Bound to the destination pill's Click. Opens the matching
    /// Destination editor by raising an event the parent
    /// BackupsViewModel handles (cards don't open windows themselves
    /// because they're hosted inside DataTemplates and have no direct
    /// access to a parent Window). Parameter is the destination id;
    /// parent VM resolves the actual model + opens the editor.
    /// </summary>
    [RelayCommand]
    private async Task EditDestination(string destinationId)
    {
        if (string.IsNullOrEmpty(destinationId)) return;
        var handler = EditDestinationRequested;
        if (handler is null) return;
        await handler(destinationId);
    }

    /// <summary>True iff the last run finished in a state worth
    /// drawing the user's attention to. Drives the inline "View logs"
    /// pill on the card header — clicking it jumps to Activity &amp;
    /// logs preselected on this backup so the user can see WHY without
    /// hunting. The user reported: "Would be good if there is an
    /// error/warning to add a View logs button/pill next to the backup
    /// name so users can click and be sent straight to the relevant
    /// logs."</summary>
    public bool HasAttentionStatus =>
        string.Equals(StatusClass, "warn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(StatusClass, "fail", StringComparison.OrdinalIgnoreCase);

    partial void OnStatusClassChanged(string value) =>
        OnPropertyChanged(nameof(HasAttentionStatus));

    /// <summary>Navigate to Activity &amp; logs and pre-select this
    /// backup so the most recent run's persisted log opens
    /// automatically. Mirrors the click-to-logs handler in
    /// <see cref="BackupOrchestrator.SendAlertsAsync"/>'s toast
    /// action.</summary>
    [RelayCommand]
    private void ViewLogs()
    {
        if (string.IsNullOrEmpty(Id)) return;
        AppNavigator.Go(NavItem.Logs);
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l
                && l.MainWindow?.DataContext is MainWindowViewModel vm)
            {
                vm.Logs.SelectBackupById(Id);
            }
        }
        catch (Exception ex) { AppLogger.For<BackupCardViewModel>().Warning(ex, "ViewLogs handler threw for {Id}", Id); }
    }

    /// <summary>
    /// Wired by the parent BackupsViewModel at construction. Raised
    /// when the user clicks a destination pill on this card; the
    /// handler opens the Edit Destination dialog and persists changes.
    /// </summary>
    public Func<string, Task>? EditDestinationRequested { get; set; }

    /// <summary>Compose the per-pill hover tooltip: a friendly kind
    /// label + the actual location + a click hint. The location string
    /// varies by kind: a folder path for local-like destinations, the
    /// cloud sub-path for OAuth destinations, and bucket/region for S3.
    /// Earlier the pill bound to the parent card's RunningActionTooltip,
    /// which is empty when the backup isn't running, so hovering on the
    /// pill while idle showed an empty grey rectangle.</summary>
    private static string BuildDestinationTooltip(Destination d)
    {
        var kind = d.Kind switch
        {
            DestinationKind.LocalFolder       => "Local folder",
            DestinationKind.ExternalDrive     => "External drive",
            DestinationKind.NetworkShare      => "Network share",
            DestinationKind.DropboxAppScoped  => "Dropbox (app-scoped)",
            DestinationKind.DropboxFullAccess => "Dropbox (full access)",
            DestinationKind.OneDrivePersonal  => "OneDrive (personal)",
            DestinationKind.OneDriveBusiness  => "OneDrive (business)",
            DestinationKind.GoogleDrive       => "Google Drive",
            DestinationKind.S3Compatible      => "S3-compatible",
            _ => d.Kind.ToString(),
        };
        string location;
        if (d.Kind == DestinationKind.S3Compatible)
        {
            var bucket = string.IsNullOrWhiteSpace(d.S3Bucket) ? "(no bucket)" : d.S3Bucket;
            var endpoint = string.IsNullOrWhiteSpace(d.S3Endpoint) ? "" : $" @ {d.S3Endpoint}";
            var sub = string.IsNullOrWhiteSpace(d.PathOrSubpath) ? "" : $"/{d.PathOrSubpath.TrimStart('/')}";
            location = $"{bucket}{sub}{endpoint}";
        }
        else
        {
            location = string.IsNullOrWhiteSpace(d.PathOrSubpath) ? "(no location set)" : d.PathOrSubpath;
        }
        return $"{kind} · {location}\nClick to edit this destination.";
    }

    /// <summary>True iff the existing SourceLine collection's paths
    /// sequence-equal the new source list (case-insensitive). Hot-path
    /// short-circuit for the common case where Apply(b) is called with
    /// the same backup snapshot.</summary>
    private static bool SourcesMatch(
        IReadOnlyList<SourceLine> existing,
        IReadOnlyList<string> desired)
    {
        for (int i = 0; i < existing.Count; i++)
            if (!string.Equals(existing[i].Path, desired[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>True iff the existing destination pills sequence-equal
    /// the desired set on every visible field. DestinationPill is
    /// immutable so a field-by-field equality check is sufficient — no
    /// need to compare ToolTip text since it's derived from kind +
    /// location.</summary>
    private static bool DestinationsMatch(
        IReadOnlyList<DestinationPill> existing,
        IReadOnlyList<DestinationPill> desired)
    {
        if (existing.Count != desired.Count) return false;
        for (int i = 0; i < existing.Count; i++)
        {
            var a = existing[i];
            var b = desired[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
            if (a.Kind != b.Kind) return false;
            if (!string.Equals(a.Tooltip, b.Tooltip, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string HumanAgo(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1))   return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1))    return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    /// <summary>
    /// Display-time rewrite of a stored RunRecord summary. New
    /// successful no-op runs get "Completed in Xs" from
    /// <see cref="BackupOrchestrator.BuildSummary"/>, but the
    /// persisted history.json still holds the older bare "in Xs"
    /// shape — rendering that as "LAST in 4s · 7d ago" reads as a
    /// missing-verb sentence. The user reported: "I am still seeing
    /// LAST in 4s — 7d ago (no Completed), make sure the changes
    /// were applied everywhere relevant." This catches up old
    /// records at render time so they look the same as new ones.
    /// Only applies to Success runs; Skipped / Warning / Failed
    /// already have descriptive prefixes ("Manually skipped after",
    /// "5 errors · in", etc.).
    /// </summary>
    private static string NormaliseSuccessSummary(string? raw, BackupRunStatus status)
    {
        var s = raw ?? "—";
        if (status != BackupRunStatus.Success) return s;
        // Bare "in 4s" / "in 1h 12m" / "in 1m 12s" with no leading
        // descriptive parts → prepend "Completed". The regex
        // requires a digit immediately after "in " so we don't
        // accidentally rewrite something like "in case of …" — the
        // duration pattern always starts with a number.
        // Other shapes ("1.2 GB uploaded · in 5s", "Manually skipped
        // after 2s", etc.) pass through unchanged because they
        // already read fine.
        if (NormaliseSuccessRx.IsMatch(s))
            return "Completed " + s;
        return s;
    }

    private static readonly System.Text.RegularExpressions.Regex NormaliseSuccessRx =
        new(@"^in \d", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>One row in the Sources column. Observable so the
    /// per-source percent / running flag drive a live progress bar
    /// behind the path label.</summary>
    public sealed partial class SourceLine : ObservableObject
    {
        public string Path { get; }
        [ObservableProperty] private double _percentComplete;
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private string _detail = "";
        /// <summary>Short, human-formatted size of the source from the
        /// last successful run ("1.2 GB", "84 MB", …). Empty until the
        /// first successful backup populates the per-source size cache
        /// in <see cref="Backup.SourceLastSizeBytes"/>; rendered in
        /// parentheses next to the path on the BackupCard.</summary>
        [ObservableProperty] private string _sizeLabel = "";
        public bool HasSize => !string.IsNullOrEmpty(SizeLabel);
        partial void OnSizeLabelChanged(string value) =>
            OnPropertyChanged(nameof(HasSize));
        public SourceLine(string path) { Path = path; }
    }

    /// <summary>One pill in the Destinations column. Identity fields
    /// (Id, Name, Kind, Tooltip) are immutable — only the live progress
    /// fill (<see cref="PercentComplete"/>) mutates. Pills are replaced
    /// wholesale by <see cref="Apply(Duplimate.Models.Backup)"/> only
    /// when the structural set changes (different destinations, ids,
    /// kinds); otherwise the existing pills are kept so their progress
    /// state survives a Config.Changed-driven refresh that didn't
    /// actually touch the destinations list. <see cref="IsClickable"/>
    /// is false for "(missing)" pills (the underlying destination was
    /// deleted from config), so the view can disable the button
    /// instead of letting the click silently no-op.
    /// </summary>
    public sealed partial class DestinationPill : ObservableObject
    {
        public string Id { get; }
        public string Name { get; }
        public DestinationKind Kind { get; }
        public string Tooltip { get; }
        public bool IsClickable => !string.IsNullOrEmpty(Id);

        /// <summary>0..100 — drives the per-pill progress fill so the
        /// user can see at a glance which destination is lagging in a
        /// multi-destination run. Updated from the
        /// <see cref="BackupProgressService"/> snapshot's
        /// <see cref="BackupProgressService.BackupProgress.Destinations"/>
        /// dictionary keyed by destination Name.</summary>
        [ObservableProperty] private double _percentComplete;

        public DestinationPill(string id, string name, DestinationKind kind, string tooltip)
        {
            Id = id; Name = name; Kind = kind; Tooltip = tooltip;
        }
    }
}
