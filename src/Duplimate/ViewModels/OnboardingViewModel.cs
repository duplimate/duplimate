using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

/// <summary>
/// Three-step first-run wizard. Steers a brand-new user from "I just
/// installed this thing" to "my first backup is running" in three
/// decisions: pick what to back up, pick where, set a schedule. The
/// final screen also offers to install the Explorer right-click verb
/// (default on) so the user gets restore-by-right-click out of the box.
///
/// Why a separate wizard rather than reusing the BackupEditor:
///   - The editor is great for tweaking — six tabs, dozens of options.
///     A first-time user seeing that lands cold and bounces.
///   - The wizard is tightly opinionated: recommended exclusions are
///     pre-applied based on what was picked; threads, encryption,
///     keep-policy etc. all get sensible defaults the user can refine
///     later in the editor.
///   - Save & Run from step 3 fires the first backup so the user gets
///     immediate "yes, this works" feedback — same pattern as Dropbox's
///     onboarding.
/// </summary>
public sealed partial class OnboardingViewModel : ViewModelBase
{
    private static Serilog.ILogger _log => AppLogger.For<OnboardingViewModel>();

    public Backup Working { get; } = new();

    // ---- step navigation ----
    [ObservableProperty] private int _currentStep = 1;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(IsFinalStep));
    }
    public bool CanGoBack    => CurrentStep > 1;
    public bool CanGoForward => CurrentStep < 3;
    public bool IsFinalStep  => CurrentStep == 3;

    // ---- step 1: sources ----
    public ObservableCollection<SourceChipVm> SourceChips { get; } = new();
    [ObservableProperty] private string? _sourcesError;

    // ---- step 2: destinations (plural — Easy mode now supports
    //              multiple, just like Advanced) ----
    /// <summary>Every destination that exists in config — drives the
    /// "add a destination" ComboBox. Filtered against
    /// <see cref="SelectedDestinations"/> at picker render time so the
    /// user can't pick the same destination twice.</summary>
    public ObservableCollection<Destination> AvailableDestinations { get; } = new();
    /// <summary>The destinations the user has chosen for this backup.
    /// Rendered as light-blue chips with the per-kind class icon.
    /// Migrated from the previous single-pick SelectedDestination so
    /// Easy mode has the same multi-target capability as Advanced —
    /// user-reported: "if I add a Dropbox destination and then a Local
    /// one, only the last is kept".</summary>
    public ObservableCollection<Destination> SelectedDestinations { get; } = new();
    /// <summary>The destination currently selected in the "add a
    /// destination" ComboBox — distinct from
    /// <see cref="SelectedDestinations"/>, which holds the committed
    /// list. Cleared after each Add so the ComboBox doesn't keep the
    /// just-added entry highlighted.</summary>
    [ObservableProperty] private Destination? _destinationCandidate;
    [ObservableProperty] private string? _destinationError;
    public bool HasNoDestinations => AvailableDestinations.Count == 0;
    /// <summary>Available destinations minus those already selected — what the
    /// "add" ComboBox actually offers, so duplicate picks aren't possible.</summary>
    public System.Collections.Generic.IEnumerable<Destination> AvailableDestinationsForAdd =>
        AvailableDestinations.Where(d => !SelectedDestinations.Any(s => s.Id == d.Id));
    /// <summary>True iff there's at least one destination the user
    /// could still add. Drives the visibility of the "Pick a
    /// destination" dropdown row — when nothing's left to pick we
    /// hide the whole row instead of greying out a disabled Add
    /// button (cleaner UX than a disabled-state visual).</summary>
    public bool HasMoreDestinationsToAdd => AvailableDestinationsForAdd.Any();

    /// <summary>
    /// Label for the inline "create new destination" button. Reads
    /// "Or set up a new destination…" before the user has added any
    /// (the comma-flow makes it a clear alternative to the dropdown
    /// above), and "Add another destination" once at least one has
    /// been added — at that point the dropdown row is hidden anyway
    /// (see HasMoreDestinationsToAdd) and "another" reflects the
    /// user's mental model that they're stacking targets.
    /// </summary>
    public string CreateDestinationButtonLabel =>
        SelectedDestinations.Count > 0
            ? "Add another destination"
            : "Or set up a new destination…";

    // (CanAddDestinationCandidate removed — the shared
    // DestinationAddPicker UserControl gates its Add button
    // internally based on SelectedDestination's null-ness, so neither
    // VM needs to expose its own can-add flag any longer.)

    // ---- step 3: schedule + finishing touches ----
    public IReadOnlyList<ScheduleFrequency> AvailableFrequencies { get; } =
        new[] { ScheduleFrequency.Daily, ScheduleFrequency.Weekly, ScheduleFrequency.Hourly };
    [ObservableProperty] private ScheduleFrequency _frequency = ScheduleFrequency.Daily;
    [ObservableProperty] private TimeSpan _timeOfDay = TimeSpan.FromHours(20);
    [ObservableProperty] private int _hourlyInterval = 4;
    [ObservableProperty] private bool _applyRecommendedExclusions = true;
    /// <summary>Defaults to true on Windows: in user research most
    /// people want "right-click → restore" available without going
    /// hunting in Settings. They can untick if they don't want shell
    /// integration. False on macOS / Linux because the underlying
    /// integration isn't supported there yet — the wizard hides the
    /// checkbox via <see cref="IsShellExtensionAvailable"/> on those
    /// platforms.</summary>
    [ObservableProperty] private bool _installShellExtension = ShellIntegrationService.IsSupportedOnThisPlatform;

    /// <summary>
    /// Drives <c>IsVisible</c> on the "Add right-click restore" checkbox
    /// in <see cref="Views.OnboardingWindow"/>. False off-Windows so the
    /// option doesn't dangle in the UI on platforms where ticking it
    /// has no effect.
    /// </summary>
    public bool IsShellExtensionAvailable => ShellIntegrationService.IsSupportedOnThisPlatform;

    // ---- runtime ----
    [ObservableProperty] private bool _saveBusy;
    [ObservableProperty] private string _saveStatus = "";
    /// <summary>Set true on a successful Save & Run so the parent
    /// can navigate the user to Activity & logs to watch progress.</summary>
    public bool SavedAndRan { get; private set; }

    /// <summary>Header title — varies by entry point. "Let's get your
    /// first backup running" for first-time users, "Let's add a new
    /// backup" otherwise. Settable by the calling window.</summary>
    [ObservableProperty] private string _headerTitle = "Let's get your first backup running.";

    public OnboardingViewModel() : this(seed: null) { }

    /// <summary>
    /// Construct from an existing backup snapshot — used by the unified
    /// creation flow when the user toggles from Advanced to Easy
    /// mid-task. Maps the inbound Backup's state back into Easy-mode
    /// fields: source paths, the first target's destination, schedule
    /// frequency / time / hourly interval. Anything Advanced-only
    /// (multi-target, custom keep policy, threads override, filters
    /// text the user hand-edited, etc.) is left in <see cref="Working"/>
    /// so a second toggle back to Advanced restores it.
    /// </summary>
    public OnboardingViewModel(Backup? seed)
    {
        foreach (var d in ServiceLocator.Config.Current.Destinations) AvailableDestinations.Add(d);

        if (seed is not null)
        {
            // Carry the full backup forward as the "Working" copy so a
            // second mode-switch loses nothing the user typed in
            // Advanced.
            Working = seed;

            foreach (var p in seed.SourcePaths)
                SourceChips.Add(new SourceChipVm(p));

            // Multi-destination: pull EVERY target from the inbound
            // snapshot, not just the first. This is the round-trip
            // path for an Advanced→Easy toggle, and earlier we
            // dropped all but the first target on the way back.
            foreach (var t in seed.Targets)
            {
                var d = AvailableDestinations.FirstOrDefault(x => x.Id == t.DestinationId);
                if (d is not null && !SelectedDestinations.Any(s => s.Id == d.Id))
                    SelectedDestinations.Add(d);
            }

            Frequency = seed.Schedule.Frequency;
            TimeOfDay = seed.Schedule.TimeOfDay;
            HourlyInterval = seed.Schedule.HourlyInterval == 0 ? 4 : seed.Schedule.HourlyInterval;
        }

        // Whenever the SelectedDestinations changes, the "add" ComboBox
        // needs to re-filter — surface that as a property change so the
        // UI binding refreshes.
        SelectedDestinations.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(AvailableDestinationsForAdd));
            OnPropertyChanged(nameof(HasMoreDestinationsToAdd));
            OnPropertyChanged(nameof(CreateDestinationButtonLabel));
            if (SelectedDestinations.Count > 0) DestinationError = null;
        };
        AvailableDestinations.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasNoDestinations));
            OnPropertyChanged(nameof(AvailableDestinationsForAdd));
            OnPropertyChanged(nameof(HasMoreDestinationsToAdd));
        };
    }

    /// <summary>
    /// Capture the current wizard state into a Backup snapshot the
    /// caller can hand to the Advanced editor on a mid-flow toggle.
    /// Doesn't persist anything — pure state extraction.
    /// </summary>
    public Backup BuildSnapshot()
    {
        Working.SourcePaths = SourceChips.Select(c => c.Path).ToList();
        // Storage names must be UNIQUE across a backup's targets —
        // Duplicacy keys per-storage state by name. We derive the
        // name from the destination's Id (`target-{Id[..8]}`) so
        // the storage name in the Duplicacy preferences file is
        // traceable to the destination it represents — matches the
        // cloud subfolder convention (`duplimate-{Id[..8]}`)
        // and removes the previous "default" / "target2" / "target3"
        // index-based naming that didn't tell the user which target
        // a given storage entry was for.
        Working.Targets = SelectedDestinations
            .Select(d => new BackupTarget
            {
                DestinationId = d.Id,
                StorageName = StorageNameForDestination(d),
            })
            .ToList();
        Working.Schedule.Frequency = Frequency;
        Working.Schedule.TimeOfDay = TimeOfDay;
        Working.Schedule.HourlyInterval = HourlyInterval;
        return Working;
    }

    /// <summary>Generate a stable, traceable Duplicacy storage name
    /// from a destination's Id. Format: <c>target-{Id[..8]}</c>. The
    /// 8-char prefix is enough to be unique across a single backup's
    /// targets and short enough to read at a glance in the
    /// <c>.duplicacy/preferences</c> file.</summary>
    internal static string StorageNameForDestination(Destination d) =>
        $"target-{(string.IsNullOrEmpty(d.Id) ? "x" : d.Id.Substring(0, Math.Min(8, d.Id.Length)))}";

    [RelayCommand]
    private void GoBack()    { if (CanGoBack) CurrentStep--; }

    [RelayCommand]
    private void GoForward()
    {
        if (!ValidateCurrentStep()) return;
        if (CanGoForward) CurrentStep++;
    }

    private bool ValidateCurrentStep()
    {
        switch (CurrentStep)
        {
            case 1:
                if (SourceChips.Count == 0)
                {
                    SourcesError = "Pick at least one folder to back up.";
                    return false;
                }
                SourcesError = null;
                return true;
            case 2:
                if (SelectedDestinations.Count == 0)
                {
                    DestinationError = "Pick at least one destination — or set one up.";
                    return false;
                }
                DestinationError = null;
                return true;
            default:
                return true;
        }
    }

    // ---- step 1 commands ----

    [RelayCommand]
    private async Task AddSourceFromTreeAsync()
    {
        var owner = Owner();
        if (owner is null) return;
        var existing = SourceChips.Select(c => c.Path).ToList();
        var picker = new SourceTreePickerWindow(new ObservableCollection<string>(existing));
        var result = await picker.ShowDialog<System.Collections.Generic.List<string>?>(owner);
        if (result is null) return;
        SourceChips.Clear();
        foreach (var p in result) AddSource(p);
        if (SourceChips.Count > 0) SourcesError = null;
    }

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
        foreach (var f in picks)
        {
            var p = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            AddSource(p);
        }
        if (SourceChips.Count > 0) SourcesError = null;
    }

    [RelayCommand]
    private void RemoveSource(string path)
    {
        var chip = SourceChips.FirstOrDefault(c => c.Path == path);
        if (chip is not null) SourceChips.Remove(chip);
    }

    private void AddSource(string p)
    {
        if (string.IsNullOrEmpty(p)) return;
        if (SourceChips.Any(c => string.Equals(c.Path, p, StringComparison.OrdinalIgnoreCase))) return;
        var chip = new SourceChipVm(p);
        SourceChips.Add(chip);
        _ = chip.StartSizeProbeAsync();
    }

    // ---- step 2 commands ----

    [RelayCommand]
    private async Task CreateDestinationInlineAsync()
    {
        var owner = Owner();
        if (owner is null) return;
        // Simplified mode hides the Cloud subfolder field (it auto-
        // fills from the destination name on save) so first-time users
        // don't have to make decisions they don't need to make.
        var dlg = new DestinationEditorWindow(new Destination(), isNew: true, simplifiedMode: true);
        var result = await dlg.ShowDialog<Destination?>(owner);
        if (result is null) return;
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(result));
        AvailableDestinations.Add(result);
        // Auto-add the just-created destination to the selection — the
        // user clearly wants to use it (otherwise why create it).
        if (!SelectedDestinations.Any(s => s.Id == result.Id))
            SelectedDestinations.Add(result);
        OnPropertyChanged(nameof(HasNoDestinations));
        OnPropertyChanged(nameof(AvailableDestinationsForAdd));
        DestinationError = null;
    }

    /// <summary>Add the destination currently chosen in the "add a
    /// destination" ComboBox to the selection list. No-ops if the
    /// candidate is null or already in the list (the ComboBox filter
    /// usually prevents the latter, but defensive).</summary>
    [RelayCommand]
    private void AddDestination()
    {
        if (DestinationCandidate is null) return;
        if (SelectedDestinations.Any(s => s.Id == DestinationCandidate.Id)) return;
        SelectedDestinations.Add(DestinationCandidate);
        DestinationCandidate = null;
    }

    /// <summary>Remove a destination from the selected list. Bound to
    /// the × on each chip.</summary>
    [RelayCommand]
    private void RemoveDestination(Destination d)
    {
        if (d is null) return;
        var match = SelectedDestinations.FirstOrDefault(x => x.Id == d.Id);
        if (match is not null) SelectedDestinations.Remove(match);
    }

    /// <summary>Open the destination editor for this destination. The
    /// edited Destination is upserted into config so other backups
    /// referencing it stay in sync. Bound to the Edit button on each
    /// chip in step 2.</summary>
    [RelayCommand]
    private async Task EditDestinationAsync(Destination d)
    {
        if (d is null) return;
        var owner = Owner();
        if (owner is null) return;
        var dlg = new DestinationEditorWindow(d, isNew: false, simplifiedMode: true);
        var saved = await dlg.ShowDialog<Destination?>(owner);
        if (saved is null) return;
        // Upsert into config so the change is visible to every other
        // backup. Also refresh our chip list so name / kind changes
        // surface immediately.
        ServiceLocator.Config.Update(cfg =>
        {
            var idx = cfg.Destinations.FindIndex(x => x.Id == saved.Id);
            if (idx >= 0) cfg.Destinations[idx] = saved;
            else cfg.Destinations.Add(saved);
        });
        var avail = AvailableDestinations.FirstOrDefault(x => x.Id == saved.Id);
        if (avail is not null)
        {
            AvailableDestinations.Remove(avail);
            AvailableDestinations.Add(saved);
        }
        var sel = SelectedDestinations.FirstOrDefault(x => x.Id == saved.Id);
        if (sel is not null)
        {
            var idx = SelectedDestinations.IndexOf(sel);
            SelectedDestinations[idx] = saved;
        }
        OnPropertyChanged(nameof(AvailableDestinationsForAdd));
    }

    // ---- final commit ----

    /// <summary>
    /// True iff Save (without running) succeeded. Mirrors
    /// <see cref="SavedAndRan"/> — the parent reads exactly one of the
    /// two flags to decide where to navigate after the wizard closes.
    /// </summary>
    public bool SavedNoRun { get; private set; }

    [RelayCommand]
    private Task FinishAsync() => CompleteAsync(runImmediately: true);

    /// <summary>"Save" (no immediate run) variant of <see cref="FinishAsync"/>.
    /// Same persistence + scheduling, just doesn't kick the orchestrator.
    /// User asked for both buttons in the wizard footer.</summary>
    [RelayCommand]
    private Task SaveOnlyAsync() => CompleteAsync(runImmediately: false);

    private async Task CompleteAsync(bool runImmediately)
    {
        // Validate every step, not just the last — handles the user
        // navigating back, clearing required fields, then Finish.
        for (int s = 1; s <= 3; s++)
        {
            CurrentStep = s;
            if (!ValidateCurrentStep()) return;
        }

        SaveBusy = true;
        SaveStatus = "Saving your backup…";
        try
        {
            // Build the Backup model from wizard state. Multi-target:
            // every selected destination becomes a BackupTarget. The
            // first wizard didn't support this — the orchestrator will
            // run each (source × destination) cell sequentially.
            var sourcePaths = SourceChips.Select(c => c.Path).ToList();
            Working.SourcePaths = sourcePaths;
            // Storage names derived from destination Id — see
            // BuildSnapshot above + StorageNameForDestination.
            Working.Targets = SelectedDestinations
                .Select(d => new BackupTarget
                {
                    DestinationId = d.Id,
                    StorageName = StorageNameForDestination(d),
                })
                .ToList();
            Working.Schedule.Frequency = Frequency;
            Working.Schedule.TimeOfDay = TimeOfDay;
            Working.Schedule.HourlyInterval = HourlyInterval;
            Working.Enabled = true;

            // Lock in the snapshot id seed from the EXISTING name (if
            // any) before we overwrite it with the freshly-computed
            // one. Same rename-safety contract as the Advanced editor:
            // the Duplicacy snapshot chain on storage stays addressable
            // by its original id even if the user later renames the
            // backup. New backups (no prior name) get the seed set
            // from the just-assigned name below.
            if (string.IsNullOrEmpty(Working.SnapshotIdSeed) && !string.IsNullOrEmpty(Working.Name))
                Working.SnapshotIdSeed = Working.Name;

            // Only auto-name when there's no name yet. Earlier this
            // call ran unconditionally with a candidate-taken predicate
            // that didn't exclude Working.Id — so re-saving an Easy
            // backup saw its OWN name as taken and renamed it with a
            // fresh suffix on every save (the user's chosen name
            // churned visibly). Now: keep the existing Name on edits
            // and only generate one for genuinely-new backups. The
            // collision predicate also excludes the current backup so
            // a future generated-name path can't match itself.
            if (string.IsNullOrEmpty(Working.Name))
            {
                Working.Name = NameGenerator.ForBackup(
                    sourcePaths,
                    candidate => ServiceLocator.Config.Current.Backups
                        .Any(b => b.Id != Working.Id
                                  && string.Equals(b.Name?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)));
            }

            // New-backup fallback for the seed: pre-assignment block
            // didn't fire because there was no prior name. Use the
            // just-computed name (same value, no rename hazard).
            if (string.IsNullOrEmpty(Working.SnapshotIdSeed))
                Working.SnapshotIdSeed = Working.Name;

            // Apply recommended exclusions if the user kept the box
            // ticked. Defaults are derived from the source mix (drive
            // root → Windows-system block on, otherwise just caches).
            if (ApplyRecommendedExclusions)
            {
                var defaults = RecommendedFilters.SuggestForSources(sourcePaths);
                var cloud = RecommendedFilters.DetectCloudSyncFolders().ToList();
                Working.FiltersText = RecommendedFilters.RenderBlock(
                    defaults.ExcludeWindowsSystem,
                    defaults.ExcludeCachesAndJunk,
                    cloud);
            }

            // Persist + register schedule.
            // Stamp the mode the user committed in so a future Edit
            // reopens the wizard rather than the Advanced editor.
            Working.LastEditMode = BackupEditMode.Easy;
            // Upsert by id: when the user toggled Advanced→Easy mid-edit,
            // Working.Id already exists in config and a plain Add would
            // produce a duplicate entry. FindIndex+replace covers both
            // new and edit cases without divergent code paths.
            ServiceLocator.Config.Update(cfg =>
            {
                var idx = cfg.Backups.FindIndex(b => b.Id == Working.Id);
                if (idx >= 0) cfg.Backups[idx] = Working;
                else cfg.Backups.Add(Working);
            });
            try
            {
                // Use the running-exe path. UpsertTask wraps it with a
                // cmd.exe + stub fallback (see TaskSchedulerService),
                // and CanonicalInstaller.ReupsertIfExePathChanged fixes
                // every task on the next launch if the user moves the
                // exe.
                var actualExe = Environment.ProcessPath
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                ServiceLocator.Scheduler.UpsertTask(Working, actualExe);
            }
            catch (Exception ex)
            {
                // Surface the failure to the user — silently swallowing
                // it left the user thinking their backup was scheduled
                // when in fact no Task Scheduler entry exists. The
                // backup is still saved to config + a manual "Run now"
                // works, but auto-runs won't fire until the user
                // re-saves the backup.
                _log.Warning(ex, "Onboarding: schedule registration failed");
                // Fire-and-forget guarded against unobserved-task escape.
                var name = Working.Name;
                var reason = ex.Message;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ServiceLocator.Notifier.NotifyAsync(
                            title: $"Couldn't schedule «{name}»",
                            detail:
                                "Your backup is saved, but Windows Task Scheduler refused our request — " +
                                "auto-runs won't fire until this is fixed. Try re-saving the backup, " +
                                "or check Task Scheduler permissions. Reason: " + reason,
                            severity: NotificationSeverity.Failure);
                    }
                    catch (Exception nx) { _log.Warning(nx, "NotifyAsync (onboarding schedule) threw"); }
                });
            }

            // Optional: install the Explorer right-click verb.
            if (InstallShellExtension)
            {
                var ok = ShellIntegrationService.Install();
                if (!ok)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ServiceLocator.Notifier.NotifyAsync(
                                "Couldn't add Explorer right-click",
                                "We tried to add 'Restore an older version' to the Windows right-click menu but it didn't take. You can try again from Settings.",
                                NotificationSeverity.Warning);
                        }
                        catch (Exception nx) { _log.Warning(nx, "NotifyAsync (shell-ext) threw"); }
                    });
                }
            }

            _log.Information(
                "Onboarding finished: backup={Name} sources={SourceCount} destinations={DestCount} freq={Freq} excludes={Excludes} shellExt={ShellExt}",
                Working.Name, sourcePaths.Count, SelectedDestinations.Count,
                Frequency, ApplyRecommendedExclusions, InstallShellExtension);

            // Kick off the first run only if the user picked "Save and
            // run my first backup". The "Save" button persists + schedules
            // but doesn't run immediately — useful when the next-fire
            // window is hours away and the user just wants the schedule
            // wired up. Either way, we set exactly one of the two flags
            // so the parent can route correctly.
            if (runImmediately)
            {
                SavedAndRan = true;
                // Wrap in Task.Run with a catch-all so the discarded
                // Task can never raise UnobservedTaskException and crash
                // the process at GC time. The wizard closes immediately
                // after this point, so we deliberately use
                // CancellationToken.None — the run should outlive the
                // wizard's lifetime.
                var working = Working;
                _ = Task.Run(async () =>
                {
                    try { await ServiceLocator.Orchestrator.RunBackupAsync(working, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Onboarding first-run threw for {Name}", working.Name);
                    }
                });
            }
            else
            {
                SavedNoRun = true;
            }
        }
        finally
        {
            SaveBusy = false;
            SaveStatus = "";
        }
    }

    private static Avalonia.Controls.Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.MainWindow;
        return null;
    }
}
