using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Duplimate.Models;
using Duplimate.ViewModels;

namespace Duplimate.Views;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _vm;

    public OnboardingWindow() : this(seed: null, isFirstBackup: true) { }

    /// <summary>
    /// Construct with optional seed (state mapped from a prior Advanced
    /// session via a mid-flow Easy/Advanced toggle) and a header copy
    /// override (first-backup vs. subsequent vs. edit).
    /// </summary>
    public OnboardingWindow(Backup? seed, bool isFirstBackup, bool isEdit = false)
    {
        _vm = new OnboardingViewModel(seed);
        if (isEdit)               _vm.HeaderTitle = $"Edit «{seed?.Name ?? "backup"}»";
        else if (isFirstBackup)   _vm.HeaderTitle = "Let's get your first backup running.";
        else                      _vm.HeaderTitle = "Let's add a new backup.";
        DataContext = _vm;
        InitializeComponent();
    }

    /// <summary>True iff the user clicked "Save and run my first backup"
    /// and the wizard committed successfully. Parent reads this on close
    /// to decide whether to navigate to Activity &amp; logs.</summary>
    public bool DidFinish { get; private set; }

    /// <summary>True iff the user clicked the Advanced toggle. Parent
    /// reads <see cref="SwitchSnapshot"/> for the state to hand to the
    /// Advanced editor.</summary>
    public bool SwitchToAdvanced { get; private set; }
    public Backup? SwitchSnapshot { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnFinish(object? sender, RoutedEventArgs e)
    {
        // async void exception trap — see BackupEditorWindow.OnSave.
        try
        {
            await _vm.FinishCommand.ExecuteAsync(null);
            if (_vm.SavedAndRan)
            {
                DidFinish = true;
                Close(true);
            }
        }
        catch (Exception ex)
        {
            Duplimate.Services.AppLogger.For<OnboardingWindow>().Error(ex, "OnFinish threw");
        }
    }

    /// <summary>
    /// "Save" (no immediate run). Wires the schedule + persists the
    /// backup but doesn't kick off the orchestrator. Closes the window
    /// either way; the parent doesn't navigate to Activity & logs since
    /// nothing's running.
    /// </summary>
    private async void OnSaveOnly(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.SaveOnlyCommand.ExecuteAsync(null);
            if (_vm.SavedNoRun) Close(false);
        }
        catch (Exception ex)
        {
            Duplimate.Services.AppLogger.For<OnboardingWindow>().Error(ex, "OnSaveOnly threw");
        }
    }

    /// <summary>
    /// Easy → Advanced toggle. Capture current wizard state into a
    /// Backup snapshot, surface it via <see cref="SwitchSnapshot"/>, and
    /// close — the parent (BackupsViewModel.AddNew/Edit) reopens the
    /// Advanced editor with the snapshot. Validation is intentionally
    /// skipped here: the user is in the middle of editing, and
    /// switching modes shouldn't fail because step 2 hasn't been
    /// completed yet.
    /// </summary>
    private void OnSwitchToAdvanced(object? sender, RoutedEventArgs e)
    {
        SwitchSnapshot = _vm.BuildSnapshot();
        SwitchToAdvanced = true;
        Close(false);
    }
}
