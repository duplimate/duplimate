using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Services;

namespace Duplimate.ViewModels;

/// <summary>
/// Hosts the four navigation-target view-models. <see cref="Backups"/>
/// is the landing page so it's eagerly constructed; the rest are
/// lazy-initialised on first navigation. Lazy-init is meaningful for
/// LogsViewModel especially — it subscribes to
/// <see cref="DuplicacyRunner.LineWritten"/> on construction and starts
/// buffering every stdout byte from any in-flight backup, even when
/// the user is sitting on the Backups tab and never opens the Logs
/// view. For multi-GB backups that's MBs of throwaway buffer growth.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private NavItem _selectedNav = NavItem.Backups;
    [ObservableProperty] private ViewModelBase? _currentPage;

    public BackupsViewModel Backups { get; }

    // Lazy-init: constructed on first access. Accessing these getters
    // is thread-safe via LazyInitializer.EnsureInitialized — without
    // it, two concurrent accesses (e.g. nav from a hotkey while a
    // Config.Changed handler also touches the property) could each
    // create a fresh VM, with the loser silently discarded after
    // having already subscribed to ServiceLocator events. The
    // discarded VM would leak its handlers for the rest of the
    // process lifetime.
    private DestinationsViewModel? _destinations;
    private LogsViewModel? _logs;
    private RestoreViewModel? _restore;
    private SettingsViewModel? _settings;

    public DestinationsViewModel Destinations =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _destinations, () => new DestinationsViewModel());
    public LogsViewModel Logs =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _logs, () => new LogsViewModel());
    public RestoreViewModel Restore =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _restore, () => new RestoreViewModel());
    public SettingsViewModel Settings =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _settings, () => new SettingsViewModel());

    public MainWindowViewModel()
    {
        // Backups is the landing page so it's worth the up-front cost —
        // anything else has to wait for first nav.
        Backups = new BackupsViewModel();
        CurrentPage = Backups;

        // Eager-init LogsViewModel so it subscribes to
        // Orchestrator.RunStarted / Runner.LineWritten /
        // AppNavigator.LiveActivityStarted from app startup. The
        // user reported live logs not appearing for a fast-fail
        // backup run kicked off from the Backups page — root cause
        // was that LogsViewModel was lazily created on first nav to
        // the Logs tab, so any run that started before that nav
        // happened was missed by every event handler. Discarding
        // the result is fine; the property setter caches it via
        // LazyInitializer.
        _ = Logs;

        AppNavigator.Requested += OnNavRequested;
    }

    private void OnNavRequested(NavItem target) => SelectedNav = target;

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value switch
        {
            NavItem.Backups      => Backups,
            NavItem.Destinations => Destinations,
            NavItem.Logs         => Logs,
            NavItem.Restore      => Restore,
            NavItem.Settings     => Settings,
            _ => Backups,
        };

        // Restore is a step-by-step wizard, not a stateful workspace.
        // Every fresh nav to it should restart from step 1 — otherwise
        // a user who half-finished a restore yesterday lands back in
        // the middle of the prior flow with stale Selected* values
        // (and that prior flow may have referenced a backup or
        // destination they've since deleted, leaving the view in a
        // broken state). The PreselectRestoreBackup signal — which
        // fires AFTER nav — re-applies whichever backup the BackupCard
        // was clicked from, so this reset doesn't conflict with the
        // "Restore from this card" path.
        if (value == NavItem.Restore && _restore is not null)
        {
            try { _restore.RestartWizard(); }
            catch { /* defensive — never block nav */ }
        }
    }

    public void Dispose()
    {
        AppNavigator.Requested -= OnNavRequested;
        Backups.Dispose();
        _destinations?.Dispose();
        _logs?.Dispose();
        _restore?.Dispose();
        _settings?.Dispose();
    }
}

/// <summary>
/// Sidebar navigation slots. Dashboard removed — it's been merged into
/// Backups (see BackupsView). The enum value is kept around as an alias
/// (Backups) at the route layer so any old persisted state or code path
/// passing NavItem.Dashboard still lands on Backups.
/// </summary>
public enum NavItem { Backups, Destinations, Logs, Restore, Settings }
