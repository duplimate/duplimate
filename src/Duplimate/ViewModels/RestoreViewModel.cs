using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Views;

namespace Duplimate.ViewModels;

/// <summary>
/// Step-by-step wizard. Each step has its own property visibility; navigation is
/// linear with Back/Next, and destructive paths (overwriting the original
/// source) gate on an OVERWRITE-to-confirm dialog before Phase 1 begins.
/// </summary>
public sealed partial class RestoreViewModel : ViewModelBase, IDisposable
{
    // ---- wizard state ----

    public enum Step { PickBackup, PickSource, PickDestination, PickRevision, PickFiles, PickTarget, Progress, Summary }
    [ObservableProperty] private Step _currentStep = Step.PickBackup;

    // Step 1 — DEAD STATE. The wizard's only entry point is now
    // AppNavigator.PreselectRestoreBackup from a BackupCard's
    // "Restore files" button (the sidebar nav was removed). The
    // PickBackup step renders a defensive "open a backup card to
    // start" empty-state placeholder when SelectedBackup is null
    // (e.g. someone reaches the wizard via a stale shortcut). The
    // Backups collection survives so OnPreselectRequested can resolve
    // the inbound id back to a live model.
    public ObservableCollection<Backup> Backups { get; } = new();
    [ObservableProperty] private Backup? _selectedBackup;

    // Step 1b (only when backup has >1 source).
    // SourcesForBackup is the multi-select pick list — each row carries
    // its own IsSelected flag so the wizard can run a restore for any
    // subset of the backup's sources in one pass. SelectedSourcePath
    // stays around as the "currently in-flight" pointer used by the
    // single-source PickRevision / PickFiles flow; for multi-source
    // restores we drive that pointer programmatically as the loop
    // walks each picked source.
    public ObservableCollection<SourcePickRow> SourcesForBackup { get; } = new();
    [ObservableProperty] private string? _selectedSourcePath;

    /// <summary>
    /// True iff the user picked more than one source on the
    /// PickSource step. The wizard then bypasses per-source revision
    /// + file picking and runs RestoreEngine for each picked source
    /// using the latest revision and every file. Reset in
    /// <see cref="RestartWizard"/>.
    /// </summary>
    [ObservableProperty] private bool _multiSourceMode;

    /// <summary>Paths actually checked on the PickSource step
    /// (multi-select). For single-source backups this is auto-set to
    /// the lone source.</summary>
    public IReadOnlyList<string> CheckedSourcePaths =>
        SourcesForBackup.Where(r => r.IsSelected).Select(r => r.Path).ToList();

    /// <summary>True iff at least one source row is checked — drives
    /// the PickSource step's Next button enable state.</summary>
    public bool HasAnyCheckedSource =>
        SourcesForBackup.Any(r => r.IsSelected);

    /// <summary>
    /// Label shown next to the "Restore to the original source" radio
    /// on the PickTarget step. Single-source: "→ &lt;source&gt; …".
    /// Multi-source: a count + the OVERWRITE warning so the user
    /// understands every checked folder will be replaced.
    /// </summary>
    public string OriginalSourceTargetLabel
    {
        get
        {
            if (MultiSourceMode)
            {
                var n = CheckedSourcePaths.Count;
                return $"→ Each of the {n} ticked folders (existing files will be overwritten — you'll need to type OVERWRITE to confirm)";
            }
            return $"→ {SelectedSourcePath} (existing files will be overwritten — you'll need to type OVERWRITE to confirm)";
        }
    }

    /// <summary>
    /// Re-fire all derived properties that change with multi-source
    /// state: the original-source target label (singular vs N-folders
    /// copy), the wizard step counter (file picking is bypassed in
    /// multi-source so the count contracts), and CanGoBack (PickTarget
    /// rewinds differently depending on mode).
    /// </summary>
    partial void OnMultiSourceModeChanged(bool value)
    {
        OnPropertyChanged(nameof(OriginalSourceTargetLabel));
        OnPropertyChanged(nameof(WizardStepLabel));
        OnPropertyChanged(nameof(CanGoBack));
    }
    partial void OnSelectedSourcePathChanged(string? value)
        => OnPropertyChanged(nameof(OriginalSourceTargetLabel));

    // Step 2
    /// <summary>
    /// Per-row VM wrappers for the destination picker. The earlier
    /// pattern bound RadioButton.IsChecked through
    /// <c>$parent[ItemsControl].((vm:RestoreViewModel)DataContext).SelectedDestination</c>
    /// + a RefEq converter, but that chain didn't propagate the
    /// OnPropertyChanged from <see cref="SelectedDestination"/> back
    /// to the per-row binding on Avalonia 11 — clicks DID set the VM
    /// property, the radio dot just never visually updated. The user
    /// reported "Clicking on Which destination to read from doesn't
    /// work at all, it won't select the item."
    ///
    /// Mirroring the working <see cref="SourcePickRow"/> pattern: wrap
    /// each Destination in a row VM with its own
    /// <see cref="DestinationPickRow.IsSelected"/> ObservableProperty
    /// the radio dot binds to directly. Mutual exclusion is enforced
    /// in <see cref="PickDestination"/> by toggling every other row's
    /// flag off.
    /// </summary>
    public ObservableCollection<DestinationPickRow> DestinationPickRows { get; } = new();
    /// <summary>Legacy list — kept for the few internal call sites
    /// (count checks in <c>WizardStepLabel</c> / <c>CanGoBack</c>).
    /// Stays in lockstep with <see cref="DestinationPickRows"/>.</summary>
    public ObservableCollection<Destination> DestinationsForBackup { get; } = new();
    [ObservableProperty] private Destination? _selectedDestination;
    [ObservableProperty] private string _selectedStorageName = "default";

    // Step 3
    public ObservableCollection<RevisionSummary> Revisions { get; } = new();
    [ObservableProperty] private RevisionSummary? _selectedRevision;
    [ObservableProperty] private bool _loadingRevisions;

    /// <summary>
    /// Multi-source restore summary — one read-only row per picked
    /// source showing its latest revision's number, age, and size. The
    /// PickRevision step renders this list (instead of the single-
    /// source ListBox picker) when <see cref="MultiSourceMode"/> is
    /// true, so the user always SEES which moment-in-time the wizard
    /// will restore from before clicking forward. Previously multi-
    /// source mode skipped the revision step entirely, jumping
    /// straight to PickTarget — which the user reported as "click
    /// Pick a revision and it goes straight to Step 6, no revision
    /// selection". The user explicitly asked: "Always show revision
    /// even if there is only one (helpful for user to see how old
    /// it is)" — that's what this collection serves.
    /// </summary>
    public ObservableCollection<MultiSourceRevisionRow> MultiSourceRevisions { get; } = new();

    /// <summary>True iff at least one revision is available to pick. The
    /// view binds the ListBox visibility to this so an empty list doesn't
    /// render a hollow border above the action buttons.</summary>
    public bool HasRevisions => Revisions.Count > 0;
    /// <summary>True iff the revision pane should display the
    /// "no revisions yet" empty-state placeholder — i.e., we finished
    /// loading, no error, and nothing came back. Loading and error
    /// states are surfaced separately above the list.</summary>
    public bool ShowNoRevisionsHint =>
        !LoadingRevisions
        && string.IsNullOrEmpty(LoadError)
        && Revisions.Count == 0;

    // Step 4
    public ObservableCollection<RevisionFileRow> AllFiles { get; } = new();
    public ObservableCollection<RevisionFileRow> FilteredFiles { get; } = new();
    [ObservableProperty] private string _fileFilter = "";
    [ObservableProperty] private bool _loadingFiles;
    [ObservableProperty] private int _selectedFilesCount;
    [ObservableProperty] private long _selectedFilesBytes;

    /// <summary>Materialised list of paths currently checked — the wire format for RestoreEngine.</summary>
    public IReadOnlyList<string> SelectedPaths =>
        AllFiles.Where(f => f.IsSelected).Select(f => f.File.Path).ToList();

    // Step 5
    [ObservableProperty] private bool _targetOriginalSource = true;
    [ObservableProperty] private string _customTargetPath = "";
    [ObservableProperty] private bool _preserveStructure = true;
    [ObservableProperty] private bool _overwriteExisting = true;
    [ObservableProperty] private int _threads = 4;

    // Step 6 (progress)
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _restoredCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _inFlightCount;
    [ObservableProperty] private string _progressLog = "";
    [ObservableProperty] private bool _isRunning;
    public ObservableCollection<FailedFileRow> FailuresList { get; } = new();

    // Step 7 (summary)
    [ObservableProperty] private bool _anyFailures;
    [ObservableProperty] private string _summaryLine = "";

    // Internal
    private CancellationTokenSource? _cts;
    private RestoreRequest? _currentRequest;
    private RestoreOutcome? _currentOutcome;

    /// <summary>
    /// Wall-clock instant the user committed to the current restore
    /// (the moment <c>NextFromTargetCommand</c> kicks off the run).
    /// Used to populate the persisted <see cref="RunRecord.StartedUtc"/>
    /// when we drop a Restore-kind entry into the per-backup history
    /// at end-of-run, so the LogsView's RUN dropdown can interleave
    /// restore activity with backup runs by start time.
    /// </summary>
    private DateTime _restoreRunStartedUtc;

    /// <summary>
    /// In-flight RunRecord shared between the synthetic Running
    /// entry registered via <see cref="RunActivityTracker"/> and the
    /// final persisted record handed to <see cref="LogStore.AppendRun"/>.
    /// Re-using the same Id means the dropdown row the user clicked
    /// while the run was in flight stays the same row after the run
    /// completes — Avalonia's ComboBox SelectedItem keeps tracking
    /// it across the synth → persisted swap.
    /// </summary>
    private RunRecord? _inFlightRunRecord;

    /// <summary>
    /// Per-listing cancellation source for the revision step's
    /// background load. A rapid Back→Next sequence used to leave the
    /// previous listing in flight; both completions then raced to
    /// populate <see cref="Revisions"/> / <see cref="MultiSourceRevisions"/>,
    /// occasionally producing duplicate rows or stomping a freshly-
    /// picked SelectedRevision back to "latest". Cancelled and
    /// replaced inside <see cref="EnterAfterDestinationAsync"/> and
    /// in <see cref="Back"/>; the loaders honour the token by
    /// short-circuiting before mutating bound collections.
    /// </summary>
    private CancellationTokenSource? _revisionLoadCts;

