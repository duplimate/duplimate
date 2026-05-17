using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Views;

namespace Duplimate.ViewModels;

public sealed partial class BackupEditorViewModel : ViewModelBase
{
    private static Serilog.ILogger _log => AppLogger.For<BackupEditorViewModel>();

    public Backup Working { get; }
    public bool IsNew { get; }

    /// <summary>Header title — "Backup", "Edit «name»", or one of the
    /// unified creation copy variants. Settable by the calling window.</summary>
    [ObservableProperty] private string _headerTitle = "Backup";

    // ---- common ----
    [ObservableProperty] private string _name;
    /// <summary>
    /// Absolute source paths. Bound to the Sources chip list in the
    /// editor — "Add source…" opens the tree picker, "Add by path…"
    /// the native folder picker, and each chip has its own remove
    /// command. Must contain at least one entry at save time.
    /// </summary>
    public ObservableCollection<string> SourcePaths { get; } = new();

    /// <summary>
    /// Parallel UI-friendly collection of source-path chips with live
    /// folder-size estimates. Mirrors <see cref="SourcePaths"/> 1:1 — the
    /// constructor wires up a CollectionChanged subscriber that keeps
    /// the two in sync. Bound by the editor's Sources card.
    /// </summary>
    public ObservableCollection<SourceChipVm> SourceChips { get; } = new();
    [ObservableProperty] private int _threads;
    [ObservableProperty] private bool _useVss;
    [ObservableProperty] private string _keepPolicy;

    /// <summary>
    /// Plain-English translation of <see cref="KeepPolicy"/>, updated
    /// whenever the user edits the string. Renders below the input so
    /// the user can see what they typed actually means without decoding
    /// the <c>n:m</c> grammar in their head.
    /// </summary>
    [ObservableProperty] private KeepPolicyDescription _keepPolicyExplanation;

    partial void OnKeepPolicyChanged(string value)
    {
        KeepPolicyExplanation = KeepPolicyHumanizer.Describe(value);
        // Clear the error as soon as the field becomes valid, regardless
        // of AttemptedSave. Earlier we only cleared post-save, which left
        // a red error visible on a freshly-corrected field until the next
        // Save click.
        if (KeepPolicyExplanation.Valid) KeepPolicyError = null;
    }

    partial void OnNameChanged(string value)
    {
        // Same UX rule as KeepPolicy: clear the error the moment the
        // field changes so the user gets immediate feedback that their
        // edit was registered. Re-validation happens on Save.
        NameError = null;
        // A name change invalidates any prior probe-warning ack — the
        // collision/uniqueness picture might be different now.
        _probeWarningAcknowledged = false;
        ProbeWarning = null;
    }

    /// <summary>
    /// Human-friendly default name derived from the currently-picked
    /// source paths. Shown as the Name input's Watermark; if the user
    /// leaves Name blank, this is what gets persisted (with a collision
    /// check so we never overwrite an existing backup).
    /// </summary>
    public string SuggestedName =>
        NameGenerator.ForBackup(
            SourcePaths.ToList(),
            candidate => ServiceLocator.Config.Current.Backups
                .Any(b => b.Id != Working.Id
                          && string.Equals(b.Name?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)));

    [ObservableProperty] private bool _pruneAfterBackup;
    [ObservableProperty] private bool _checkAfterBackup;
    [ObservableProperty] private bool _abortOnMeteredNetwork;
    [ObservableProperty] private bool _enabled;

    // ---- schedule ----
    [ObservableProperty] private ScheduleFrequency _frequency;
    [ObservableProperty] private TimeSpan _timeOfDay;
    [ObservableProperty] private int _hourlyInterval;
    [ObservableProperty] private bool _skipOnBattery;
    [ObservableProperty] private bool _stopOnBattery;
    [ObservableProperty] private bool _requireNetwork;
    [ObservableProperty] private bool _catchUpMissed;

    // ---- advanced ----
    [ObservableProperty] private int _bandwidthLimitKBps;
    [ObservableProperty] private string _extraBackupArgs;

    /// <summary>
    /// Toggled by the "Show advanced options" button. VM-backed (not an
    /// x:Name-bound ToggleButton) so the reveal is immune to XAML element
    /// scoping / template measurement bugs that previously left the
    /// toggle itself unrenderable.
    /// </summary>
    [ObservableProperty] private bool _isAdvancedOpen;
    public string AdvancedToggleLabel => IsAdvancedOpen ? "Hide advanced options" : "Show advanced options";
    partial void OnIsAdvancedOpenChanged(bool value) => OnPropertyChanged(nameof(AdvancedToggleLabel));

    [RelayCommand] private void ToggleAdvanced() => IsAdvancedOpen = !IsAdvancedOpen;

