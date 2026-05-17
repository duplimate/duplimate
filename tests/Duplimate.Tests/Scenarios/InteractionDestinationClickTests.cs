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
/// Real interaction tests, not state-seed snapshots. Use Avalonia.Headless
/// to fire actual pointer events at pixel coordinates; assert the bound
/// VM property changed. The user reported that the Restore wizard's
/// "Which destination to read from" rows can't be clicked — earlier
/// snapshot-only scenarios couldn't catch that because they never
/// exercised the routed-event pipeline.
/// </summary>
public class InteractionDestinationClickTests
{
    [AvaloniaFact]
    public async Task ClickingARestoreDestinationRow_SetsSelectedDestination()
    {
        ResetConfig();

        // Two destinations so the picker actually renders the row
        // ItemsControl. Single-destination short-circuits to
        // EnterAfterDestinationAsync.
        var d1 = new Destination
        {
            Id = "dest-1",
            Name = "Local — primary",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"D:\fake-primary",
        };
        var d2 = new Destination
        {
            Id = "dest-2",
            Name = "Local — secondary",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"D:\fake-secondary",
        };
        var backup = new Backup
        {
            Name = "ClickTest",
            SourcePaths = { @"D:\fake-source" },
            LastRunStatus = BackupRunStatus.Success,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = d1.Id, StorageName = "p" });
        backup.Targets.Add(new BackupTarget { DestinationId = d2.Id, StorageName = "s" });
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(d1);
            cfg.Destinations.Add(d2);
            cfg.Backups.Add(backup);
        });

        var vm = new RestoreViewModel { SelectedBackup = backup };
        // Mirror the wizard's flow into the destination step: a
        // single source, both destinations populated, sitting on
        // PickDestination.
        vm.SourcesForBackup.Add(new SourcePickRow(backup.SourcePaths[0], vm, isSelected: true));
        vm.SelectedSourcePath = backup.SourcePaths[0];
        vm.MultiSourceMode = false;
        vm.DestinationsForBackup.Add(d1);
        vm.DestinationsForBackup.Add(d2);
        vm.DestinationPickRows.Add(new DestinationPickRow(d1));
        vm.DestinationPickRows.Add(new DestinationPickRow(d2));
        vm.CurrentStep = RestoreViewModel.Step.PickDestination;

        var view = new RestoreView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        // Multiple layout passes — Avalonia's measure/arrange settles
        // on the second pass once styled-property defaults populate.
        for (int i = 0; i < 4; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        // Locate the SECOND destination row's Button (we'll click
        // it and assert the VM moves SelectedDestination from null
        // to dest-2).
        var rowButtons = view.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("picker-row-button"))
            .ToList();
        Assert.Equal(2, rowButtons.Count);
        var targetButton = rowButtons[1];
        // The Button's DataContext is now a DestinationPickRow
        // wrapper around the Destination — the post-fix shape.
        var targetRow = (DestinationPickRow)targetButton.DataContext!;
        Assert.Same(d2, targetRow.Destination);

        // Sanity check before the click.
        Assert.Null(vm.SelectedDestination);

        // Real pointer click via Avalonia's headless helper. The
        // earlier scenario suite skipped this — it just rendered
        // PNGs — so a broken click target slipped through.
        var bounds = targetButton.Bounds;
        var pos = targetButton.TranslatePoint(
            new Point(bounds.Width / 2, bounds.Height / 2), window) ?? default;
        window.MouseDown(pos, MouseButton.Left);
        window.MouseUp(pos, MouseButton.Left);
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(d2, vm.SelectedDestination);

        // The radio dot is a visual confirmation — without
        // IsChecked updating, the click looks like a no-op even
        // when the VM already moved. Pin both halves: the
        // clicked row's RadioButton should be Checked, and the
        // other row's should NOT be.
        var rowRadios = view.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(r => r.GroupName == "pickDest")
            .ToList();
        Assert.Equal(2, rowRadios.Count);
        var clicked   = rowRadios.First(r => r.DataContext is DestinationPickRow row && ReferenceEquals(row.Destination, d2));
        var unclicked = rowRadios.First(r => r.DataContext is DestinationPickRow row && ReferenceEquals(row.Destination, d1));
        Assert.True(clicked.IsChecked == true,
            "Clicked row's RadioButton.IsChecked did not update — RefEq binding regressed.");
        Assert.True(unclicked.IsChecked != true,
            "Unclicked row's RadioButton.IsChecked stayed true — group exclusivity broken.");

        window.Close();
    }

    private static void ResetConfig() =>
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
}