    /// <summary>Coalesces a flurry of Config.Changed firings into a
    /// single Refresh on the UI thread.</summary>
    private readonly UiThreadDebouncer _refreshDebouncer;

    public RestoreViewModel()
    {
        _refreshDebouncer = new UiThreadDebouncer(Refresh);
        Refresh();
        // The Restore wizard's only entry point is the per-backup
        // "Restore files" button on a BackupCard, which fires
        // AppNavigator.PreselectRestoreBackup. Step 1 (PickBackup)
        // is now an empty-state placeholder shown when the user
        // somehow lands here without a preselect (defensive).
        AppNavigator.RestorePreselectRequested += OnPreselectRequested;
        ServiceLocator.Config.Changed += OnConfigChanged;
        Threads = ServiceLocator.Config.Current.Restore.DefaultThreads;
        PreserveStructure = ServiceLocator.Config.Current.Restore.PreserveStructureByDefault;
        OverwriteExisting = ServiceLocator.Config.Current.Restore.OverwriteByDefault;
        // Default the safe-restore target to a /restore subfolder beside
        // the app exe — always writable, easy to find, never collides
        // with anything the user cares about.
        CustomTargetPath = SafeRestoreTargetRoot();
    }

    private void OnPreselectRequested(Backup b)
    {
        // Run the whole sequence on the UI thread. The await chain
        // inside NextFromBackupAsync touches ObservableProperty setters
        // and Avalonia visual state — must not run on a thread-pool
        // thread. InvokeAsync schedules the async lambda on the
        // dispatcher and the continuation after each await stays on
        // the UI thread (the dispatcher's sync context is captured).
        // We discard the returned task; exceptions surface via the
        // try/catch inside.
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var match = Backups.FirstOrDefault(x => x.Id == b.Id);
                if (match is null) return;
                SelectedBackup = match;
                // The user reached Restore by clicking "Restore files"
                // on a specific backup card — step 1 (pick a backup)
                // is already satisfied. Auto-advance into the next
                // applicable step (PickSource for multi-source,
                // PickDestination for multi-dest, otherwise
                // PickRevision). NextFromBackupCommand runs the same
                // gates the explicit Next button uses (running-backup
                // notice, never-run notice), so the wizard stays on
                // step 1 with the right error surface if there's
                // nothing to restore.
                await NextFromBackupCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                AppLogger.For<RestoreViewModel>().Warning(ex,
                    "Auto-advance from preselect threw for {Backup}", b.Name);
            }
        });
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _refreshDebouncer.Schedule();
        // Verbose may have just toggled — re-materialize the bound
        // ProgressLog so the change applies retroactively to whatever
        // is on screen instead of only to lines that arrive afterwards.
        // Hot-path safe: ProgressLog flushes are debounced, so this
        // single materialization on a config event is negligible.
        if (Dispatcher.UIThread.CheckAccess())
            ProgressLog = MaterializeProgressLog();
        else
            Dispatcher.UIThread.Post(() => ProgressLog = MaterializeProgressLog());
    }

    public void Dispose()
    {
        AppNavigator.RestorePreselectRequested -= OnPreselectRequested;
        ServiceLocator.Config.Changed -= OnConfigChanged;
        // Cancel-and-dispose the live restore CTS (if any). Without this
        // a navigate-away-during-restore leaked the CTS — every restore
        // also leaked the prior CTS because Run/RetryAll reassigned
        // _cts without disposing.
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        // Same cancel-and-dispose pattern for the revision-listing CTS
        // (separate lifetime from the restore-run CTS — listings happen
        // pre-restore and shouldn't keep the wizard alive past Dispose).
        try { _revisionLoadCts?.Cancel(); } catch { }
        try { _revisionLoadCts?.Dispose(); } catch { }
        _revisionLoadCts = null;
        _refreshDebouncer.Dispose();
    }

    public void Refresh()
    {
        Backups.Clear();
        foreach (var b in ServiceLocator.Config.Current.Backups.OrderBy(b => b.Name))
        {
            Backups.Add(b);
        }
        OnPropertyChanged(nameof(HasAnyBackup));

        // If the backup the wizard was working with has been deleted
        // (or every backup was), tear the wizard back to step 1. Without
        // this the user could be stuck on "pick revision" for a backup
        // that no longer exists, with nothing in the list and the
        // sidebar showing "0 backups" — visually broken state.
        if (SelectedBackup is not null && !Backups.Any(b => b.Id == SelectedBackup.Id))
        {
            SelectedBackup = null;
            SelectedDestination = null;
            SelectedSourcePath = null;
            SelectedRevision = null;
            Revisions.Clear();
            AllFiles.Clear();
            FilteredFiles.Clear();
            CurrentStep = Step.PickBackup;
            OnPropertyChanged(nameof(HasRevisions));
            OnPropertyChanged(nameof(ShowNoRevisionsHint));
        }

        // If exactly one backup is configured, pre-pick it. Saves the
        // user a click on "the only obvious option" — this view's job
        // is to get them to a restore, not to make them re-prove that
        // there's only one backup. Don't override an explicit
        // selection if the user's already on step 1+ for some reason.
        if (Backups.Count == 1 && SelectedBackup is null)
            SelectedBackup = Backups[0];
    }

    /// <summary>
    /// Path the shell-extension verb passed in (absolute path, as Windows
    /// gave it to us). Drives the matching-backup lookup; not used for
    /// filtering — see <see cref="ShellLaunchFilterPath"/> for the value
    /// that's actually applied to the file list.
    /// </summary>
    [ObservableProperty] private string? _shellLaunchPath;

    /// <summary>
    /// Sticky shell-launch filter. Set only by <see cref="HandleShellLaunchPath"/>;
    /// cleared only by <see cref="ClearShellLaunchFilterCommand"/>. The
    /// user-editable <see cref="FileFilter"/> is a separate, additive
    /// narrow on top of this one — so accidentally clearing the search
    /// box no longer loses the right-clicked file.
    /// </summary>
    [ObservableProperty] private string? _shellLaunchFilterPath;

    /// <summary>True iff the wizard was opened via the shell-extension
    /// path. Drives a banner at the top of step PickRevision: "You're
    /// restoring an older version of {file}".</summary>
    public bool IsShellLaunchMode => !string.IsNullOrEmpty(ShellLaunchPath);

    /// <summary>True while a shell-launch filter is active — drives the
    /// chip above the file list ("Filtered to: …  Clear").</summary>
    public bool HasShellLaunchFilter => !string.IsNullOrEmpty(ShellLaunchFilterPath);

    partial void OnShellLaunchPathChanged(string? value) => OnPropertyChanged(nameof(IsShellLaunchMode));
    partial void OnShellLaunchFilterPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasShellLaunchFilter));
        ApplyFileFilter();
    }

    /// <summary>
    /// Drops the sticky shell-launch filter so the user can browse the
    /// whole revision (e.g. they want a sibling file in the same folder).
    /// Leaves the user-typed <see cref="FileFilter"/> alone.
    /// </summary>
    [RelayCommand]
    private void ClearShellLaunchFilter() => ShellLaunchFilterPath = null;

    /// <summary>
    /// Entry point for the right-click "Restore an older version" verb.
    /// Picks the most-specific configured backup whose source contains
    /// <paramref name="path"/>, advances the wizard past the picks the
    /// user can already make for them, and pre-filters the file picker
    /// to just that path so they see exactly the file they right-clicked.
    /// </summary>
    public void HandleShellLaunchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShellLaunchPath = null;
            return;
        }

        ShellLaunchPath = path;

        // Find the matching backup + source. Most-specific source wins
        // (longest source-path prefix match).
        var match = FindMatchingBackupSource(path);
        if (match is null)
        {
            // No backup includes this path — leave the user on step 1
            // with a banner. We can't auto-pick, but the message
            // explains why.
            CurrentStep = Step.PickBackup;
            SummaryLine = $"No configured backup includes {path}. Pick one to browse manually.";
            return;
        }

        SelectedBackup = match.Value.Backup;
        SelectedSourcePath = match.Value.SourcePath;
        // Sticky shell-launch filter — applied in addition to whatever
        // the user types in the search box. Survives any edit/clear of
        // FileFilter; only the chip's Clear button removes it.
        ShellLaunchFilterPath = MakeRelativeUnderSource(match.Value.SourcePath, path);
        FileFilter = "";

        // Default the restore target to the safe path (NOT original)
        // so the right-click flow can't surprise-overwrite the user.
        TargetOriginalSource = false;
        CustomTargetPath = SafeRestoreTargetRoot();

        // Drive past the source picker (we picked it) and into the
        // destination + revision flow. Same code path as the manual
        // wizard from this point on.
        _ = EnterDestinationPickerAsync();
    }

    /// <summary>
    /// Default "restore to a different folder" location for both manual
    /// and shell-launched flows: a <c>restore</c> subfolder beside the
    /// app exe. Always-writable, easy to find, and won't accidentally
    /// overwrite source data.
    /// </summary>
    public static string SafeRestoreTargetRoot()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrWhiteSpace(exeDir))
                return Path.Combine(exeDir, "restore");
        }
        catch { /* fall through */ }
        // Fallback: %TEMP%\Duplimate-restore. Always writable.
        return Path.Combine(Path.GetTempPath(), "Duplimate-restore");
    }

    private static (Backup Backup, string SourcePath)? FindMatchingBackupSource(string path)
    {
        var pathFull = SafeFullPath(path);
        if (pathFull is null) return null;

        (Backup, string)? best = null;
        int bestLen = -1;
        foreach (var b in ServiceLocator.Config.Current.Backups)
        {
            foreach (var s in b.SourcePaths)
            {
                var srcFull = SafeFullPath(s);
                if (srcFull is null) continue;
                if (!IsPathUnder(pathFull, srcFull)) continue;
                if (srcFull.Length > bestLen)
                {
                    best = (b, s);
                    bestLen = srcFull.Length;
                }
            }
        }
        return best;
    }

    private static string? SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return null; }
    }

    private static bool IsPathUnder(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeRelativeUnderSource(string sourcePath, string filePath)
    {
        var src = SafeFullPath(sourcePath);
        var file = SafeFullPath(filePath);
        if (src is null || file is null) return filePath;
        if (file.StartsWith(src, StringComparison.OrdinalIgnoreCase))
            return file.Substring(src.Length).TrimStart('\\', '/');
        return filePath;
    }

    /// <summary>True when there is at least one configured backup to restore from.</summary>
    public bool HasAnyBackup => Backups.Count > 0;

    /// <summary>
    /// Inline notice shown above the Next button on the Pick-backup step.
    /// Surfaces the reason the user's Next click didn't move them on —
    /// no selection, the backup never ran, the backup is currently in
    /// flight, etc. Cleared whenever the user picks a different backup.
    /// Earlier the Next button silently no-op'd in these cases, which
    /// looked broken.
    /// </summary>
    [ObservableProperty] private string _pickBackupNoticeText = "";

    partial void OnSelectedBackupChanged(Backup? value)
    {
        PickBackupNoticeText = "";
    }

    /// <summary>Jumps the shell to the Backups page so the user can configure one.</summary>
    [RelayCommand]
    private void GoToBackups() => AppNavigator.Go(NavItem.Backups);

    // ---- transitions ----

    [RelayCommand]
    private async Task NextFromBackupAsync()
    {
        if (SelectedBackup is null)
        {
            PickBackupNoticeText = "Pick a backup first by clicking one of the rows above.";
            return;
        }

        // Don't try to restore from a backup that's currently writing
        // snapshots — the snapshot list is in flux, and the user almost
        // always wants the LAST FULLY-COMMITTED revision rather than a
        // half-finished one. Tell them what to do instead.
        if (ServiceLocator.Orchestrator.IsRunning(SelectedBackup.Id))
        {
            PickBackupNoticeText =
                $"«{SelectedBackup.Name}» is running a backup right now. Wait for it to finish " +
                "(Activity & logs shows progress) and try again — restoring mid-run can read a " +
                "half-finished snapshot.";
            return;
        }

        // Surface "this backup has nothing to restore from yet" up-front
        // rather than waiting for the destination/revision steps to
        // come back empty. NeverRun is the obvious case; a status of
        // Failed with no successful run also has no usable snapshots.
        if (SelectedBackup.LastRunStatus == BackupRunStatus.NeverRun
            || SelectedBackup.LastRunEndUtc is null)
        {
            PickBackupNoticeText =
                $"«{SelectedBackup.Name}» hasn't completed a successful run yet, so there's " +
                "nothing to restore. Run it first (Backups tab → Run now), then come back here.";
            return;
        }
        PickBackupNoticeText = "";

        // Multi-source backups need the user to pick which source(s)
        // they want to restore from — each source is its own Duplicacy
        // repo with its own revision timeline. The picker is checkbox-
        // based so the user can select more than one in a single pass;
        // every row defaults to checked (the common case is "restore
        // everything from this backup at the latest revision"). Single-
        // source skips straight through to destination selection.
        SourcesForBackup.Clear();
        foreach (var s in SelectedBackup.SourcePaths)
            SourcesForBackup.Add(new SourcePickRow(s, this, isSelected: true));
        OnPropertyChanged(nameof(HasAnyCheckedSource));

        if (SourcesForBackup.Count > 1)
        {
            SelectedSourcePath = null;
            MultiSourceMode = false;
            CurrentStep = Step.PickSource;
            return;
        }

        // Single source: pick it implicitly and continue.
        SelectedSourcePath = SourcesForBackup.FirstOrDefault()?.Path;
        MultiSourceMode = false;
        await EnterDestinationPickerAsync();
    }

    /// <summary>Called by <see cref="SourcePickRow"/> whenever a
    /// checkbox toggles. Re-fires the dependent properties so the
    /// PickSource step's Next button updates its enable state and
    /// the multi-source target label reflects the current count.</summary>
    public void OnSourcePickRowToggled()
    {
        OnPropertyChanged(nameof(HasAnyCheckedSource));
        OnPropertyChanged(nameof(OriginalSourceTargetLabel));
    }

    [RelayCommand]
    private async Task NextFromSourceAsync()
    {
        if (SelectedBackup is null) return;
        var picked = CheckedSourcePaths;
        if (picked.Count == 0) return;
        if (picked.Count == 1)
        {
            // Same path as a single-source backup: pick implicitly,
            // walk the revision + file picker.
            SelectedSourcePath = picked[0];
            MultiSourceMode = false;
            await EnterDestinationPickerAsync();
            return;
        }

        // Multi-source: skip per-source revision + file picking.
        // The wizard restores the latest revision of every checked
        // source, all files, into the chosen target. The per-source
        // SelectedSourcePath gets set during the run loop in
        // NextFromTargetAsync.
        MultiSourceMode = true;
        SelectedSourcePath = picked[0];
        await EnterDestinationPickerAsync();
    }

    /// <summary>
    /// After source is chosen (either implicitly for single-source or
    /// explicitly in step PickSource), populate destination list and
    /// either short-circuit (single destination) or show the picker.
    /// </summary>
    private Task EnterDestinationPickerAsync()
    {
        if (SelectedBackup is null) return Task.CompletedTask;

        DestinationsForBackup.Clear();
        DestinationPickRows.Clear();
        foreach (var t in SelectedBackup.Targets)
        {
            var d = ServiceLocator.Config.Current.Destinations.FirstOrDefault(x => x.Id == t.DestinationId);
            if (d is not null)
            {
                DestinationsForBackup.Add(d);
                DestinationPickRows.Add(new DestinationPickRow(d, isSelected: false));
            }
        }

        if (DestinationsForBackup.Count == 1)
        {
            SelectedDestination = DestinationsForBackup[0];
            DestinationPickRows[0].IsSelected = true;
            SelectedStorageName = SelectedBackup.Targets[0].StorageName;
            // Revision listing fires on the dispatcher inside
            // EnterAfterDestinationAsync — no await needed; CurrentStep
            // flips synchronously so the user sees the spinner pane
            // immediately.
            EnterAfterDestinationAsync();
        }
        else
        {
            CurrentStep = Step.PickDestination;
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void NextFromDestination()
    {
        if (SelectedBackup is null || SelectedDestination is null) return;
        var target = SelectedBackup.Targets.FirstOrDefault(t => t.DestinationId == SelectedDestination.Id);
        SelectedStorageName = target?.StorageName ?? "default";
        EnterAfterDestinationAsync();
    }

    /// <summary>
    /// Pick a destination from the PickDestination step's row list.
    /// Each row's Button is bound to this command with the row's
    /// <see cref="DestinationPickRow"/> as <c>CommandParameter</c>.
    /// We update the per-row <see cref="DestinationPickRow.IsSelected"/>
    /// directly (so the radio dot animates) AND mirror the choice
    /// into the legacy <see cref="SelectedDestination"/> property
    /// the rest of the wizard reads. Earlier the radio dot was bound
    /// through a parent-relative RefEq chain that didn't propagate
    /// SelectedDestination's PropertyChanged back to the row — a
    /// real interaction test
    /// (<c>InteractionDestinationClickTests</c>) catches that
    /// regression now.
    /// </summary>
    [RelayCommand]
    private void PickDestination(DestinationPickRow? row)
    {
        if (row is null) return;
        foreach (var r in DestinationPickRows)
            r.IsSelected = ReferenceEquals(r, row);
        SelectedDestination = row.Destination;
    }

    /// <summary>
    /// Branch after a destination is chosen. Both single-source and
    /// multi-source modes now route through PickRevision so the user
    /// always sees what they're about to restore (and how old it is)
    /// — the user explicitly asked: "Always show revision even if
    /// there is only one (helpful for user to see how old it is)".
    ///
    /// In single-source mode, PickRevision is a picker (the user can
    /// choose any historical revision to restore). In multi-source
    /// mode, it's a read-only summary listing each picked source's
    /// latest revision; PickFiles is bypassed (the wizard restores
    /// every file of each source's latest revision in one pass).
    ///
    /// CurrentStep advances IMMEDIATELY (not after the listing
    /// completes) so the user sees the new step with its loading
    /// spinner instead of staring at a disabled "Next" button while
    /// duplicacy.exe spawns and lists the storage. The user reported:
    /// "After I click pick a revision, the button gets disabled and
    /// it takes a while to go to the next step." The fix is to flip
    /// the order: navigate first, load second.
    /// </summary>
    private void EnterAfterDestinationAsync()
    {
        // Set the loading flag eagerly so the spinner is visible the
        // instant the user lands on the revision step (the actual
        // load runs on the dispatcher below and would otherwise have
        // a brief gap where the step is shown but Loading=false).
        LoadingRevisions = true;
        if (MultiSourceMode)
        {
            MultiSourceRevisions.Clear();
            OnPropertyChanged(nameof(HasMultiSourceRevisions));
        }
        else
        {
            Revisions.Clear();
            OnPropertyChanged(nameof(HasRevisions));
            OnPropertyChanged(nameof(ShowNoRevisionsHint));
        }

        CurrentStep = Step.PickRevision;

        // Cancel any previous in-flight revision listing — a rapid
        // Back→Next sequence used to leave the prior load racing the
        // new one against the same Revisions collection. Capture the
        // token BEFORE dispatching so the loader sees this exact
        // session's cancellation, not a later one's.
        try { _revisionLoadCts?.Cancel(); _revisionLoadCts?.Dispose(); } catch { }
        _revisionLoadCts = new CancellationTokenSource();
        var ct = _revisionLoadCts.Token;

        // Fire-and-forget the listing. Exceptions are caught inside
        // each loader and surfaced via LoadError; the dispatcher
        // posts the work back to the UI thread.
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (MultiSourceMode) await LoadMultiSourceLatestRevisionsAsync(ct);
            else                 await LoadRevisionsAsync(ct);
        });
    }

    /// <summary>
    /// Populate <see cref="MultiSourceRevisions"/> with each picked
    /// source's latest revision summary. Runs every source's revision
    /// listing in sequence (Duplicacy's list command is per-repo, not
    /// per-storage globally) and takes the head of each. A source
    /// with no revisions yet lands as a row with null
    /// <see cref="MultiSourceRevisionRow.RevisionNumber"/> so the UI
    /// can render "no revisions yet" rather than dropping the row —
    /// the user still wants to see that source was considered.
    /// </summary>
    private async Task LoadMultiSourceLatestRevisionsAsync(CancellationToken ct)
    {
        if (SelectedBackup is null || SelectedDestination is null) return;
        LoadingRevisions = true;
        LoadError = null;
        MultiSourceRevisions.Clear();
        OnPropertyChanged(nameof(HasMultiSourceRevisions));
        try
        {
            foreach (var path in CheckedSourcePaths)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var list = await ServiceLocator.Revisions.ListRevisionsAsync(
                        SelectedBackup, path, SelectedDestination, SelectedStorageName, ct);
                    if (ct.IsCancellationRequested) return;
                    var head = list.FirstOrDefault();
                    MultiSourceRevisions.Add(new MultiSourceRevisionRow(
                        path,
                        head.Number == 0 && list.Count == 0 ? null : head.Number,
                        head.Number == 0 && list.Count == 0 ? null : head.CreatedUtc,
                        head.FileCount,
                        head.TotalBytes));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // A per-source list failure shouldn't tank the whole
                    // step. Log it and surface a "no revisions yet" row
                    // for that path so the user still sees something.
                    AppLogger.For<RestoreViewModel>().Warning(ex,
                        "Multi-source latest-revision lookup failed for {Source}", path);
                    MultiSourceRevisions.Add(new MultiSourceRevisionRow(path, null, null, 0, 0));
                }
            }
            OnPropertyChanged(nameof(HasMultiSourceRevisions));
        }
        finally
        {
            // Only flip Loading off if we're still the active session.
            // A cancelled load means EnterAfterDestinationAsync fired
            // a successor that's already set Loading=true; clearing it
            // here would race with the successor.
            if (!ct.IsCancellationRequested) LoadingRevisions = false;
        }
    }

    /// <summary>True iff at least one multi-source row was populated —
    /// drives the multi-source list visibility on the PickRevision
    /// step. False before the load fires or when every source's
    /// listing failed.</summary>
    public bool HasMultiSourceRevisions => MultiSourceRevisions.Count > 0;

    [RelayCommand]
    private async Task NextFromRevisionAsync()
    {
        // In multi-source mode the revision step is a read-only
        // summary, not a picker — there's no SelectedRevision and
        // file picking is bypassed (every file of each source's
        // latest revision is restored). Skip straight to PickTarget.
        if (MultiSourceMode)
        {
            CurrentStep = Step.PickTarget;
            return;
        }
        if (SelectedRevision is null) return;
        await LoadFilesAsync();
        CurrentStep = Step.PickFiles;
    }

    [RelayCommand]
    private void NextFromFiles()
    {
        if (SelectedFilesCount == 0) return;
        CurrentStep = Step.PickTarget;
    }

    [RelayCommand]
    private async Task NextFromTargetAsync()
    {
        if (SelectedBackup is null || SelectedDestination is null) return;
        // Single-source mode: needs a picked revision. Multi-source
        // mode resolves latest revision per source at run time so
        // SelectedRevision can be null here.
        if (!MultiSourceMode && SelectedRevision is null) return;

        if (MultiSourceMode)
        {
            await RunMultiSourceRestoreAsync();
            return;
        }

        // Snapshot EVERY input the restore depends on BEFORE any await.
        // The OVERWRITE dialog can sit on screen for an arbitrary amount
        // of time, during which Config.Changed -> Refresh could rebuild
        // SelectedBackup/SelectedDestination, and AllFiles could be
        // cleared (which empties the live SelectedPaths projection).
        // Resolving any of these AFTER the await would mix the user's
        // chosen revision with state from a later refresh — wrong files,
        // wrong destination, or NRE on a cleared SelectedRevision.
        var capturedBackup = SelectedBackup;
        var capturedDestination = SelectedDestination;
        var capturedRevisionNumber = SelectedRevision!.Value.Number;
        var capturedSourcePath = SelectedSourcePath ?? capturedBackup.SourcePaths.FirstOrDefault() ?? "";
        var capturedStorageName = SelectedStorageName;
        var capturedFiles = SelectedPaths.ToList();
        var capturedOverwrite = OverwriteExisting;
        var capturedPreserveStructure = PreserveStructure;
        var capturedThreads = Threads;
        var capturedRetryDelays = ServiceLocator.Config.Current.Restore.RetryDelaysSeconds;

        var target = TargetOriginalSource
            ? capturedSourcePath
            : (CustomTargetPath?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(target))
        {
            ResetProgressLog("Pick a target folder first.\n");
            return;
        }

        // Type-OK gate when restoring onto the original source — same
        // confirm shape used for backup delete + destination wipe so
        // the user has one mental model for "this is destructive".
        if (TargetOriginalSource)
        {
            var owner = OwnerWindow();
            if (owner is null) return;
            // Step 5 of the wizard tells the user "you'll need to type
            // OVERWRITE to confirm" — the dialog must match that promise
            // exactly, otherwise users read OVERWRITE, type OVERWRITE,
            // hit a confusing "wrong value" red flash, and lose trust
            // in the rest of the destructive flow. Case-insensitive so
            // "Overwrite" and "overwrite" both work.
            var dlg = new DestructiveConfirmWindow(
                title: "Restore will overwrite original files",
                subtitle:
                    $"Files under «{target}» will be replaced with their state at revision {capturedRevisionNumber}. " +
                    "There's no undo. Type OVERWRITE to proceed.",
                confirmString: "OVERWRITE",
                caseInsensitive: true);
            var ok = await dlg.ShowDialog<bool>(owner);
            if (!ok) return;
        }

        _currentRequest = new RestoreRequest
        {
            Backup = capturedBackup,
            SourcePath = capturedSourcePath,
            Destination = capturedDestination,
            StorageName = capturedStorageName,
            Revision = capturedRevisionNumber,
            Files = capturedFiles,
            TargetPath = target,
            Overwrite = capturedOverwrite,
            PreserveStructure = capturedPreserveStructure,
            Threads = capturedThreads,
            RetryDelaysSeconds = capturedRetryDelays,
        };

        TotalFiles = _currentRequest.Files.Count;
        RestoredCount = 0;
        FailedCount = 0;
        InFlightCount = 0;
        ResetProgressLog();
        FailuresList.Clear();
        CurrentStep = Step.Progress;

        // Dispose any prior CTS before reassigning. Each restore-then-
        // retry cycle previously leaked the prior CancellationTokenSource
        // (and its WaitHandle).
        try { _cts?.Dispose(); } catch { }
        _cts = new CancellationTokenSource();
        _restoreRunStartedUtc = DateTime.UtcNow;
        BeginInFlightRestoreRun();
        IsRunning = true;
        var engine = ServiceLocator.Restore;
        engine.Log += OnEngineLog;
        engine.Progress += OnEngineProgress;
        engine.FileFailed += OnFileFailed;
        engine.FileRestored += OnFileRestored;
        try
        {
            _currentOutcome = await engine.RunAsync(_currentRequest, _cts.Token);
        }
        catch (OperationCanceledException) { /* user cancelled */ }
        finally
        {
            engine.Log -= OnEngineLog;
            engine.Progress -= OnEngineProgress;
            engine.FileFailed -= OnFileFailed;
            engine.FileRestored -= OnFileRestored;
            IsRunning = false;
            EndInFlightRestoreRun();
        }

        PersistRestoreRunRecord(RunKind.Restore);
        BuildSummary();
        CurrentStep = Step.Summary;
    }

    /// <summary>
    /// Multi-source restore loop. For each checked source: resolves
    /// the latest revision via the RevisionBrowser, lists every file
    /// in that revision, builds a per-source RestoreRequest, and
    /// runs RestoreEngine sequentially. Restored files are written
    /// under the user's chosen target preserving each source's
    /// directory structure (the only sensible default — without
    /// PreserveStructure two sources containing similarly-named
    /// files would clobber each other).
    /// <para>
    /// "Restore to the original source" means each source restores
    /// in place; "different folder" means a `&lt;target&gt;\&lt;source-slug&gt;`
    /// subdir per source so the trees don't collide. The OVERWRITE
    /// gate fires once for the multi-source restore as a whole when
    /// any source is restoring in-place.
    /// </para>
    /// </summary>
    private async Task RunMultiSourceRestoreAsync()
    {
        if (SelectedBackup is null || SelectedDestination is null) return;

        // Snapshot every input BEFORE the OVERWRITE dialog (which can
        // sit on screen indefinitely) and BEFORE the loop begins.
        // Includes TargetOriginalSource — the loop's per-source target
        // formula reads it, and we don't want a Config.Changed-driven
        // refresh or a wayward UI tick to flip the choice mid-loop and
        // restore half the sources to one path, half to another.
        var capturedBackup = SelectedBackup;
        var capturedDestination = SelectedDestination;
        var capturedStorageName = SelectedStorageName;
        var capturedOverwrite = OverwriteExisting;
        var capturedThreads = Threads;
        var capturedRetryDelays = ServiceLocator.Config.Current.Restore.RetryDelaysSeconds;
        var capturedTargetOriginalSource = TargetOriginalSource;
        var pickedSources = CheckedSourcePaths;
        if (pickedSources.Count == 0) return;
        var targetRoot = capturedTargetOriginalSource ? null : (CustomTargetPath?.Trim() ?? "");
        if (!capturedTargetOriginalSource && string.IsNullOrWhiteSpace(targetRoot))
        {
            ResetProgressLog("Pick a target folder first.\n");
            return;
        }
        if (capturedTargetOriginalSource)
        {
            var owner = OwnerWindow();
            if (owner is null) return;
            var dlg = new DestructiveConfirmWindow(
                title: $"Restore will overwrite {pickedSources.Count} source folders",
                subtitle:
                    "Each source you ticked will be replaced with its state at the latest revision. " +
                    "There's no undo. Type OVERWRITE to proceed.",
                confirmString: "OVERWRITE",
                caseInsensitive: true);
            var ok = await dlg.ShowDialog<bool>(owner);
            if (!ok) return;
        }

        // Reset progress UI + transition to Progress step.
        TotalFiles = 0;
        RestoredCount = 0;
        FailedCount = 0;
        InFlightCount = 0;
        ResetProgressLog();
        FailuresList.Clear();
        // Reset multi-source baselines so OnEngineProgress doesn't
        // inherit counters from a previous wizard pass. They climb
        // monotonically as iterations finish (see end of loop body).
        _msBaselineTotal = 0;
        _msBaselineRestored = 0;
        _msBaselineFailed = 0;
        CurrentStep = Step.Progress;

        try { _cts?.Dispose(); } catch { }
        _cts = new CancellationTokenSource();
        _restoreRunStartedUtc = DateTime.UtcNow;
        BeginInFlightRestoreRun();
        IsRunning = true;
        var engine = ServiceLocator.Restore;
        engine.Log += OnEngineLog;
        engine.Progress += OnEngineProgress;
        engine.FileFailed += OnFileFailed;
        engine.FileRestored += OnFileRestored;

        RestoreOutcome? lastOutcome = null;
        try
        {
            foreach (var src in pickedSources)
            {
                if (_cts.Token.IsCancellationRequested) break;
                SelectedSourcePath = src;
                AppendProgressLine($"=== Source: {src} ===");
                // Latest revision for this source.
                IReadOnlyList<RevisionSummary> revs;
                try
                {
                    revs = await ServiceLocator.Revisions.ListRevisionsAsync(
                        capturedBackup, src, capturedDestination, capturedStorageName, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppendProgressLine($"[error] couldn't list revisions for {src}: {ex.Message}");
                    continue;
                }
                if (revs.Count == 0)
                {
                    AppendProgressLine($"[skip] no snapshots at this destination for {src}");
                    continue;
                }
                var latest = revs.OrderByDescending(r => r.Number).First();

                // List every file in the latest revision so we can
                // populate the RestoreRequest. Without an explicit
                // file list the engine doesn't know what to write to
                // disk.
                IReadOnlyList<RevisionFile> files;
                try
                {
                    files = await ServiceLocator.Revisions.ListFilesAsync(
                        capturedBackup, src, capturedDestination, capturedStorageName, latest.Number, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppendProgressLine($"[error] couldn't list files in revision {latest.Number} for {src}: {ex.Message}");
                    continue;
                }
                if (files.Count == 0)
                {
                    AppendProgressLine($"[skip] revision {latest.Number} has no files for {src}");
                    continue;
                }
                TotalFiles += files.Count;

                // Per-source target. Original-source mode → write back
                // to the source path. Custom-target mode → put each
                // source under its own subfolder under the chosen
                // root so two sources don't clobber each other's tree.
                var perSourceTarget = capturedTargetOriginalSource
                    ? src
                    : System.IO.Path.Combine(targetRoot!, MultiSourceSubfolderName(src));

                _currentRequest = new RestoreRequest
                {
                    Backup = capturedBackup,
                    SourcePath = src,
                    Destination = capturedDestination,
                    StorageName = capturedStorageName,
                    Revision = latest.Number,
                    Files = files.Select(f => f.Path).ToList(),
                    TargetPath = perSourceTarget,
                    Overwrite = capturedOverwrite,
                    // Honour the user's "Preserve directory structure"
                    // toggle from the Target step. Earlier this was
                    // hard-coded to true and the UI checkbox was
                    // silently ignored in multi-source mode. With per-
                    // source target subdirs the toggle is still
                    // meaningful — false flattens each source's tree
                    // under its own subfolder, which some users want
                    // for a quick "give me everything in one place".
                    PreserveStructure = PreserveStructure,
                    Threads = capturedThreads,
                    RetryDelaysSeconds = capturedRetryDelays,
                };
                try { System.IO.Directory.CreateDirectory(perSourceTarget); }
                catch (Exception ex) { AppendProgressLine($"[warn] couldn't pre-create {perSourceTarget}: {ex.Message}"); }

                try
                {
                    lastOutcome = await engine.RunAsync(_currentRequest, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppendProgressLine($"[error] restore for {src} threw: {ex.Message}");
                }

                // Roll the per-iteration outcome into the multi-source
                // baselines. The next iteration's engine progress
                // events fire with a fresh outcome (counts reset to 0
                // inside the engine), so without this the displayed
                // counters would visibly snap back to whatever the new
                // source's first chunk reports — losing every prior
                // source's contribution.
                //
                // Total uses the SAME pre-flight files.Count that drove
                // the running counter at line 1002 (and that the engine
                // itself initialised s.Total to). Using
                // lastOutcome.Files.Count here would diverge if the
                // engine dropped any files — TotalFiles would visibly
                // jump backwards on iteration boundaries.
                if (lastOutcome is not null)
                {
                    _msBaselineTotal += files.Count;
                    _msBaselineRestored += lastOutcome.Files.Values
                        .Count(f => f.Status == RestoreFileStatus.Restored);
                    _msBaselineFailed += lastOutcome.Files.Values
                        .Count(f => f.Status == RestoreFileStatus.Failed);
                }
            }
            // The Summary screen reads _currentOutcome — for multi-
            // source we surface the LAST source's outcome, with the
            // overall ProgressLog telling the bigger story.
            _currentOutcome = lastOutcome;
        }
        finally
        {
            engine.Log -= OnEngineLog;
            engine.Progress -= OnEngineProgress;
            engine.FileFailed -= OnFileFailed;
            engine.FileRestored -= OnFileRestored;
            IsRunning = false;
            EndInFlightRestoreRun();
        }

        PersistRestoreRunRecord(RunKind.Restore);
        BuildSummary();
        CurrentStep = Step.Summary;
    }

    /// <summary>
    /// Filesystem-safe subfolder name for a multi-source restore so
    /// "C:\Users\…\Documents" and "C:\Users\…\Pictures" don't collide
    /// when restored under the same custom target. Uses the leaf
    /// directory name + a 6-char hash of the full path so two
    /// "Documents" folders from different drives stay distinct.
    /// </summary>
    private static string MultiSourceSubfolderName(string sourcePath)
    {
        try
        {
            var leaf = new System.IO.DirectoryInfo(sourcePath.TrimEnd('\\', '/')).Name;
            if (string.IsNullOrEmpty(leaf)) leaf = "source";
            var hashBytes = System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(sourcePath));
            var hash = Convert.ToHexString(hashBytes, 0, 3).ToLowerInvariant();
            // Strip filesystem-illegal chars from leaf as a precaution.
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                leaf = leaf.Replace(ch, '_');
            return $"{leaf}-{hash}";
        }
        catch
        {
            return "source-" + Math.Abs(sourcePath.GetHashCode()).ToString("x");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private void Back()
    {
        // Going back from PickRevision (or any step that triggered a
        // revision listing) means the user is no longer interested in
        // that listing's result — cancel any in-flight load so a
        // late completion can't stomp the destination/source picker
        // they're rewinding into.
        try { _revisionLoadCts?.Cancel(); } catch { }

        CurrentStep = CurrentStep switch
        {
            Step.PickSource      => Step.PickBackup,
            Step.PickDestination => SourcesForBackup.Count > 1 ? Step.PickSource : Step.PickBackup,
            // From PickRevision, rewind past whichever steps were skipped
            // forward: if destination was auto-picked (1 target) and
            // source was auto-picked (1 source), we go all the way back
            // to PickBackup; otherwise to the most-recent picker shown.
            Step.PickRevision    => DestinationsForBackup.Count > 1
                                        ? Step.PickDestination
                                        : SourcesForBackup.Count > 1
                                            ? Step.PickSource
                                            : Step.PickBackup,
            Step.PickFiles       => Step.PickRevision,
            // Both modes route through PickRevision now (single-source:
            // a picker; multi-source: a read-only latest-revision
            // summary). Single-source goes via PickFiles; multi-source
            // skips PickFiles and so rewinds straight to PickRevision.
            Step.PickTarget      => MultiSourceMode
                                        ? Step.PickRevision
                                        : Step.PickFiles,
            _                    => CurrentStep,
        };
    }

    [RelayCommand]
    private void StartOver() => RestartWizard();

    /// <summary>
    /// Public restart used by the nav layer: clicking "Restore files"
    /// in the sidebar (or programmatic nav from a card) should always
    /// land on step 1 with no stale prior selections, even if a
    /// previous wizard session left mid-flow.
    /// </summary>
    public void RestartWizard()
    {
        SelectedBackup = null;
        SelectedDestination = null;
        SelectedRevision = null;
        SelectedSourcePath = null;
        SourcesForBackup.Clear();
        MultiSourceMode = false;
        AllFiles.Clear();
        FilteredFiles.Clear();
        SelectedFilesCount = 0;
        SelectedFilesBytes = 0;
        ResetProgressLog();
        FailuresList.Clear();
        ShellLaunchPath = null;
        ShellLaunchFilterPath = null;
        FileFilter = "";
        _currentOutcome = null;
        _currentRequest = null;
        CurrentStep = Step.PickBackup;
    }

    [RelayCommand]
    private async Task RetryAllFailedAsync()
    {
        if (_currentRequest is null || _currentOutcome is null) return;
        CurrentStep = Step.Progress;
        IsRunning = true;
        try { _cts?.Dispose(); } catch { }
        _cts = new CancellationTokenSource();
        _restoreRunStartedUtc = DateTime.UtcNow;
        BeginInFlightRestoreRun();
        // Re-signal live activity so the LogsView pivots to the new
        // synthetic Running entry — the prior _inFlightRunRecord (if
        // any) was unregistered when the previous restore ended, and
        // the dropdown's selection is now stale.
        AppNavigator.SignalLiveActivity(SelectedBackup?.Name);
        var engine = ServiceLocator.Restore;
        engine.Log += OnEngineLog;
        engine.Progress += OnEngineProgress;
        engine.FileFailed += OnFileFailed;
        engine.FileRestored += OnFileRestored;
        try
        {
            _currentOutcome = await engine.RetryFailedAsync(_currentRequest, _currentOutcome, _cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            engine.Log -= OnEngineLog;
            engine.Progress -= OnEngineProgress;
            engine.FileFailed -= OnFileFailed;
            engine.FileRestored -= OnFileRestored;
            IsRunning = false;
            EndInFlightRestoreRun();
        }
        PersistRestoreRunRecord(RunKind.Restore);
        BuildSummary();
        CurrentStep = Step.Summary;
    }

    [RelayCommand]
    private async Task BrowseCustomTargetAsync()
    {
        var owner = OwnerWindow();
        if (owner is null) return;
        var picks = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a folder to restore into",
            AllowMultiple = false,
        });
        if (picks?.Count > 0)
        {
            var p = picks[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) CustomTargetPath = p;
        }
    }

    [RelayCommand]
    private void OpenTargetFolder()
    {
        if (_currentOutcome is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _currentOutcome.TargetPath,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    // ---- file list helpers ----

    partial void OnFileFilterChanged(string value) => ApplyFileFilter();

    private void ApplyFileFilter()
    {
        FilteredFiles.Clear();
        IEnumerable<RevisionFileRow> src = AllFiles;
        // Shell-launch filter applies first and is sticky — surviving any
        // edit to FileFilter so right-click-restored users don't lose the
        // narrow they were given. The user-typed FileFilter further
        // narrows on top of it.
        if (!string.IsNullOrWhiteSpace(ShellLaunchFilterPath))
        {
            var sticky = ShellLaunchFilterPath.Trim();
            src = src.Where(r => r.File.Path.Contains(sticky, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(FileFilter))
        {
            var q = FileFilter.Trim();
            src = src.Where(r => r.File.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var r in src.Take(5000))
            FilteredFiles.Add(r);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in AllFiles) r.IsSelected = true;
        RecomputeSelectionTotals();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in AllFiles) r.IsSelected = false;
        RecomputeSelectionTotals();
    }

    /// <summary>
    /// Called by RevisionFileRow when its IsSelected changes, so the header
    /// counter stays in sync without any binding gymnastics.
    /// </summary>
    internal void RecomputeSelectionTotals()
    {
        SelectedFilesCount = AllFiles.Count(r => r.IsSelected);
        SelectedFilesBytes = AllFiles.Where(r => r.IsSelected).Sum(r => r.File.SizeBytes);
    }

    // ---- loaders ----

    [ObservableProperty] private string? _loadError;

    /// <summary>Re-fire the derived "should I show the empty hint?"
    /// property whenever any of its inputs change. Without these the
    /// placeholder/listbox visibility wouldn't toggle as state moves
    /// between loading → error → loaded-empty → loaded-with-data.</summary>
    partial void OnLoadingRevisionsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoRevisionsHint));
    }
    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowNoRevisionsHint));
    }

    private async Task LoadRevisionsAsync(CancellationToken ct = default)
    {
        if (SelectedBackup is null || SelectedDestination is null) return;
        var source = SelectedSourcePath ?? SelectedBackup.SourcePaths.FirstOrDefault();
        if (string.IsNullOrEmpty(source))
        {
            LoadError = "This backup has no source paths configured.";
            return;
        }
        // Snapshot the previously-selected revision number so we can
        // restore the selection after the reload — without this, a
        // Config.Changed-driven refresh during the wizard always jumped
        // the user back to the latest revision, which is wrong if they
        // were inspecting an older one.
        var previouslySelectedNumber = SelectedRevision?.Number;
        LoadingRevisions = true;
        LoadError = null;
        Revisions.Clear();
        OnPropertyChanged(nameof(HasRevisions));
        OnPropertyChanged(nameof(ShowNoRevisionsHint));
        try
        {
            var list = await ServiceLocator.Revisions.ListRevisionsAsync(
                SelectedBackup, source, SelectedDestination, SelectedStorageName, ct);
            // If the user clicked Back (or re-navigated through the
            // destination step) while this listing was in flight,
            // bail before touching the bound collections — a successor
            // load is already running against a clean Revisions list
            // and we'd otherwise stomp its in-progress data.
            if (ct.IsCancellationRequested) return;
            foreach (var r in list) Revisions.Add(r);
            // Re-apply the previous selection if the same revision is
            // still in the list; otherwise default to the latest. If
            // the list is empty, leave SelectedRevision as null —
            // RevisionSummary is a struct, so a naïve
            // Revisions.FirstOrDefault() returns default(RevisionSummary)
            // (a zero-numbered struct) which the IsNotNull-bound Next
            // button reads as "selection present" and stays enabled.
            // The user reported: "If there are no revisions to pick
            // from, it still offers to click on Next: pick files to
            // restore."
            if (Revisions.Count == 0)
            {
                SelectedRevision = null;
            }
            else
            {
                SelectedRevision = previouslySelectedNumber is int prev
                    ? Revisions.FirstOrDefault(r => r.Number == prev) is { } match ? match : Revisions[0]
                    : Revisions[0];
            }
            // No LoadError on an empty result — the dedicated empty-state
            // placeholder ("no revisions yet — run the backup once first")
            // is more informative than a red callout that implies
            // something is broken when nothing is. LoadError is reserved
            // for genuine "we couldn't even ask" failures (catch block
            // below).
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            LoadError = "Something went wrong while reading revisions: " + ex.Message;
        }
        finally
        {
            // Same successor-race guard as the multi-source loader —
            // see LoadMultiSourceLatestRevisionsAsync.
            if (!ct.IsCancellationRequested)
            {
                LoadingRevisions = false;
                OnPropertyChanged(nameof(HasRevisions));
                OnPropertyChanged(nameof(ShowNoRevisionsHint));
            }
        }
    }

    /// <summary>
    /// A snapshot with millions of files would blow out memory if we
    /// materialized them all. Cap at this count; users who actually want the
    /// whole tree can drill in with the search box.
    /// </summary>
    public const int MaxFilesInRestoreList = 200_000;

    private async Task LoadFilesAsync()
    {
        if (SelectedBackup is null || SelectedDestination is null || SelectedRevision is null) return;
        var source = SelectedSourcePath ?? SelectedBackup.SourcePaths.FirstOrDefault();
        if (string.IsNullOrEmpty(source))
        {
            LoadError = "This backup has no source paths configured.";
            return;
        }
        LoadingFiles = true;
        LoadError = null;
        AllFiles.Clear();
        FilteredFiles.Clear();
        try
        {
            var list = await ServiceLocator.Revisions.ListFilesAsync(
                SelectedBackup, source, SelectedDestination, SelectedStorageName,
                SelectedRevision.Value.Number, CancellationToken.None);

            if (list.Count > MaxFilesInRestoreList)
            {
                LoadError = $"This revision has {list.Count:N0} files — showing the first " +
                            $"{MaxFilesInRestoreList:N0}. Use the search box above to find specific files.";
                foreach (var f in list.Take(MaxFilesInRestoreList))
                    AllFiles.Add(new RevisionFileRow(f, this));
            }
            else
            {
                foreach (var f in list) AllFiles.Add(new RevisionFileRow(f, this));
            }
            ApplyFileFilter();
        }
        catch (Exception ex)
        {
            LoadError = "Something went wrong while reading files: " + ex.Message;
        }
        finally { LoadingFiles = false; RecomputeSelectionTotals(); }
    }

    // ---- engine callbacks (marshaled to UI thread) ----

    private void OnEngineLog(string line) =>
        Dispatcher.UIThread.Post(() =>
        {
            // ALWAYS append every line to the raw buffer — verbose is
            // a DISPLAY filter, not a recording filter, so the
            // persisted .log file (written from this same buffer)
            // captures the full duplicacy output regardless of the
            // setting. Materialization to the bound ProgressLog
            // applies the filter (see ScheduleProgressLogFlush).
            AppendProgressLine(line);
        });

    // ---- ProgressLog buffer ----
    // Earlier the code did `ProgressLog += line + "\n"` per line plus a
    // mid-string slice for trim. That's O(n²) over the run and slices
    // mid-line on truncation (the surviving leading "line" is a partial
    // fragment). The buffer below holds the live text in a StringBuilder
    // and rebuilds the bound string only on a 150ms debounce — same
    // pattern as LogsViewModel.LiveTail. Trim drops the oldest chunk
    // aligned to the next newline so the head of the buffer always
    // starts at a complete line.
    private const int ProgressLogHardCap = 400_000;
    private const int ProgressLogKeepAfterTrim = 200_000;
    private static readonly TimeSpan ProgressLogFlushInterval = TimeSpan.FromMilliseconds(150);
    private readonly System.Text.StringBuilder _progressLogBuffer = new(capacity: 64 * 1024);
    private bool _progressLogFlushScheduled;

    /// <summary>
    /// Append a line to the restore progress log, marshaling to the UI
    /// thread if necessary. Adds a trailing newline if the caller's
    /// text doesn't already end with one. Triggers a debounced flush
    /// of <see cref="ProgressLog"/> so the bound TextBox doesn't
    /// re-render once per line on a verbose restore (5–10k lines/sec).
    /// </summary>
    private void AppendProgressLine(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendProgressLine(text));
            return;
        }
        _progressLogBuffer.Append(text);
        if (text.Length == 0 || text[^1] != '\n') _progressLogBuffer.Append('\n');
        TrimProgressLogIfNeeded();
        ScheduleProgressLogFlush();
    }

    // ---- test seams (internal so tests can drive the buffer
    // synchronously without waiting on the 150ms debounced flush) ----
    internal void AppendProgressLineForTesting(string text) => AppendProgressLine(text);
    internal string ProgressLogBufferSnapshot() => _progressLogBuffer.ToString();
    internal int ProgressLogBufferLength => _progressLogBuffer.Length;

    /// <summary>Reset the progress log to <paramref name="initial"/>
    /// (defaults to empty). Synchronous — clears the SB and flips the
    /// bound property in one go so the next OnEngineLog starts from a
    /// clean state. Replaces the previous <c>ProgressLog = ""</c>
    /// pattern that left the buffer out of sync with the bound
    /// string.</summary>
    private void ResetProgressLog(string initial = "")
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ResetProgressLog(initial));
            return;
        }
        _progressLogBuffer.Clear();
        if (!string.IsNullOrEmpty(initial)) _progressLogBuffer.Append(initial);
        _progressLogFlushScheduled = false;
        ProgressLog = MaterializeProgressLog();
    }

    /// <summary>Materialize the bound <see cref="ProgressLog"/> from
    /// the raw <see cref="_progressLogBuffer"/> applying the verbose
    /// filter. Verbose off → DEBUG/TRACE lines stripped from display
    /// only; the buffer (and any persistence path that reads it)
    /// stays untouched.</summary>
    private string MaterializeProgressLog() =>
        VerboseLogFilter.FilterForDisplay(_progressLogBuffer.ToString());

    private void TrimProgressLogIfNeeded()
    {
        if (_progressLogBuffer.Length <= ProgressLogHardCap) return;
        var dropFrom = _progressLogBuffer.Length - ProgressLogKeepAfterTrim;
        // Snap the trim point forward to the next '\n' so the head of
        // the survivor buffer is always a complete line, never a
        // half-truncated fragment. Cap the search at +2 KB so a
        // pathological no-newline blob doesn't scan the whole buffer.
        var snap = -1;
        var ceiling = System.Math.Min(_progressLogBuffer.Length, dropFrom + 2048);
        for (int i = dropFrom; i < ceiling; i++)
        {
            if (_progressLogBuffer[i] == '\n') { snap = i + 1; break; }
        }
        _progressLogBuffer.Remove(0, snap > 0 ? snap : dropFrom);
    }

    private void ScheduleProgressLogFlush()
    {
        if (_progressLogFlushScheduled) return;
        _progressLogFlushScheduled = true;
        _ = System.Threading.Tasks.Task.Delay(ProgressLogFlushInterval).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                _progressLogFlushScheduled = false;
                // Materialise once per flush, not once per line.
                // The verbose filter is applied here so the bound
                // string hides DEBUG/TRACE while the underlying
                // buffer keeps every line for persistence.
                ProgressLog = MaterializeProgressLog();
            }));
    }

    /// <summary>Test-friendly facade preserved for back-compat with
    /// existing tests: returns true iff <paramref name="line"/> would
    /// be visible in the bound <see cref="ProgressLog"/> at the
    /// current verbose-logging setting. Delegates to
    /// <see cref="VerboseLogFilter"/> which is now the single source
    /// of truth (used by both this view and LogsViewModel).</summary>
    internal static bool ShouldShowRestoreLogLine(string line) =>
        VerboseLogFilter.ShouldShow(line);

    /// <summary>Multi-source baselines: counts contributed by sources
    /// already finished in the current run loop. The engine emits
    /// per-iteration snapshots (its RestoreOutcome is rebuilt fresh
    /// each call), so without these baselines a multi-source run
    /// would visibly RESET the counters every time the loop moved to
    /// the next source. <see cref="RunMultiSourceRestoreAsync"/>
    /// increments these between iterations; single-source flows leave
    /// them at zero and the assignment below is a no-op.</summary>
    private int _msBaselineTotal;
    private int _msBaselineRestored;
    private int _msBaselineFailed;

    private void OnEngineProgress(RestoreProgressSnapshot s) =>
        Dispatcher.UIThread.Post(() =>
        {
            TotalFiles    = _msBaselineTotal    + s.Total;
            RestoredCount = _msBaselineRestored + s.Restored;
            FailedCount   = _msBaselineFailed   + s.Failed;
            InFlightCount = s.InFlight;
        });

    private void OnFileRestored(RestoredFile f) =>
        Dispatcher.UIThread.Post(() =>
        {
            var existing = FailuresList.FirstOrDefault(r => r.Path == f.Path);
            if (existing is not null) FailuresList.Remove(existing);
        });

    private void OnFileFailed(RestoredFile f) =>
        Dispatcher.UIThread.Post(() =>
        {
            var existing = FailuresList.FirstOrDefault(r => r.Path == f.Path);
            if (existing is null)
                FailuresList.Add(new FailedFileRow(f.Path, f.LastError ?? ""));
            else
                existing.LastError = f.LastError ?? "";
        });

    // ---- misc ----

    /// <summary>
    /// Stamp a <see cref="RunRecord"/> for the just-finished restore
    /// (or test-restore) and append it to the backup's history. Lands
    /// the record in the same per-backup history.json the LogsView
    /// reads for backup runs, so the RUN dropdown interleaves
    /// activity by start time. Writes the buffered ProgressLog to a
    /// real file under the per-backup logs dir so the
    /// "Selected run" pane has something to load.
    /// </summary>
    private void PersistRestoreRunRecord(RunKind kind)
    {
        if (SelectedBackup is null) return;
        try
        {
            var endedUtc = DateTime.UtcNow;
            var status = ClassifyRestoreStatus();
            var summary = BuildPersistedSummaryLine();

            // Per-run log file. Mirrors the per-backup-cell layout:
            // <DuplicacyLogsRoot>/<sanitized-backup-name>/<timestamp>-<kind>.log
            // The LogsView's "Selected run" pane reads this path.
            string? logPath = null;
            try
            {
                var dir = AppPaths.LogDirForBackup(SelectedBackup.Name);
                System.IO.Directory.CreateDirectory(dir);
                var stamp = _restoreRunStartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
                var kindTag = kind == RunKind.TestRestore ? "test-restore" : "restore";
                logPath = System.IO.Path.Combine(dir, $"{stamp}-{kindTag}.log");
                // Write from the live StringBuilder, not the bound
                // ProgressLog property — the latter is updated via a
                // 150 ms debounced flush, so a fast restore could
                // finish (and we'd persist) before the final flush
                // had committed the last lines into the bound
                // string. The user reported one persisted log was
                // blank for exactly this reason.
                System.IO.File.WriteAllText(logPath, _progressLogBuffer.ToString());
            }
            catch (Exception ex)
            {
                AppLogger.For<RestoreViewModel>().Warning(ex,
                    "Failed to persist {Kind} log file for {Backup}", kind, SelectedBackup.Name);
            }

            var rec = new RunRecord
            {
                // Re-use the in-flight record's Id when one exists so
                // the persisted entry IS the same row in the LogsView
                // RUN dropdown the user clicked while the run was in
                // flight — clicking it during the run and after the
                // run lands on the same item. See BackupVerifier for
                // the symmetrical pattern.
                Id         = _inFlightRunRecord?.Id ?? Guid.NewGuid().ToString("N"),
                BackupId   = SelectedBackup.Id,
                BackupName = SelectedBackup.Name,
                Kind       = kind,
                StartedUtc = _restoreRunStartedUtc,
                EndedUtc   = endedUtc,
                Status     = status,
                Summary    = summary,
                LogPath    = logPath ?? "",
            };
            ServiceLocator.Logs.AppendRun(rec);
        }
        catch (Exception ex)
        {
            AppLogger.For<RestoreViewModel>().Warning(ex,
                "Failed to persist {Kind} RunRecord for {Backup}", kind, SelectedBackup?.Name);
        }
    }

    /// <summary>
    /// Register a synthetic "Running" RunRecord with
    /// <see cref="RunActivityTracker"/> so the LogsView's RUN dropdown
    /// surfaces this restore as a Running entry while it's in flight.
    /// Mirrors what the orchestrator does for backup runs and what
    /// <see cref="BackupVerifier.VerifyAsync"/> does for test-restore
    /// drills. Caller MUST pair this with <see cref="EndInFlightRestoreRun"/>
    /// in a finally block; otherwise a phantom "Running" entry persists
    /// in the dropdown forever.
    /// </summary>
    private void BeginInFlightRestoreRun()
    {
        if (SelectedBackup is null) { _inFlightRunRecord = null; return; }
        // Both steps wrapped in try/catch so a failure here can NEVER
        // bubble up into the restore run start path. The three call
        // sites all sit BEFORE `IsRunning = true` and the engine
        // event subscriptions; a throw out of Begin would leak the
        // wizard into a half-armed state (engine.Log etc. never
        // wired, finally never reached). Registration with the
        // tracker is purely cosmetic UI plumbing — if it fails,
        // the run can still proceed without a "Running" row in the
        // LogsView dropdown. Errors are logged so they're visible
        // in support reports.
        try
        {
            _inFlightRunRecord = new RunRecord
            {
                BackupId   = SelectedBackup.Id,
                BackupName = SelectedBackup.Name,
                Kind       = RunKind.Restore,
                StartedUtc = _restoreRunStartedUtc,
                Status     = BackupRunStatus.Running,
                Summary    = "Running…",
            };
            ServiceLocator.Activity.Register(_inFlightRunRecord);
        }
        catch (Exception ex)
        {
            AppLogger.For<RestoreViewModel>().Warning(ex,
                "BeginInFlightRestoreRun threw — restore proceeds without a Running entry in the dropdown");
            _inFlightRunRecord = null;
        }
    }

    /// <summary>Unregister the in-flight record from the tracker. The
    /// field stays non-null so <see cref="PersistRestoreRunRecord"/>
    /// can re-use its Id for ID continuity in the dropdown.</summary>
    private void EndInFlightRestoreRun()
    {
        if (_inFlightRunRecord is null) return;
        try { ServiceLocator.Activity.Unregister(_inFlightRunRecord.Id); }
        catch { /* tracker mutations should never break the wizard */ }
    }

    private BackupRunStatus ClassifyRestoreStatus()
    {
        if (_currentOutcome is null) return BackupRunStatus.Skipped;
        var failed = _currentOutcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Failed);
        var ok     = _currentOutcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Restored);
        if (_cts is { IsCancellationRequested: true }) return BackupRunStatus.Skipped;
        if (failed == 0 && ok > 0) return BackupRunStatus.Success;
        if (failed > 0 && ok > 0)  return BackupRunStatus.Warning;
        if (failed > 0)            return BackupRunStatus.Failed;
        return BackupRunStatus.Skipped;
    }

    private string BuildPersistedSummaryLine()
    {
        if (_currentOutcome is null) return "no files restored";
        var ok = _currentOutcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Restored);
        var fail = _currentOutcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Failed);
        var dur  = (DateTime.UtcNow - _restoreRunStartedUtc).TotalSeconds;
        var durText = dur < 60 ? $"in {dur:F0}s" : $"in {dur / 60:F1}m";
        return fail == 0
            ? $"{ok:N0} restored · {durText}"
            : $"{ok:N0} restored · {fail:N0} failed · {durText}";
    }

    private void BuildSummary()
    {
        if (_currentOutcome is null) return;
        AnyFailures = _currentOutcome.FailedCount > 0;
        SummaryLine = $"{_currentOutcome.RestoredCount} restored · {_currentOutcome.FailedCount} failed · into {_currentOutcome.TargetPath}";
    }

    private static Avalonia.Controls.Window? OwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.MainWindow;
        return null;
    }

    public bool IsStepPickBackup       => CurrentStep == Step.PickBackup;
    public bool IsStepPickSource       => CurrentStep == Step.PickSource;
    public bool IsStepPickDestination  => CurrentStep == Step.PickDestination;
    public bool IsStepPickRevision     => CurrentStep == Step.PickRevision;
    public bool IsStepPickFiles        => CurrentStep == Step.PickFiles;
    public bool IsStepPickTarget       => CurrentStep == Step.PickTarget;
    public bool IsStepProgress         => CurrentStep == Step.Progress;
    public bool IsStepSummary          => CurrentStep == Step.Summary;

    /// <summary>True iff we're on the Progress OR Summary step. Both
    /// share the same dark-terminal layout; Summary just swaps the
    /// title + bottom action buttons. Bound by the outer Restore-
    /// progress Grid's IsVisible so the terminal stays on screen
    /// after the run finishes — the user reported "when the restore
    /// completes, don't hide the console". </summary>
    public bool IsStepProgressOrSummary => CurrentStep is Step.Progress or Step.Summary;

    /// <summary>True iff the wizard is on a step that should live
    /// INSIDE the outer ScrollViewer (the calm input steps that
    /// scale with content). Progress / Summary / PickFiles each
    /// host their own internal scroll surface — the files list and
    /// the terminal would otherwise drag the whole page along when
    /// they grow. The user reported this for both: bottom buttons
    /// scrolling off-screen and "the file selector list … should
    /// resize dynamically to accommodate". Bound by the input-
    /// steps ScrollViewer's IsVisible.</summary>
    public bool IsScrollableInputStep =>
        CurrentStep is Step.PickBackup or Step.PickSource or Step.PickDestination
                    or Step.PickRevision or Step.PickTarget or Step.Summary;

    /// <summary>
    /// Wizard step counter. Computes the effective ordered list of
    /// USER-VISIBLE steps and reports "STEP 3 OF 5" so users know how
    /// far in they are. Hidden during Progress/Summary which aren't
    /// input steps.
    ///
    /// PickBackup is deliberately excluded from the count: it's a
    /// defensive empty-state placeholder — the only entry point into
    /// the wizard is the per-backup "Restore files" button on a
    /// BackupCard, which auto-advances past PickBackup. Counting it
    /// produced the off-by-one bug the user just hit ("starts with
    /// Step 2 even though we got rid of Step 1").
    ///
    /// PickRevision and PickFiles are also skipped in multi-source
    /// mode — the wizard restores latest revision + every file per
    /// picked source — so the count contracts to "Step N of 3" or
    /// similar, mirroring exactly what the user actually sees.
    /// </summary>
    public string WizardStepLabel
    {
        get
        {
            var ordered = new List<Step>();
            if (SourcesForBackup.Count > 1) ordered.Add(Step.PickSource);
            if (DestinationsForBackup.Count > 1) ordered.Add(Step.PickDestination);
            ordered.Add(Step.PickRevision);
            // In multi-source mode the wizard restores all files of
            // every picked source — file picking is bypassed.
            if (!MultiSourceMode) ordered.Add(Step.PickFiles);
            ordered.Add(Step.PickTarget);
            var idx = ordered.IndexOf(CurrentStep);
            if (idx < 0) return "";
            return $"STEP {idx + 1} OF {ordered.Count}";
        }
    }

    /// <summary>True on input steps (PickSource → PickTarget); false on
    /// PickBackup (empty-state placeholder) and Progress/Summary (where
    /// a step counter would be misleading).</summary>
    public bool ShowWizardStepIndicator =>
        CurrentStep is Step.PickSource or Step.PickDestination
                    or Step.PickRevision or Step.PickFiles or Step.PickTarget;

    /// <summary>
    /// True iff the current step has a previous USER-VISIBLE step to
    /// rewind to. Drives the visibility of the per-step Back buttons —
    /// without this the wizard rendered a Back button on its first
    /// visible step that, when clicked, would dead-end on PickBackup
    /// (the empty-state placeholder reachable only through the
    /// Backups page). Mirrors the rewind switch in <see cref="Back"/>:
    /// Back is hidden iff that switch would land on PickBackup.
    /// </summary>
    public bool CanGoBack => CurrentStep switch
    {
        // PickSource is always the first visible step when shown — no
        // earlier user-visible step exists.
        Step.PickSource      => false,
        // PickDestination has a Back target only if PickSource is also
        // visible (multi-source backup).
        Step.PickDestination => SourcesForBackup.Count > 1,
        // PickRevision rewinds to whichever picker came last.
        Step.PickRevision    => DestinationsForBackup.Count > 1
                                    || SourcesForBackup.Count > 1,
        Step.PickFiles       => true,
        // PickTarget always has a previous visible step: in single-
        // source mode it rewinds to PickFiles, in multi-source mode
        // to PickRevision (the new summary view).
        Step.PickTarget      => true,
        _ => false,
    };

    partial void OnCurrentStepChanged(Step value)
    {
        OnPropertyChanged(nameof(IsStepPickBackup));
        OnPropertyChanged(nameof(IsStepPickSource));
        OnPropertyChanged(nameof(IsStepPickDestination));
        OnPropertyChanged(nameof(IsStepPickRevision));
        OnPropertyChanged(nameof(IsStepPickFiles));
        OnPropertyChanged(nameof(IsStepPickTarget));
        OnPropertyChanged(nameof(IsStepProgress));
        OnPropertyChanged(nameof(IsStepSummary));
        OnPropertyChanged(nameof(IsStepProgressOrSummary));
        OnPropertyChanged(nameof(IsScrollableInputStep));
        OnPropertyChanged(nameof(WizardStepLabel));
        OnPropertyChanged(nameof(ShowWizardStepIndicator));
        OnPropertyChanged(nameof(CanGoBack));

        // When the wizard enters Progress, signal global live activity
        // so a LogsViewModel listening on Activity & Logs can flip its
        // unified pane into Live mode (otherwise the restore output
        // would be invisible there because the dropdowns are pinned
        // to past backup runs). The user reported: "Restore logs
        // should show up in Activity & Logs."
        // Pass the backup name so the Logs view can switch its
        // dropdown to that backup before flipping into Live mode —
        // see BackupVerifier.VerifyAsync for the symmetrical signal.
        if (value == Step.Progress)
            AppNavigator.SignalLiveActivity(SelectedBackup?.Name);
    }

}

