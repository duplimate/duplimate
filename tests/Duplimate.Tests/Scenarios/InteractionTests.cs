using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Real interaction tests — fire pointer events at pixel coords and
/// assert both the VM state AND the visible control state. The
/// snapshot-only scenarios (S01-S25) verify rendering; these verify
/// the wiring between user click and bound state, which is where
/// most "looks fine but doesn't work" bugs live (we caught the
/// destination-row regression with this pattern after 25 snapshot
/// scenarios missed it).
///
/// Each test seeds a minimal VM, materialises the real view, fires
/// a real click via Avalonia.Headless's MouseDown/MouseUp, pumps the
/// dispatcher, and asserts.
/// </summary>
public class InteractionTests
{
    // ---- helpers ----

    private static void ResetConfig() =>
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });

    private static void Settle(Window window, int passes = 4)
    {
        for (int i = 0; i < passes; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void ClickAt(Window window, Visual element)
    {
        var b = element.Bounds;
        var pt = element.TranslatePoint(new Point(b.Width / 2, b.Height / 2), window) ?? default;
        window.MouseDown(pt, MouseButton.Left);
        window.MouseUp(pt, MouseButton.Left);
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    // ---- 1: Banner attention-pill click jumps to the backup ----

    [AvaloniaFact]
    public void BannerAttentionPill_Click_FiresJumpToBackup()
    {
        ResetConfig();
        var dest = new Destination { Name = "D", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\fake" };
        var failed = new Backup
        {
            Id = "id-fail",
            Name = "Photos",
            SourcePaths = { @"D:\fake-source" },
            LastRunStatus = BackupRunStatus.Failed,
            LastRunSummary = "boom",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        failed.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(failed); });

        var listVm = new BackupsViewModel();
        string? jumpedTo = null;
        listVm.JumpToBackupRequested += id => jumpedTo = id;

        var view = new BackupsView { DataContext = listVm };
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        Settle(window);

        // The attention-pill renders inside the banner — find the
        // Button with class "attention-pill". We don't simulate the
        // click via the visual tree because the pill itself can be
        // routed through the pointer pipeline cleanly.
        var pill = view.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.Classes.Contains("attention-pill"));
        ClickAt(window, pill);

        Assert.Equal(failed.Id, jumpedTo);
        window.Close();
    }

    // ---- 1b: PickDestination Next button enables after selection ----

    [AvaloniaFact]
    public void PickDestinationNextButton_EnablesAfterSelection()
    {
        // The Next button binds IsEnabled to SelectedDestination via
        // IsNotNull. Pin the binding propagation: setting the VM
        // property must flip the Button's IsEnabled to true. The
        // S04 snapshot suggested the button stayed disabled visually
        // even with SelectedDestination set — this test reproduces
        // that against the real layout.
        ResetConfig();
        var d1 = new Destination { Id = "a", Name = "A", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\a" };
        var d2 = new Destination { Id = "b", Name = "B", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\b" };
        var b = new Backup { Name = "T", SourcePaths = { @"D:\src" } };
        b.Targets.Add(new BackupTarget { DestinationId = d1.Id, StorageName = "p" });
        b.Targets.Add(new BackupTarget { DestinationId = d2.Id, StorageName = "s" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(d1); cfg.Destinations.Add(d2); cfg.Backups.Add(b); });

        var vm = new RestoreViewModel { SelectedBackup = b };
        vm.SourcesForBackup.Add(new SourcePickRow(b.SourcePaths[0], vm, isSelected: true));
        vm.DestinationsForBackup.Add(d1);
        vm.DestinationsForBackup.Add(d2);
        vm.DestinationPickRows.Add(new DestinationPickRow(d1));
        vm.DestinationPickRows.Add(new DestinationPickRow(d2));
        vm.CurrentStep = RestoreViewModel.Step.PickDestination;

        var view = new RestoreView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        Settle(window, passes: 6);

        Button FindNext() => view.GetVisualDescendants()
            .OfType<Button>()
            .Where(btn => btn.IsVisible && btn.Classes.Contains("primary"))
            .FirstOrDefault(btn =>
            {
                var t = btn.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).FirstOrDefault()
                      ?? (btn.Content as string ?? "");
                return t.Contains("revision", StringComparison.OrdinalIgnoreCase);
            }) ?? throw new InvalidOperationException("No Next button found.");

        var next = FindNext();
        Assert.False(next.IsEffectivelyEnabled,
            "Next should start disabled when no destination is picked.");

        vm.SelectedDestination = d1;
        Settle(window, passes: 6);
        Assert.True(next.IsEffectivelyEnabled,
            "Next stayed disabled after SelectedDestination was set — IsNotNull binding regressed.");

        window.Close();
    }

    // ---- 2: Restore PickSource checkbox toggling drives HasAnyCheckedSource ----

    [AvaloniaFact]
    public void RestorePickSource_CheckboxToggle_UpdatesHasAnyCheckedSource()
    {
        ResetConfig();
        var dest = new Destination { Name = "D", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\d" };
        var b = new Backup { Name = "Multi", SourcePaths = { @"D:\src1", @"D:\src2", @"D:\src3" } };
        b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(b); });

        var vm = new RestoreViewModel { SelectedBackup = b };
        foreach (var s in b.SourcePaths)
            vm.SourcesForBackup.Add(new SourcePickRow(s, vm, isSelected: true));
        vm.CurrentStep = RestoreViewModel.Step.PickSource;

        var view = new RestoreView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        Settle(window);

        // All three start checked → HasAnyCheckedSource true.
        Assert.True(vm.HasAnyCheckedSource);

        // Find the first row's CheckBox and click it to UNCHECK.
        var checkboxes = view.GetVisualDescendants()
            .OfType<CheckBox>()
            .Where(c => c.DataContext is SourcePickRow)
            .ToList();
        Assert.Equal(3, checkboxes.Count);
        ClickAt(window, checkboxes[0]);
        Assert.False(((SourcePickRow)checkboxes[0].DataContext!).IsSelected);

        // Uncheck the rest. After all are unchecked, HasAnyCheckedSource
        // should flip false (the Next-button enable gate).
        ClickAt(window, checkboxes[1]);
        ClickAt(window, checkboxes[2]);
        Assert.False(vm.HasAnyCheckedSource);
        window.Close();
    }

    // ---- 3: Restore PickRevision row selection updates SelectedRevision ----

    [AvaloniaFact]
    public void RestorePickRevision_RowClick_UpdatesSelectedRevision()
    {
        ResetConfig();
        var dest = new Destination { Name = "D", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\d" };
        var b = new Backup { Name = "RevTest", SourcePaths = { @"D:\src" } };
        b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(b); });

        var vm = new RestoreViewModel
        {
            SelectedBackup = b,
            SelectedDestination = dest,
            SelectedSourcePath = b.SourcePaths[0],
            MultiSourceMode = false,
        };
        vm.SourcesForBackup.Add(new SourcePickRow(b.SourcePaths[0], vm, isSelected: true));
        vm.DestinationsForBackup.Add(dest);
        vm.DestinationPickRows.Add(new DestinationPickRow(dest, isSelected: true));

        // Three revisions, none initially selected.
        for (int i = 3; i >= 1; i--)
            vm.Revisions.Add(new RevisionSummary(i, DateTime.UtcNow.AddDays(-(4 - i)), 100, 1024));
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;

        var view = new RestoreView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 820, Content = view };
        window.Show();
        Settle(window);

        // Click the second revision (#2). The ListBox is bound to
        // Revisions; clicking should set SelectedRevision on the VM.
        var listbox = view.GetVisualDescendants().OfType<ListBox>().First();
        var rowItems = listbox.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(3, rowItems.Count);
        ClickAt(window, rowItems[1]);
        Assert.NotNull(vm.SelectedRevision);
        Assert.Equal(2, vm.SelectedRevision!.Value.Number);
        window.Close();
    }

    // ---- 4: Restore PickFiles search filter narrows the list ----

    [AvaloniaFact]
    public void RestorePickFiles_SearchFilter_NarrowsFilteredFiles()
    {
        ResetConfig();
        var dest = new Destination { Name = "D", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\d" };
        var b = new Backup { Name = "Filter", SourcePaths = { @"D:\src" } };
        b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(b); });

        var vm = new RestoreViewModel
        {
            SelectedBackup = b,
            SelectedDestination = dest,
            SelectedSourcePath = b.SourcePaths[0],
            SelectedRevision = new RevisionSummary(1, DateTime.UtcNow, 0, 0),
        };
        foreach (var p in new[] { "Documents/notes.txt", "Documents/budget.csv", "Photos/cat.jpg", "Photos/dog.jpg" })
            vm.AllFiles.Add(new RevisionFileRow(new RevisionFile(p, 1024, DateTime.UtcNow), vm));
        // Real flow: LoadFilesAsync populates AllFiles then explicitly
        // calls ApplyFileFilter. Mirror that here by changing FileFilter
        // away from its initial "" so OnFileFilterChanged fires.
        vm.FileFilter = " ";
        vm.FileFilter = "";
        Assert.Equal(4, vm.FilteredFiles.Count);

        vm.FileFilter = "Photos";
        Assert.Equal(2, vm.FilteredFiles.Count);
        Assert.All(vm.FilteredFiles, r => Assert.StartsWith("Photos", r.File.Path));

        vm.FileFilter = "nothing-matches-xyz";
        Assert.Empty(vm.FilteredFiles);
    }

    // ---- 5: Restore PickTarget radio toggle drives TargetOriginalSource ----

    [AvaloniaFact]
    public void RestorePickTarget_RadioToggle_DrivesTargetOriginalSource()
    {
        ResetConfig();
        var vm = new RestoreViewModel
        {
            SelectedSourcePath = @"D:\source",
            CustomTargetPath  = @"D:\custom-target",
            TargetOriginalSource = true,
        };
        vm.CurrentStep = RestoreViewModel.Step.PickTarget;
        var view = new RestoreView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 820, Content = view };
        window.Show();
        Settle(window);

        var radios = view.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(r => r.GroupName == "target")
            .ToList();
        Assert.Equal(2, radios.Count);

        // Currently TargetOriginalSource=true, so radios[0] is checked.
        // Click the second radio (different folder).
        ClickAt(window, radios[1]);
        Assert.False(vm.TargetOriginalSource);

        // Toggle back.
        ClickAt(window, radios[0]);
        Assert.True(vm.TargetOriginalSource);
        window.Close();
    }

    // ---- 6: Backup-card action buttons resolve to commands ----

    [AvaloniaFact]
    public void BackupCardActions_ButtonsResolveToCommands()
    {
        ResetConfig();
        var dest = new Destination { Name = "D", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\d" };
        var b = new Backup
        {
            Name = "Hello",
            SourcePaths = { @"D:\src" },
            LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
        };
        b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(b); });

        var listVm = new BackupsViewModel();
        var view = new BackupsView { DataContext = listVm };
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        Settle(window);

        // Walk the BackupCard's action strip — every button must
        // resolve to a non-null Command, otherwise the visual button
        // is a no-op (the user reported this exact silent-disable
        // pattern with Run-now earlier).
        var card = view.GetVisualDescendants().OfType<BackupCard>().First();
        var labelled = card.GetVisualDescendants()
            .OfType<Button>()
            .Select(btn => new
            {
                Button = btn,
                Label  = btn.GetVisualDescendants().OfType<TextBlock>()
                            .Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                          ?? (btn.Content as string ?? ""),
            })
            .ToList();

        foreach (var name in new[] { "Run now", "Edit", "Restore files", "Test restore" })
        {
            var hit = labelled.FirstOrDefault(x => string.Equals(x.Label?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            Assert.True(hit is not null, $"BackupCard is missing the '{name}' button entirely.");
            Assert.True(hit!.Button.Command is not null,
                $"'{name}' button has no bound Command — clicking it would silently no-op.");
        }
        window.Close();
    }

    // ---- 7: Onboarding "Next" button gates on having a source ----

    [AvaloniaFact]
    public void OnboardingStep1_Next_DisabledUntilSourceAdded()
    {
        ResetConfig();
        var win = new OnboardingWindow(seed: null, isFirstBackup: true) { Width = 900, Height = 760 };
        win.Show();
        Settle(win);

        // Hunt for the step-1 "Next" button. Onboarding labels its
        // forward button differently per step; on step 1 it's
        // "Next: pick a destination" (or similar).
        var primaries = win.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("primary"))
            .ToList();
        // At least one primary button exists on step 1.
        Assert.NotEmpty(primaries);

        var vm = (OnboardingViewModel)win.DataContext!;
        // No sources → forward command's CanExecute should be false
        // OR Next button should be IsEnabled=false. We assert via the
        // command path because the button might have a CanExecute
        // gate rather than IsEnabled binding.
        var nextLikely = primaries.FirstOrDefault(b =>
        {
            var label = b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault();
            return label is not null && label.Contains("Next", StringComparison.OrdinalIgnoreCase);
        });
        if (nextLikely?.Command is { } cmd)
            Assert.False(cmd.CanExecute(nextLikely.CommandParameter),
                "Onboarding step-1 Next button should be disabled with no sources picked.");

        // After adding a source, the Next path should open.
        vm.SourceChips.Add(new SourceChipVm(@"D:\test-source"));
        Settle(win);
        if (nextLikely?.Command is { } cmd2)
            Assert.True(cmd2.CanExecute(nextLikely.CommandParameter),
                "Onboarding Next stayed disabled after a source was added.");
        win.Close();
    }

    // ---- 8: Settings accent picker actually changes the Accent property ----

    [AvaloniaFact]
    public void SettingsAccent_Selection_PersistsToConfig()
    {
        ResetConfig();
        var vm = new SettingsViewModel();
        var initial = vm.Accent;

        // Pick whichever variant differs from the current one.
        var different = vm.AvailableAccents.First(a => a != initial);
        vm.Accent = different;

        Assert.Equal(different, vm.Accent);
        Assert.Equal(different, ServiceLocator.Config.Current.Preferences.Accent);
    }
}
