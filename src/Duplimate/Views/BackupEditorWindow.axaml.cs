using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;

namespace Duplimate.Views;

public partial class BackupEditorWindow : Window
{
    private readonly BackupEditorViewModel _vm;

    public BackupEditorWindow() : this(new Backup(), true) { }

    public BackupEditorWindow(Backup working, bool isNew)
        : this(working, isNew, isFirstBackup: false) { }

    public BackupEditorWindow(Backup working, bool isNew, bool isFirstBackup)
    {
        _vm = new BackupEditorViewModel(working, isNew);
        // Header copy mirrors the Easy mode wizard so a mid-flow toggle
        // lands the user on a familiar phrasing rather than "Backup
        // settings".
        if (!isNew)               _vm.HeaderTitle = $"Edit «{working.Name}»";
        else if (isFirstBackup)   _vm.HeaderTitle = "Let's get your first backup running.";
        else                      _vm.HeaderTitle = "Let's add a new backup.";
        DataContext = _vm;
        InitializeComponent();

        // Escape closes without saving — matches OS dialog convention.
        // Enter is intentionally NOT bound to Save here: the editor
        // contains free-form multi-line text fields (filters, extra
        // args), so binding Enter globally would corrupt typing.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>True when the user confirmed via "Save &amp; Run" rather
    /// than plain "Save". The parent <see cref="ViewModels.BackupsViewModel"/>
    /// reads this on dialog close so it can fire the Orchestrator after
    /// persisting.</summary>
    public bool RunAfterSave { get; private set; }

    /// <summary>True iff the user clicked the Easy toggle. Caller reads
    /// <see cref="SwitchSnapshot"/> for the state to seed the wizard.</summary>
    public bool SwitchToEasy { get; private set; }
    public Backup? SwitchSnapshot { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        // async void exception trap: SaveAsync runs remote probes and
        // can throw on network errors that bubble out of the regular
        // validation path. Without this catch the exception goes to
        // Avalonia's unobserved-async-void path and the user sees the
        // Save button do nothing.
        try
        {
            _vm.RunAfterSaveRequested = false;
            var saved = await _vm.SaveAsync(CancellationToken.None);
            if (saved is null) return;   // validation failed or user cancelled
            Close(saved);
        }
        catch (Exception ex)
        {
            AppLogger.For<BackupEditorWindow>().Error(ex, "OnSave threw");
            _vm.SummaryError = "Save failed: " + ex.Message;
        }
    }

    private async void OnSaveAndRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            _vm.RunAfterSaveRequested = true;
            var saved = await _vm.SaveAsync(CancellationToken.None);
            if (saved is null) return;
            RunAfterSave = true;
            Close(saved);
        }
        catch (Exception ex)
        {
            AppLogger.For<BackupEditorWindow>().Error(ex, "OnSaveAndRun threw");
            _vm.SummaryError = "Save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Advanced → Easy toggle. Capture current editor state into a
    /// Backup snapshot, surface it via <see cref="SwitchSnapshot"/>, and
    /// close — the parent reopens the wizard with the snapshot. We
    /// don't validate before closing: the user is mid-edit and switching
    /// modes shouldn't be gated by Advanced-only required fields.
    /// </summary>
    private void OnSwitchToEasy(object? sender, RoutedEventArgs e)
    {
        SwitchSnapshot = _vm.BuildSnapshot();
        SwitchToEasy = true;
        Close(null);
    }
}