/// <summary>
/// Read-only summary row for the multi-source revision step: each
/// picked source's latest revision number, age, and size. Lets the
/// user confirm what they're about to restore (and how old the data
/// is) before committing in multi-source mode.
/// </summary>
public sealed class MultiSourceRevisionRow
{
    public string SourcePath { get; }
    /// <summary>Latest revision number for this source, or null when
    /// the source has no revisions yet (e.g. a recently-added source
    /// the backup hasn't reached on a successful run). Rendered as
    /// "—" in that case.</summary>
    public int? RevisionNumber { get; }
    public DateTime? CreatedUtc { get; }
    public long TotalBytes { get; }
    public int FileCount { get; }
    /// <summary>"#7 · 2026-04-30 14:21" / "no revisions yet" — the
    /// short verdict shown next to the source path. Computed once
    /// at construction; the row is read-only.</summary>
    public string VerdictLabel =>
        RevisionNumber is int n && CreatedUtc is DateTime t
            ? $"#{n} · {t.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "no revisions yet";

    public MultiSourceRevisionRow(string sourcePath, int? revisionNumber,
        DateTime? createdUtc, int fileCount, long totalBytes)
    {
        SourcePath = sourcePath;
        RevisionNumber = revisionNumber;
        CreatedUtc = createdUtc;
        FileCount = fileCount;
        TotalBytes = totalBytes;
    }
}