    // ---- validation (Save-attempt gated) ----
    [ObservableProperty] private bool _attemptedSave;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _sourcesError;
    [ObservableProperty] private string? _targetsError;
    [ObservableProperty] private string? _keepPolicyError;
    [ObservableProperty] private string? _summaryError;
    /// <summary>Non-blocking warning surfaced after a save when the
    /// destination probe couldn't fully verify uniqueness, or when a
    /// typed Name appears to collide with an existing remote snapshot
    /// chain. Distinct from SummaryError so the editor renders it in a
    /// calmer (amber) tone. The first save attempt that triggers the
    /// warning is refused (returns null); a second attempt with no
    /// intervening Name/Targets change passes through, so the user has
    /// to consciously confirm.</summary>
    [ObservableProperty] private string? _probeWarning;
    /// <summary>Set the first time the user sees a ProbeWarning; cleared
    /// when they change Name or Targets. Lets the second Save click pass
    /// through as "I've read it, proceed".</summary>
    private bool _probeWarningAcknowledged;
    public bool HasAnyError => !string.IsNullOrEmpty(SummaryError);
    public bool HasProbeWarning => !string.IsNullOrEmpty(ProbeWarning);
    partial void OnProbeWarningChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProbeWarning));
    }

    /// <summary>True while the async save is probing destinations / committing.
    /// Bound to the Save button's IsEnabled to prevent double-clicks.</summary>
    [ObservableProperty] private bool _saveBusy;
    /// <summary>Short "Checking destination for collisions…" style status shown
    /// near the footer so the user knows why Save is momentarily unresponsive.</summary>
    [ObservableProperty] private string _saveStatus = "";

    // ---- targets ----
    public ObservableCollection<TargetRow> Targets { get; } = new();
    public ObservableCollection<Destination> AvailableDestinations { get; } = new();
    /// <summary>Destinations the user hasn't already added as a target.
    /// Drives the "pick a destination" ComboBox so the same destination
    /// can't be picked twice — earlier the picker offered every
    /// destination unconditionally and accepting the duplicate created
    /// a doomed dup BackupTarget.</summary>
    public System.Collections.Generic.IEnumerable<Destination> AvailableDestinationsForAdd =>
        AvailableDestinations.Where(d => !Targets.Any(t => t.DestinationId == d.Id));
    /// <summary>True iff there's at least one destination the user
    /// could still add as a target. Drives visibility of the
    /// "Pick a destination" dropdown row in the editor — when nothing's
    /// left to pick we hide the row entirely instead of greying out a
    /// disabled Add button.</summary>
    public bool HasMoreDestinationsToAdd => AvailableDestinationsForAdd.Any();
    [ObservableProperty] private Destination? _pendingNewDestination;

    /// <summary>
    /// Label for the "+ create new destination" link below the picker.
    /// Mirrors the same flexing copy the Easy-mode wizard uses so both
    /// modes read identically. The shared
    /// <see cref="Views.DestinationAddPicker"/> binds against this
    /// property — keeping the label authoring on the VM lets the
    /// wording stay tied to the user's mental state ("add another"
    /// once they've added one).
    /// </summary>
    public string CreateDestinationButtonLabel =>
        Targets.Count > 0
            ? "Add another destination"
            : "Or set up a new destination…";

    // ---- filters ----
    [ObservableProperty] private string _filtersText;
    [ObservableProperty] private string _filterSimulationLog = "";
    [ObservableProperty] private bool _simulationRunning;

    /// <summary>
    /// Sticky flag: true once the user manually edits FiltersText. We
    /// only auto-apply recommended exclusions on new backups while this
    /// stays false, so the auto-update never clobbers something the
    /// user typed by hand. Reset only by closing/reopening the editor.
    /// Bypassed when <see cref="ApplyRecommendedFilters"/> writes (it
    /// programmatically updates FiltersText, which would otherwise flip
    /// this flag and pin auto-application off).
    /// </summary>
    private bool _userTouchedFilters;
    private bool _suppressFilterTouchTracking;

    partial void OnFiltersTextChanged(string value)
    {
        if (_suppressFilterTouchTracking) return;
        _userTouchedFilters = true;
    }

    // Recommendation toggles. The UI-bound checkboxes drive these; the
    // ApplyRecommendedFilters command renders the corresponding block
    // and merges it into FiltersText (replacing any prior auto-block).
    // Defaults are recomputed from the user's source paths on each
    // CollectionChanged tick so a user adding only a Documents folder
    // doesn't see the Windows-system box checked irrelevantly.
    // Default true on both. Earlier _recExcludeWindowsSystem defaulted
    // to false and was only flipped on by SuggestForSources when a
    // source was a drive root ("C:" / "C:\"). The user repeatedly
    // reported the toggle unticked even on backups they expected
    // it on. Defaulting both to true matches the user's expectation
    // ("recommended exclusions should be... recommended"). The
    // Windows-system patterns are root-relative ('e:(?i)^Windows$')
    // so they're no-ops on non-drive-root sources — leaving the
    // toggle on costs nothing for users who don't back up a drive
    // root, and prevents the silent miss for users who do.
    [ObservableProperty] private bool _recExcludeWindowsSystem = true;
    [ObservableProperty] private bool _recExcludeCachesAndJunk = true;
    /// <summary>Estimated bytes the rec-block would skip on this PC.
    /// Computed lazily after Apply; -1 means "not estimated yet",
    /// -2 means "computation in progress".</summary>
    [ObservableProperty] private long _recEstimatedSavingsBytes = -1;
    [ObservableProperty] private bool _recEstimatedTruncated;
    public string RecEstimatedSavingsLabel => RecEstimatedSavingsBytes switch
    {
        -1 => "",
        -2 => "Estimating savings… (walking your source folders)",
        _  => RecEstimatedTruncated
              ? $"Estimated savings: at least {HumanBytes(RecEstimatedSavingsBytes)} (estimate truncated — sources too large to fully scan)."
              : $"Estimated savings: {HumanBytes(RecEstimatedSavingsBytes)} on this PC.",
    };
    private CancellationTokenSource? _savingsCts;

    private static string HumanBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024L * 1024 => $"{b / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):F1} MB",
        < 1024L * 1024 * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):F2} GB",
        _ => $"{b / (1024.0 * 1024 * 1024 * 1024):F2} TB",
    };
    /// <summary>Cloud sync folders detected on this machine, each with
    /// an <c>IsSelected</c> toggle. Empty collection = no providers
    /// detected; the UI hides the section in that case.</summary>
    public ObservableCollection<CloudExcludeChoice> DetectedCloudFolders { get; } = new();
    public bool HasDetectedCloudFolders => DetectedCloudFolders.Count > 0;

    // ---- healthchecks ----
    [ObservableProperty] private string? _healthcheckId;
    [ObservableProperty] private string _healthcheckStatus = "";
    /// <summary>Per-edit copy of the global Healthchecks.io API key —
    /// shown inline when the user hasn't set one yet in Settings, so they
    /// don't have to leave the editor to paste one. Saved into the global
    /// config on commit if non-empty.</summary>
    [ObservableProperty] private string _healthcheckApiKeyInput = "";
    /// <summary>Per-edit copy of the global Healthchecks.io base URL.
    /// Defaults to whatever Settings has (usually healthchecks.io).</summary>
    [ObservableProperty] private string _healthcheckBaseUrlInput = "";
    /// <summary>True when the user has no API key in Settings — drives
    /// inline visibility of the API-key + base-URL inputs in the
    /// BackupEditor's Healthchecks card.</summary>
    public bool NeedsHealthcheckSetup =>
        string.IsNullOrEmpty(ServiceLocator.Config.Current.Monitoring.HealthchecksApiKeyRef)
        || !ServiceLocator.Secrets.TryGet(
            ServiceLocator.Config.Current.Monitoring.HealthchecksApiKeyRef!, out var k)
        || string.IsNullOrWhiteSpace(k);
    [ObservableProperty] private string? _healthcheckIdError;

    public IReadOnlyList<ScheduleFrequency> AvailableFrequencies { get; } =
        new[] { ScheduleFrequency.Daily, ScheduleFrequency.Hourly, ScheduleFrequency.Weekly };

    /// <summary>
    /// Stable identity of the Targets collection at editor-open time.
    /// SaveAsync compares the current key against this and skips the
    /// destination probe when nothing about the targets changed —
    /// so a pure rename / schedule edit no longer hits the network.
    /// </summary>
    private string _originalTargetsKey = "";

    /// <summary>
    /// Build a deterministic identity string from the current Targets
    /// collection so ComputeTargetsKey() at save time can be compared
    /// against the value stamped at editor-open. Includes
    /// DestinationId + StorageName per row, sorted, joined — order
    /// independence comes from the Sort. ThreadsOverride is omitted
    /// (a thread-count tweak doesn't change the storage shape, so
    /// re-probing after a thread change would also be wasteful).
    /// </summary>
    private string ComputeTargetsKey()
    {
        return string.Join("|", Targets
            .Select(t => $"{t.DestinationId}/{t.StorageName ?? ""}")
            .OrderBy(s => s, StringComparer.Ordinal));
    }

    public BackupEditorViewModel(Backup working, bool isNew)
    {
        Working = working;
        IsNew = isNew;

        _name = working.Name;
        foreach (var p in working.SourcePaths) SourcePaths.Add(p);

        // Build matching chips up-front so the initial render isn't
        // empty; the size probes kick off lazily.
        foreach (var p in SourcePaths) AddChipFor(p);

        SourcePaths.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(SuggestedName));
            // Clear the "no sources" error as soon as sources exist —
            // earlier we only cleared post-AttemptedSave, which left the
            // red banner visible after the user added a source if they'd
            // already triggered validation once. Now any non-empty list
            // clears the error so it doesn't outlive its meaning.
            if (SourcePaths.Count > 0) SourcesError = null;
            SyncChipsFromPaths(e);
            // Re-render the recommended filters block whenever sources
            // change (so newly-added cloud-sync detections etc. get
            // included). DON'T overwrite the toggle states — the
            // user's choice on RecExclude* survives source changes. We
            // only set the toggles from defaults during construction
            // (below).
            if (IsNew && !_userTouchedFilters)
                ApplyRecommendedFilters();
        };
        // For new backups: render the recommended-filters block on
        // construction so opening the editor's Filters tab shows the
        // patterns already in place. The toggles' field-init defaults
        // (both true) drive what's in the block — earlier we'd compute
        // defaults from `SuggestForSources(SourcePaths)` which DEMOTED
        // RecExcludeWindowsSystem to false for non-drive-root sources,
        // overriding the user-friendly always-on default. The patterns
        // are root-relative so a Windows-system block under a non-drive
        // source matches nothing — leaving the toggle on for everyone
        // is harmless and matches the user's expectation that
        // "recommended exclusions" actually means "recommended on by
        // default". User can untick freely.
        if (isNew && SourcePaths.Count > 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_userTouchedFilters) ApplyRecommendedFilters();
            });
        }
        // 0 means "auto-tune per destination kind" — UI shows this as
        // "Auto" via a placeholder rather than coercing to 4.
        _threads = working.Threads;
        _useVss = working.UseVss;
        _keepPolicy = string.IsNullOrEmpty(working.KeepPolicy) ? "0:365 30:90 7:30 1:7" : working.KeepPolicy;
        _keepPolicyExplanation = KeepPolicyHumanizer.Describe(_keepPolicy);
        _pruneAfterBackup = working.PruneAfterBackup;
        _checkAfterBackup = working.CheckAfterBackup;
        _abortOnMeteredNetwork = working.AbortOnMeteredNetwork;
        _enabled = working.Enabled;

        _frequency = working.Schedule.Frequency;
        _timeOfDay = working.Schedule.TimeOfDay == TimeSpan.Zero ? TimeSpan.FromHours(20) : working.Schedule.TimeOfDay;
        _hourlyInterval = working.Schedule.HourlyInterval == 0 ? 4 : working.Schedule.HourlyInterval;
        _skipOnBattery = working.Schedule.SkipOnBattery;
        _stopOnBattery = working.Schedule.StopOnBattery;
        _requireNetwork = working.Schedule.RequireNetwork;
        _catchUpMissed = working.Schedule.CatchUpMissedRuns;

        _bandwidthLimitKBps = working.BandwidthLimitKBps;
        _extraBackupArgs = working.ExtraBackupArgs;

        _filtersText = working.FiltersText;
        _healthcheckId = working.HealthcheckId;
        _healthcheckBaseUrlInput = ServiceLocator.Config.Current.Monitoring.HealthchecksBaseUrl;

        // Cloud-sync folder detection runs once at editor open. Cheap
        // (env-var + file-existence checks); no need to re-run.
        foreach (var folder in RecommendedFilters.DetectCloudSyncFolders())
            DetectedCloudFolders.Add(new CloudExcludeChoice(folder));

        foreach (var d in ServiceLocator.Config.Current.Destinations) AvailableDestinations.Add(d);
        foreach (var t in working.Targets)
        {
            var d = ServiceLocator.Config.Current.Destinations.FirstOrDefault(x => x.Id == t.DestinationId);
            if (d is not null) Targets.Add(new TargetRow(d, t.StorageName, t.ThreadsOverride));
        }
        // Snapshot the targets identity at editor-open so SaveAsync can
        // skip the destination probe when the user changed only the
        // friendly name, the schedule, or filters. Without this, every
        // save (including a pure rename) hit the network to re-list
        // every destination's snapshot ids — slow, and the user
        // explicitly flagged it: "Changing the friendly name has no
        // reason to trigger the checking destinations for existing
        // backups." Pure rename = same _originalTargetsKey at save
        // time = probe skipped.
        _originalTargetsKey = ComputeTargetsKey();
        Targets.CollectionChanged += (_, __) =>
        {
            if (Targets.Count > 0) TargetsError = null;
            // Refresh the "add" ComboBox so already-added destinations
            // disappear from the dropdown.
            OnPropertyChanged(nameof(AvailableDestinationsForAdd));
            OnPropertyChanged(nameof(HasMoreDestinationsToAdd));
            // The create-new link label flexes between "Or set up a
            // new destination…" (none added yet) and "Add another
            // destination" (one or more added) — refresh on every
            // target collection change so the copy stays in sync.
            OnPropertyChanged(nameof(CreateDestinationButtonLabel));
            // A target change invalidates any prior probe-warning ack —
            // probe runs against the new target set on the next save.
            _probeWarningAcknowledged = false;
            ProbeWarning = null;
        };
    }

    /// <summary>
    /// Opens the <see cref="Views.SourceTreePickerWindow"/> — drives + folders
    /// with tri-state checkboxes. Every already-selected source comes back
    /// in with its checkbox pre-ticked, so opening the picker on a backup
    /// that has sources shows the user what's already there rather than
    /// starting blank.
    /// </summary>
    [RelayCommand]
    private async Task AddSourceFromTreeAsync()
    {
        var owner = Owner();
        if (owner is null) return;
        var before = SourcePaths.Count;
        var picker = new Views.SourceTreePickerWindow(SourcePaths);
        var result = await picker.ShowDialog<System.Collections.Generic.List<string>?>(owner);
        if (result is null)
        {
            _log.Debug("Source tree picker cancelled (had {Before} sources)", before);
            return;
        }

        // Replace the whole set — the picker returned the minimal cover
        // of ticked paths. Preserve insertion order (the picker emits in
        // tree-walk order, which reads naturally).
        SourcePaths.Clear();
        foreach (var p in result) SourcePaths.Add(p);
        _log.Information("Source tree picker: {Before} → {After} source(s)", before, result.Count);
    }

    /// <summary>
    /// Fallback path-entry: native folder picker plus a manual
    /// paste-in-UNC affordance via the dialog's address-bar. Handy for
    /// shares that don't appear in the drive tree.
    /// </summary>
    [RelayCommand]
    private async Task AddSourceByPathAsync()
    {
        var owner = Owner();
        if (owner is null) return;
        var picks = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder to back up",
            AllowMultiple = true,
        });
        if (picks is null) return;
        var added = 0;
        foreach (var f in picks)
        {
            var p = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            if (!SourcePaths.Contains(p, System.StringComparer.OrdinalIgnoreCase))
            {
                SourcePaths.Add(p);
                added++;
            }
        }
        if (added > 0) _log.Information("Source paths added via path picker: {Count}", added);
    }

    [RelayCommand]
    private void RemoveSource(string path)
    {
        if (!string.IsNullOrEmpty(path)) SourcePaths.Remove(path);
    }

    private void SyncChipsFromPaths(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Mirror the SourcePaths collection. We do an additive/diff
        // sync rather than wholesale rebuild so existing chips keep
        // their in-flight size-probe results across UI re-renders.
        var current = new HashSet<string>(SourcePaths, StringComparer.OrdinalIgnoreCase);

        // Remove chips whose path is no longer in SourcePaths.
        for (int i = SourceChips.Count - 1; i >= 0; i--)
        {
            if (!current.Contains(SourceChips[i].Path)) SourceChips.RemoveAt(i);
        }

        // Add chips for paths that don't yet have one.
        var existing = new HashSet<string>(SourceChips.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var p in SourcePaths)
        {
            if (!existing.Contains(p)) AddChipFor(p);
        }
    }

    private void AddChipFor(string path)
    {
        var chip = new SourceChipVm(path);
        SourceChips.Add(chip);
        // Kick off the async size probe — UI shows "estimating…" until
        // the probe completes (or times out, in which case we display
        // the partial number with a "~" prefix).
        _ = chip.StartSizeProbeAsync();
    }

    [RelayCommand]
    private void AddTarget()
    {
        if (PendingNewDestination is null) return;
        if (Targets.Any(t => t.DestinationId == PendingNewDestination.Id)) return;
        // Storage name derived from the destination's Id so the entry
        // in `.duplicacy/preferences` is traceable to a specific
        // destination — same convention as the Easy-mode wizard.
        var name = OnboardingViewModel.StorageNameForDestination(PendingNewDestination);
        Targets.Add(new TargetRow(PendingNewDestination, name, null));
        PendingNewDestination = null;
    }

    /// <summary>True when the user hasn't configured any destinations yet —
    /// drives the inline "Add destination" callout in the editor so they
    /// don't have to bounce to the Destinations page mid-flow.</summary>
    public bool HasNoAvailableDestinations => AvailableDestinations.Count == 0;

    /// <summary>
    /// Opens the DestinationEditor modally so the user can create a
    /// destination without leaving the BackupEditor. On save, the new
    /// destination is appended to AvailableDestinations and pre-selected
    /// in the "Pick a destination to add" combo.
    /// </summary>
    [RelayCommand]
    private async Task CreateDestinationInlineAsync()
    {
        var owner = Owner();
        if (owner is null) return;
        var dlg = new DestinationEditorWindow(new Destination(), isNew: true);
        var result = await dlg.ShowDialog<Destination?>(owner);
        if (result is null) return;

        // Persist and refresh the picker list. Update runs through the
        // ConfigStore so the Destinations page sees it too.
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(result));
        AvailableDestinations.Add(result);
        PendingNewDestination = result;
        OnPropertyChanged(nameof(HasNoAvailableDestinations));
        AddTargetCommand.Execute(null);
    }

    [RelayCommand]
    private void RemoveTarget(TargetRow row) => Targets.Remove(row);

    /// <summary>Open the destination editor for the destination this
    /// target row points at. Mirrors the BackupCard's pill-click flow.
    /// On save: upsert into config (so other backups using this
    /// destination see the change), refresh the local
    /// AvailableDestinations + replace the matching TargetRow so the
    /// chip reflects the rename / kind change immediately.</summary>
    [RelayCommand]
    private async Task EditTargetDestinationAsync(TargetRow row)
    {
        if (row is null) return;
        var owner = Owner();
        if (owner is null) return;
        var dest = ServiceLocator.Config.Current.Destinations
            .FirstOrDefault(d => d.Id == row.DestinationId);
        if (dest is null) return;
        var dlg = new Views.DestinationEditorWindow(dest, isNew: false);
        var saved = await dlg.ShowDialog<Destination?>(owner);
        if (saved is null) return;
        ServiceLocator.Config.Update(cfg =>
        {
            var idx = cfg.Destinations.FindIndex(x => x.Id == saved.Id);
            if (idx >= 0) cfg.Destinations[idx] = saved;
        });
        // Refresh the local AvailableDestinations + the corresponding
        // TargetRow so the rename / kind change shows up without
        // closing/re-opening the editor.
        var avail = AvailableDestinations.FirstOrDefault(x => x.Id == saved.Id);
        if (avail is not null)
        {
            var i = AvailableDestinations.IndexOf(avail);
            AvailableDestinations[i] = saved;
        }
        var targetIdx = Targets.IndexOf(row);
        if (targetIdx >= 0)
            Targets[targetIdx] = new TargetRow(saved, row.StorageName, row.ThreadsOverride);
        OnPropertyChanged(nameof(AvailableDestinationsForAdd));
    }

    /// <summary>
    /// True after the user clicks "Save &amp; Run" — the Window code-behind
    /// reads this on close so the parent (<see cref="BackupsViewModel"/>)
    /// can fire the Orchestrator AFTER persisting the saved backup. We
    /// don't run from inside the editor itself because the parent owns
    /// config persistence (add-vs-update is decided there) and we'd
    /// otherwise have to duplicate that logic in two places or risk
    /// running against unpersisted state.
    /// </summary>
    public bool RunAfterSaveRequested { get; set; }

    /// <summary>
    /// Renders the curated rule blocks (Windows-system + caches + the
    /// user-ticked cloud folders) and merges them into FiltersText. If
    /// the text already contains a marker block from a previous click,
    /// it gets replaced in place — anything the user typed outside the
    /// markers survives unchanged.
    /// </summary>
    [RelayCommand]
    private void ApplyRecommendedFilters()
    {
        var cloudPicks = DetectedCloudFolders
            .Where(c => c.IsSelected)
            .Select(c => c.Folder)
            .ToList();
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: RecExcludeWindowsSystem,
            includeCachesAndJunk: RecExcludeCachesAndJunk,
            cloudExcludes: cloudPicks);
        // Programmatic write — don't flip _userTouchedFilters or auto-
        // application turns off the moment we run.
        _suppressFilterTouchTracking = true;
        try
        {
            FiltersText = RecommendedFilters.MergeIntoFilters(FiltersText, block);
        }
        finally { _suppressFilterTouchTracking = false; }
        _log.Information(
            "Applied recommended filters: windowsSystem={WinSys} caches={Caches} cloud={CloudCount}",
            RecExcludeWindowsSystem, RecExcludeCachesAndJunk, cloudPicks.Count);

        // Walk the configured source paths and apply the freshly-merged
        // FiltersText to count actual excluded bytes. Runs off the UI
        // thread; the user sees "Estimating…" until it lands.
        _ = ComputeSavingsEstimateAsync();
    }

    private async Task ComputeSavingsEstimateAsync()
    {
        // Cancel any prior in-flight estimate (e.g. user clicked Apply
        // twice quickly with different toggles).
        try { _savingsCts?.Cancel(); } catch { }
        _savingsCts = new CancellationTokenSource();
        var ct = _savingsCts.Token;

        // Show "Estimating…" immediately so the user knows we're working.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RecEstimatedSavingsBytes = -2;
            RecEstimatedTruncated = false;
            OnPropertyChanged(nameof(RecEstimatedSavingsLabel));
        });

        try
        {
            var sources = SourcePaths.ToList();
            var filters = FiltersText ?? "";
            var est = await ServiceLocator.Savings.ComputeAsync(sources, filters, ct);

            if (ct.IsCancellationRequested) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RecEstimatedSavingsBytes = est.ExcludedBytes;
                RecEstimatedTruncated = est.Truncated;
                OnPropertyChanged(nameof(RecEstimatedSavingsLabel));
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer estimate; the newer one will win.
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Savings estimate failed");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RecEstimatedSavingsBytes = -1;
                OnPropertyChanged(nameof(RecEstimatedSavingsLabel));
            });
        }
    }

    [RelayCommand]
    private async Task RunFilterSimulationAsync()
    {
        var saved = CommitToWorking();
        if (saved is null) return;
        if (saved.Targets.Count == 0) return;

        SimulationRunning = true;
        FilterSimulationLog = "";
        var dest = ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == saved.Targets[0].DestinationId);
        if (dest is null) { SimulationRunning = false; return; }
        var storage = saved.Targets[0].StorageName;

        void OnEntry(SimulatedEntry e)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var prefix = e.Included ? "✓  " : "✗  ";
                FilterSimulationLog += prefix + e.Path + "\n";
            });
        }

        try
        {
            await ServiceLocator.Filters.SimulateAsync(saved, dest, storage, OnEntry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            FilterSimulationLog += $"\n[error] {ex.Message}";
        }
        finally { SimulationRunning = false; }
    }

    [RelayCommand]
    private async Task CreateHealthcheckAsync()
    {
        var saved = CommitToWorking();
        if (saved is null) return;
        HealthcheckStatus = "Creating…";
        var rec = await ServiceLocator.Healthcheck.UpsertCheckAsync(saved, CancellationToken.None);
        if (rec is null)
        {
            HealthcheckStatus = "Failed — set your Healthchecks.io API key in Settings first.";
            return;
        }
        HealthcheckId = rec.ExtractPingUuid();
        HealthcheckStatus = $"Created. Ping URL: {rec.PingUrl}";
    }

    [RelayCommand]
    private async Task EraseDestinationAsync(TargetRow row)
    {
        var owner = Owner();
        if (owner is null) return;
        var dlg = new DestructiveConfirmWindow(
            title: "Erase this backup’s storage?",
            subtitle:
                $"Every revision of «{Name}» stored at «{row.DestinationName}» will be removed. " +
                "Copies at other destinations are untouched. There’s no undo — type OK to confirm.",
            confirmString: "OK",
            caseInsensitive: true);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;

        var saved = CommitToWorking();
        if (saved is null) return;
        var dest = ServiceLocator.Config.Current.Destinations.First(d => d.Id == row.DestinationId);

        // Show a modal progress dialog so the user knows the erase is
        // in flight (otherwise the editor sat motionless for the
        // duration — multi-minute on cloud destinations) and so the
        // cleaner's per-step messages have somewhere to land. The
        // user reported: "show a dialog asking the user to wait until
        // erasing process is complete (ideally with progress %)."
        // Snapshot-id-scoped erase via StorageCleaner — leaves any
        // other backups sharing the destination intact.
        //
        // Acquires the global maintenance lock first: any in-flight
        // backup is cancelled, queued runs wait, restores wait. The
        // user explicitly asked: "ensure that NO backup or restore at
        // all can run … while one of these sensitive operations is
        // in progress."
        await ErasingProgressWindow.RunAsync(
            owner,
            $"Erasing «{Name}» from «{row.DestinationName}»",
            async (progress, chunkPct, ct) =>
            {
                using var maintenance = await ServiceLocator.Maintenance.AcquireAsync(
                    $"Erasing «{Name}» from «{row.DestinationName}»", progress, cancelGracePeriod: null, ct);
                await ServiceLocator.Cleaner.EraseBackupFromDestinationAsync(
                    saved, dest, row.StorageName, ct, progress, chunkPct);
            });

        // Reset the backup's run state so the card no longer shows a
        // "Last successful run on date X" verdict — the data that
        // backs that verdict is gone (at least for this destination).
        // Per-source size hints are also cleared since they referred
        // to the erased revision. Verify state is wiped for the same
        // reason. The user explicitly asked for this: "invalidate
        // the status of the backup writing to this destination so
        // it becomes like when it was never run."
        ServiceLocator.Config.Update(cfg =>
        {
            var b = cfg.Backups.FirstOrDefault(x => x.Id == saved.Id);
            if (b is null) return;
            b.LastRunStatus = BackupRunStatus.NeverRun;
            b.LastRunSummary = null;
            b.LastRunStartUtc = null;
            b.LastRunEndUtc = null;
            b.SourceLastSizeBytes.Clear();
            b.LastVerifyUtc = null;
            b.LastVerifyPass = null;
            b.LastVerifySummary = null;
        });

        // We deliberately DO NOT delete the per-backup repo dir here:
        // the same dir holds preferences for ALL targets of this backup,
        // and wiping it would force a fresh `duplicacy init` against
        // every other destination too — which can break encryption-key
        // continuity for cloud OAuth and risks "Snapshot already exists"
        // errors on the next run. Duplicacy will pick the storage back
        // up cleanly on next backup; the local repo metadata is fine
        // to keep.
    }

    [RelayCommand]
    private async Task PruneToLatestAsync(TargetRow row)
    {
        var owner = Owner();
        if (owner is null) return;
        var dlg = new DestructiveConfirmWindow(
            title: "Keep only the latest revision?",
            subtitle:
                $"All earlier revisions of «{Name}» on «{row.DestinationName}» will be deleted to reclaim space. " +
                "The most recent one is kept. Type OK to confirm.",
            confirmString: "OK",
            caseInsensitive: true);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;
        var saved = CommitToWorking();
        if (saved is null) return;
        var dest = ServiceLocator.Config.Current.Destinations.First(d => d.Id == row.DestinationId);

        // Same modal-progress + maintenance-gate pattern as
        // EraseDestinationAsync. The user explicitly asked for both:
        // a "wait until complete" dialog AND no concurrent backup /
        // restore overlap. Hooks the runner's LineWritten so the
        // dialog's status line ticks through "Deleted N of M chunks…"
        // as duplicacy emits per-chunk progress.
        await ErasingProgressWindow.RunAsync(
            owner,
            $"Pruning «{Name}» on «{row.DestinationName}»",
            async (progress, ct) =>
            {
                using var maintenance = await ServiceLocator.Maintenance.AcquireAsync(
                    $"Pruning «{Name}» on «{row.DestinationName}»", progress, cancelGracePeriod: null, ct);

                Action<string> handler = line =>
                {
                    var msg = StorageCleaner.TryFormatPruneProgressLineForUi(line);
                    if (msg is not null) progress.Report(msg);
                };
                ServiceLocator.Runner.LineWritten += handler;
                try
                {
                    var totalSources = saved.SourcePaths.Count;
                    var doneSources = 0;
                    // Prune across every source of the backup — each
                    // source is its own duplicacy repo, so "prune to
                    // latest" only touches the one whose repo we
                    // target.
                    foreach (var src in saved.SourcePaths)
                    {
                        ct.ThrowIfCancellationRequested();
                        doneSources++;
                        progress.Report(totalSources > 1
                            ? $"Pruning source {doneSources} of {totalSources}…"
                            : "Pruning revisions…");
                        await ServiceLocator.Runner.PruneToLatestAsync(saved, src, dest, row.StorageName, ct);
                    }
                }
                finally
                {
                    ServiceLocator.Runner.LineWritten -= handler;
                }
            });

        // Update the run-state metadata: prune doesn't invalidate the
        // backup itself (the latest revision is still there), so we
        // leave LastRunStatus / LastRunSummary alone — those still
        // accurately reflect the most recent successful run. The
        // user can still restore from the surviving revision.
    }

    /// <summary>
    /// Runs validation and populates per-field error properties. Returns
    /// true when the form is OK to save. Called both from Save (hard gate)
    /// and from CommitToWorking via Run-now / create-healthcheck
    /// side-operations so partial state still surfaces errors helpfully.
    /// </summary>
    public bool ValidateForm()
    {
        AttemptedSave = true;
        NameError = null;
        SourcesError = null;
        TargetsError = null;
        KeepPolicyError = null;
        SummaryError = null;

        var errors = new List<string>();

        if (SourcePaths.Count == 0)
        {
            SourcesError = "Add at least one folder to back up.";
            errors.Add("No source folders.");
        }

        if (Targets.Count == 0)
        {
            TargetsError = "Pick at least one destination.";
            errors.Add("No destinations.");
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            // Hard cap matches Backup.MaxNameLength (255). The display
            // Name is free-form per user request, but a name longer
            // than this couldn't survive a round-trip through any
            // filesystem-segment-derived path.
            if (Name.Trim().Length > Backup.MaxNameLength)
            {
                NameError = $"Name must be {Backup.MaxNameLength} characters or fewer.";
                errors.Add("Name is too long.");
            }
            else
            {
                var taken = ServiceLocator.Config.Current.Backups
                    .Any(b => b.Id != Working.Id
                              && string.Equals(b.Name?.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (taken)
                {
                    NameError = "Another backup already uses this name.";
                    errors.Add("Name is not unique.");
                }
            }
        }

        if (!KeepPolicyExplanation.Valid && !string.IsNullOrWhiteSpace(KeepPolicy))
        {
            KeepPolicyError = KeepPolicyExplanation.Summary;
            errors.Add("Retention policy is invalid.");
        }

        // Healthcheck UUID format check. We only verify shape here (a
        // duplicated network round-trip per Save would slow things down
        // for the common case where the field is blank or unchanged); a
        // live ping against healthchecks.io happens in SaveAsync below
        // when the user has actually typed something.
        HealthcheckIdError = null;
        if (!string.IsNullOrWhiteSpace(HealthcheckId)
            && !System.Guid.TryParse(HealthcheckId.Trim(), out _))
        {
            HealthcheckIdError = "That doesn't look like a valid Healthchecks.io UUID.";
            errors.Add("Healthchecks.io UUID is invalid.");
        }

        if (errors.Count > 0)
        {
            SummaryError = errors.Count == 1
                ? errors[0]
                : $"{errors.Count} things need attention:  " + string.Join("  ·  ", errors);
            OnPropertyChanged(nameof(HasAnyError));
            _log.Information("BackupEditor validation failed: {Errors}", string.Join(" | ", errors));
            return false;
        }

        OnPropertyChanged(nameof(HasAnyError));
        return true;
    }

    /// <summary>
    /// Capture current editor state into a Backup snapshot — used by
    /// the unified creation window when the user toggles Advanced→Easy
    /// mid-flow. Doesn't validate or persist; the resulting Backup may
    /// be incomplete. The Easy wizard maps a useful subset of this back
    /// onto its own fields and carries the rest as <c>Working</c> so a
    /// second toggle to Advanced restores everything.
    /// </summary>
    public Backup BuildSnapshot()
    {
        // Reuse CommitToWorking which writes every field through but
        // doesn't persist. Call from a synchronous path so it's safe in
        // a window-close handler.
        try { CommitToWorking(); } catch { /* best-effort */ }
        return Working;
    }

    public Backup? CommitToWorking()
    {
        // Lock in the snapshot id seed BEFORE we overwrite Working.Name
        // with the typed value. For a pre-existing backup whose seed
        // is still empty (config predates this field), the seed must
        // be the on-disk name — not whatever the user just typed —
        // otherwise renaming an existing backup would silently fork
        // the on-storage chain to a new id and orphan every prior
        // revision. The seed is ALWAYS sanitised (it's used to derive
        // the Duplicacy snapshot id) even though the display Name is
        // free-form.
        if (string.IsNullOrEmpty(Working.SnapshotIdSeed) && !string.IsNullOrEmpty(Working.Name))
            Working.SnapshotIdSeed = SanitizeName(Working.Name);

        // Resolve name. Blank → auto-generated from sources + random
        // suffix. Otherwise → use the user's typed string verbatim
        // (just trimmed). The previous code ran SanitizeName on every
        // save which turned "Hello World" into "Hello_World" and
        // lower-cased some of it; the user explicitly asked for the
        // friendly name to be left alone: "people could rename it to
        // anything (still enforce max 255 chars)." Filesystem-safe
        // sanitisation happens at use time in AppPaths.Sanitize and
        // SnapshotIdSeed; the displayed Name stays as the user
        // entered it.
        if (string.IsNullOrWhiteSpace(Name))
        {
            Working.Name = NameGenerator.ForBackup(
                SourcePaths.ToList(),
                candidate => ServiceLocator.Config.Current.Backups
                    .Any(b => b.Id != Working.Id
                              && string.Equals(b.Name?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            // Trim only — preserve internal spaces / punctuation /
            // case. The 255-char cap is enforced in ValidateForm.
            Working.Name = Name.Trim();
        }

        // New-backup fallback: pre-assignment block above only fires
        // when a prior name existed. New backups had no pre-existing
        // name so we seed from the just-computed name (sanitised so
        // the snapshot id is filesystem-safe regardless of what the
        // user typed).
        if (string.IsNullOrEmpty(Working.SnapshotIdSeed))
            Working.SnapshotIdSeed = SanitizeName(Working.Name);
        Working.SourcePaths = SourcePaths
            .Select(s => s?.Trim() ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        // 0 is a valid stored value meaning "auto" — don't floor to 1.
        Working.Threads = Math.Max(0, Threads);
        Working.UseVss = UseVss;
        Working.KeepPolicy = KeepPolicy?.Trim() ?? "";
        Working.PruneAfterBackup = PruneAfterBackup;
        Working.CheckAfterBackup = CheckAfterBackup;
        Working.AbortOnMeteredNetwork = AbortOnMeteredNetwork;
        Working.BandwidthLimitKBps = Math.Max(0, BandwidthLimitKBps);
        Working.ExtraBackupArgs = ExtraBackupArgs ?? "";
        Working.Enabled = Enabled;
        Working.FiltersText = FiltersText ?? "";
        Working.HealthcheckId = string.IsNullOrWhiteSpace(HealthcheckId) ? null : HealthcheckId.Trim();

        // Healthcheck inline API-key promotion intentionally lives in
        // SaveAsync, not here. CommitToWorking is also called by Run-now,
        // Filter-Simulate, Erase, and Prune — none of which should write
        // a fresh API key into Settings as a side effect of validation
        // / preview operations.

        Working.Schedule.Frequency = Frequency;
        Working.Schedule.TimeOfDay = TimeOfDay;
        Working.Schedule.HourlyInterval = Math.Max(1, HourlyInterval);
        Working.Schedule.SkipOnBattery = SkipOnBattery;
        Working.Schedule.StopOnBattery = StopOnBattery;
        Working.Schedule.RequireNetwork = RequireNetwork;
        Working.Schedule.CatchUpMissedRuns = CatchUpMissed;

        Working.Targets = Targets.Select(t => new BackupTarget
        {
            DestinationId = t.DestinationId,
            StorageName = t.StorageName,
            ThreadsOverride = t.ThreadsOverride,
        }).ToList();
        return Working;
    }

    /// <summary>
    /// Synchronous save used by unit tests and legacy callers. Does NOT
    /// run the remote-collision probe. Production UI goes through
    /// <see cref="SaveAsync"/> so the probe can do its thing.
    /// </summary>
    public Backup? Save()
    {
        if (!ValidateForm()) return null;
        return CommitToWorking();
    }

    /// <summary>
    /// Full save path: validate → probe each target destination for
    /// existing snapshot-ids → roll an auto-name that's unique both
    /// locally AND remotely → commit. Returns null if validation fails
    /// or the user cancelled mid-probe. The probe step is best-effort:
    /// if it errors out the save still proceeds using local-only
    /// uniqueness (a logged warning captures the reason).
    /// </summary>
    public async Task<Backup?> SaveAsync(CancellationToken ct)
    {
        if (!ValidateForm()) return null;

        // Refuse to overwrite config while the orchestrator is mid-run for
        // this backup — the running cell holds a snapshot of sources +
        // targets at start time, and changing those underneath it could
        // produce inconsistent metadata or break the cell's repo dir
        // assumptions. Editor stays open with the user's changes intact;
        // they can retry once the run finishes.
        if (!IsNew && ServiceLocator.Orchestrator.IsRunning(Working.Id))
        {
            // Tailor the message: a plain Save tells the user to wait;
            // Save & Run additionally hints that they need to Stop the
            // current run if they want to apply changes and re-run.
            var msg = RunAfterSaveRequested
                ? "This backup is already running. Stop it from the Backups list (the Stop button), then come back to save your changes and run again."
                : "This backup is currently running. Wait for it to finish (or stop it from the Backups list) before saving changes.";
            SummaryError = msg;
            OnPropertyChanged(nameof(HasAnyError));
            _log.Information("BackupEditor save refused — backup {Name} is running (saveAndRun={SaveAndRun})",
                Working.Name, RunAfterSaveRequested);
            // Reset the run-after-save flag so a subsequent plain Save
            // doesn't accidentally fire a run. Logged value above
            // captures the original click intent.
            RunAfterSaveRequested = false;
            return null;
        }

        // Live UUID check against the Healthchecks.io API. Shape was
        // already verified in ValidateForm; here we make sure the UUID
        // belongs to a real check in the user's project.
        //
        // Order matters: we use the inline-entered API key for the
        // verification call WITHOUT writing it to global config first,
        // so that a failed verification doesn't leak the user's key
        // into Settings. We only promote it into config after we know
        // the whole save is going to succeed.
        if (!string.IsNullOrWhiteSpace(HealthcheckId))
        {
            SaveBusy = true;
            SaveStatus = "Checking the Healthchecks.io UUID…";
            try
            {
                var verify = await ServiceLocator.Healthcheck.VerifyCheckUuidAsync(
                    HealthcheckId.Trim(),
                    inlineApiKey: HealthcheckApiKeyInput,
                    inlineBaseUrl: HealthcheckBaseUrlInput,
                    ct);

                if (verify.IsBlockingError)
                {
                    HealthcheckIdError = verify.Message;
                    SummaryError = verify.Message;
                    OnPropertyChanged(nameof(HasAnyError));
                    return null;
                }
                if (verify.Outcome == HealthcheckService.UuidVerificationOutcome.NetworkError)
                {
                    // Network failure blocks save — we promised the
                    // user the UUID is verified before persistence.
                    HealthcheckIdError = verify.Message;
                    SummaryError = verify.Message;
                    OnPropertyChanged(nameof(HasAnyError));
                    return null;
                }
                if (verify.Outcome == HealthcheckService.UuidVerificationOutcome.NoApiKey)
                {
                    // Can't verify without a key — refuse to persist
                    // an unverified UUID. The inline API-key + base-URL
                    // fields are right above the UUID input so the user
                    // can fix it without leaving the dialog.
                    var msg = "Add a Healthchecks.io API key (above, or in Settings) so we can verify this UUID before saving.";
                    HealthcheckIdError = msg;
                    SummaryError = msg;
                    OnPropertyChanged(nameof(HasAnyError));
                    return null;
                }
                if (verify.IsValid)
                {
                    HealthcheckStatus = verify.Message;
                }
            }
            finally
            {
                SaveBusy = false;
                SaveStatus = "";
            }
        }

        // UUID verified (or no UUID was supplied). Now — and only now —
        // safe to promote any inline-entered API key + base URL into
        // global config. Doing this earlier would leak credentials on
        // a failed verify; doing it later (in CommitToWorking) would
        // leak on every Erase / Prune / Run-now / Simulate path.
        if (!string.IsNullOrWhiteSpace(HealthcheckApiKeyInput))
        {
            ServiceLocator.Config.Update(cfg =>
            {
                cfg.Monitoring.HealthchecksApiKeyRef ??= "monitoring:hcApiKey";
                ServiceLocator.Secrets.Set(cfg.Monitoring.HealthchecksApiKeyRef, HealthcheckApiKeyInput.Trim());
                if (!string.IsNullOrWhiteSpace(HealthcheckBaseUrlInput))
                    cfg.Monitoring.HealthchecksBaseUrl = HealthcheckBaseUrlInput.Trim();
                cfg.Monitoring.HealthchecksEnabled = true;
            });
            OnPropertyChanged(nameof(NeedsHealthcheckSetup));
        }

        // Probe destinations only when the targets actually changed
        // since editor-open — a pure rename / schedule edit / filter
        // edit doesn't need to re-list every destination's snapshot
        // ids over the network. The user reported: "Changing the
        // friendly name has no reason to trigger the checking
        // destinations for existing backups." For brand-new backups
        // (IsNew) we always probe; the seed isn't locked yet and the
        // probe feeds the collision warning that catches "you typed
        // a name that's already on this storage from another
        // machine." For existing backups we compare the current
        // targets identity to the snapshot we stamped at editor-open.
        var targetsChanged = IsNew || ComputeTargetsKey() != _originalTargetsKey;
        var remoteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var probeFailures = new System.Collections.Generic.List<string>();
        if (Targets.Count > 0 && targetsChanged)
        {
            SaveBusy = true;
            SaveStatus = "Checking destinations for existing backups…";
            try
            {
                foreach (var row in Targets)
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = ServiceLocator.Config.Current.Destinations
                        .FirstOrDefault(d => d.Id == row.DestinationId);
                    if (dest is null) continue;

                    // Per-destination probe timeout — a slow / hanging
                    // cloud provider shouldn't block the entire save.
                    // We fall back to local-only collision check if the
                    // probe times out (logged as a warning).
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        var ids = await ServiceLocator.DestProbe.ListSnapshotIdsAsync(dest, probeCts.Token);
                        foreach (var id in ids) remoteNames.Add(id);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _log.Warning("Snapshot-id probe timed out for destination {Name} — proceeding without remote collision check", dest.Name);
                        probeFailures.Add($"«{dest.Name}»: timeout");
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Snapshot-id probe threw for destination {Name}", dest.Name);
                        probeFailures.Add($"«{dest.Name}»: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SaveBusy = false;
                SaveStatus = "";
                return null;
            }
            finally
            {
                SaveBusy = false;
                SaveStatus = "";
            }
        }

        // Surface probe failures as a warning banner. The first time the
        // banner appears, the save is refused so the user actually reads
        // it; a second click (with no intervening Name/Targets change)
        // is treated as "I've read it, proceed".
        string? newWarning = null;
        if (probeFailures.Count > 0)
        {
            newWarning =
                "Couldn't fully check the destination(s) for existing backups before saving — " +
                "if a backup with the same name already exists there, the new chain may share " +
                "snapshots with it. " + string.Join(" · ", probeFailures);
        }

        // Resolve name here (rather than inside CommitToWorking) so the
        // remote-collision set participates. CommitToWorking's own fallback
        // stays for the sync Save() path.
        if (string.IsNullOrWhiteSpace(Name))
        {
            var localTaken = ServiceLocator.Config.Current.Backups
                .Where(b => b.Id != Working.Id)
                .Select(b => b.Name?.Trim() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Name = NameGenerator.ForBackup(
                SourcePaths.ToList(),
                candidate => localTaken.Contains(candidate) || remoteNames.Contains(candidate));
        }
        else if (Targets.Count > 0 && remoteNames.Count > 0)
        {
            // The user typed a name. Check whether a snapshot id derived
            // from that name already exists at any of the targets — if
            // so, a NEW backup with this name would write to the same
            // chain (data merges between this and the other backup),
            // and the user almost certainly wants to know. This is
            // warning-only because cross-machine snapshot sharing is a
            // documented Duplicacy feature.
            var typedSnapshotPrefix = Backup.SanitizeName(Name.Trim());
            var collidingIds = remoteNames
                .Where(id => id.StartsWith(typedSnapshotPrefix, StringComparison.OrdinalIgnoreCase))
                .Take(3) // surface up to 3 in the warning
                .ToList();
            // Suppress the warning when EDITING — the existing backup's
            // own snapshot ids will obviously match its own name.
            if (collidingIds.Count > 0 && IsNew)
            {
                var ids = string.Join(", ", collidingIds);
                // Collision warning supersedes (or merges with) any
                // probe-failure warning we already collected.
                newWarning = (newWarning is null ? "" : newWarning + " · ") +
                    $"A backup with this name (or one starting with «{typedSnapshotPrefix}») already exists at the destination(s): {ids}. " +
                    "If that's another machine pointing at the same storage, this backup will share its snapshot chain (data merges). " +
                    "Pick a different Name above if you want a separate chain.";
            }
        }

        // Apply the warning + gate. Refuse the first attempt so the user
        // actually sees the banner; pass through on a subsequent click.
        if (newWarning is not null)
        {
            if (!_probeWarningAcknowledged)
            {
                ProbeWarning = newWarning;
                _probeWarningAcknowledged = true;
                return null;
            }
            // User has seen the warning and clicked Save again — proceed,
            // but keep the banner visible in case they want to re-read
            // it before this dialog closes (the parent view typically
            // closes on success anyway).
            ProbeWarning = newWarning;
        }
        else
        {
            ProbeWarning = null;
            _probeWarningAcknowledged = false;
        }

        // Stamp the mode the user committed in so a future Edit reopens
        // the same window. The unified creation path picks this up on
        // BackupCreationWindow construction.
        Working.LastEditMode = BackupEditMode.Advanced;
        return CommitToWorking();
    }

    /// <summary>
    /// Duplicacy snapshot-id rules: letters, digits, '_' and '-' only.
    /// Delegates to <see cref="Backup.SanitizeName"/> so the model and
    /// the editor agree on the canonical shape. (See Backup.cs for why
    /// every entry point — including legacy import — must funnel
    /// through this.)
    /// </summary>
    private static string SanitizeName(string? raw) => Backup.SanitizeName(raw);

    private static Avalonia.Controls.Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.MainWindow;
        return null;
    }
}

public sealed partial class TargetRow : ObservableObject
{
    public string DestinationId { get; }
    /// <summary>The destination's class kind — drives the per-kind
    /// class icon shown on the chip in the BackupEditor's Destinations
    /// card. Snapshotted at construction; if the user later edits the
    /// destination's Kind we'd recreate the row anyway.</summary>
    public DestinationKind DestinationKind { get; }
    [ObservableProperty] private string _destinationName;
    [ObservableProperty] private string _storageName;
    [ObservableProperty] private int? _threadsOverride;

    public TargetRow(Destination d, string storageName, int? threads)
    {
        DestinationId = d.Id;
        DestinationKind = d.Kind;
        _destinationName = d.Name;
        _storageName = storageName;
        _threadsOverride = threads;
    }
}

/// <summary>
/// UI wrapper around a source path that exposes a live folder-size
/// estimate. The probe runs off the UI thread and posts its result
/// back via Dispatcher; until it completes we show "Estimating size…".
/// Truncated probes (huge folders that hit the time cap) prefix the
/// number with "~" so the user knows it's a lower bound.
/// </summary>
public sealed partial class SourceChipVm : ObservableObject
{
    public string Path { get; }

    [ObservableProperty] private string _sizeLabel = "Estimating size…";
    [ObservableProperty] private bool _sizeReady;

    public SourceChipVm(string path) => Path = path;

    public async System.Threading.Tasks.Task StartSizeProbeAsync()
    {
        // Progress<T> captures the current sync context at construction
        // and marshals Report callbacks back to it — so running this on
        // the UI thread means each update lands on the UI thread
        // without a manual Dispatcher.Post. Running-total text reads as
        // "12.3 MB · 412 files…" while the walk is in flight, replacing
        // the previously-static "Estimating size…" placeholder that
        // looked stuck on slow / large trees.
        var progress = new System.Progress<SourceSizeProgress>(p =>
        {
            if (SizeReady) return; // walk finished while a stale Report was in flight
            SizeLabel = p.FileCount == 0
                ? "Reading folder…"
                : $"{HumanBytes(p.Bytes)} · {p.FileCount:N0} files…";
        });

        try
        {
            var result = await ServiceLocator.SizeProbe.GetOrComputeAsync(
                Path, System.Threading.CancellationToken.None, progress);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var prefix = result.Truncated ? "~" : "";
                SizeLabel = result.FileCount == 0
                    ? "Empty or unreadable"
                    : $"{prefix}{HumanBytes(result.Bytes)} · {result.FileCount:N0} files";
                SizeReady = true;
            });
        }
        catch (System.Exception)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SizeLabel = "Size unavailable";
                SizeReady = true;
            });
        }
    }

    private static string HumanBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024L * 1024 => $"{b / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):F1} MB",
        < 1024L * 1024 * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):F2} GB",
        _ => $"{b / (1024.0 * 1024 * 1024 * 1024):F2} TB",
    };
}

/// <summary>
/// One detected cloud-sync folder + a checkbox-bound flag for whether
/// the user wants to exclude it from this backup. Default: checked,
/// because someone backing up "everything" almost never wants to also
/// back up data already living in Dropbox/OneDrive/Drive — that's
/// duplicate work + duplicate storage cost.
/// </summary>
public sealed partial class CloudExcludeChoice : ObservableObject
{
    public RecommendedFilters.CloudSyncFolder Folder { get; }
    [ObservableProperty] private bool _isSelected = true;

    public string Label => Folder.Label;
    public string Path  => Folder.Path;

    public CloudExcludeChoice(RecommendedFilters.CloudSyncFolder folder) => Folder = folder;
}