public sealed partial class FailedFileRow : ObservableObject
{
    public string Path { get; }
    [ObservableProperty] private string _lastError = "";
    public FailedFileRow(string path, string lastError) { Path = path; _lastError = lastError; }
}

/// <summary>
/// Multi-select row for the PickSource step. Each backup source
/// becomes one row with its own IsSelected flag; the wizard uses
/// the set of checked rows to drive single- or multi-source restore
/// flows. Notifies the parent VM on toggle so the Next button's
/// enable state and the multi-source banner can recompute.
/// </summary>
public sealed partial class SourcePickRow : ObservableObject
{
    private readonly RestoreViewModel _parent;
    public string Path { get; }

    [ObservableProperty] private bool _isSelected;

    public SourcePickRow(string path, RestoreViewModel parent, bool isSelected = true)
    {
        Path = path;
        _parent = parent;
        _isSelected = isSelected;
    }

    partial void OnIsSelectedChanged(bool value) => _parent.OnSourcePickRowToggled();
}

/// <summary>
/// Wraps a <see cref="Destination"/> for the Restore wizard's
/// PickDestination radio-group picker. Mirrors <see cref="SourcePickRow"/>:
/// a row VM lets each RadioButton bind its IsChecked directly to
/// the row's own <see cref="IsSelected"/> ObservableProperty, which
/// reliably notifies on change. The earlier
/// <c>$parent[ItemsControl].DataContext.SelectedDestination</c> +
/// RefEq pattern silently failed to update the radio dot when
/// SelectedDestination changed — see
/// <c>InteractionDestinationClickTests</c> for the regression
/// guard.
/// </summary>
public sealed partial class DestinationPickRow : ObservableObject
{
    public Destination Destination { get; }
    [ObservableProperty] private bool _isSelected;

    public DestinationPickRow(Destination destination, bool isSelected = false)
    {
        Destination = destination;
        _isSelected = isSelected;
    }
}

/// <summary>
/// Wraps a <see cref="RevisionFile"/> with a mutable IsSelected flag so the UI
/// checkbox can bind two-way and selection totals update via the parent VM.
/// </summary>
public sealed partial class RevisionFileRow : ObservableObject
{
    private readonly RestoreViewModel _parent;
    public RevisionFile File { get; }

    [ObservableProperty] private bool _isSelected;

    // Convenience getters for XAML binding (DataType=svc:RevisionFile previously).
    public string Path => File.Path;
    public long SizeBytes => File.SizeBytes;
    public DateTime ModifiedUtc => File.ModifiedUtc;

    public RevisionFileRow(RevisionFile f, RestoreViewModel parent)
    {
        File = f;
        _parent = parent;
    }

    partial void OnIsSelectedChanged(bool value) => _parent.RecomputeSelectionTotals();
}
