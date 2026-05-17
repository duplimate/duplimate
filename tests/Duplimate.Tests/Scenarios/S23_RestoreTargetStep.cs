using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Restore wizard step 5 — pick target. Two snapshots covering
/// the "to original source" and "to a different folder" radio
/// states, including the OVERWRITE warning copy on the original-
/// source path. The recently-redesigned inline MORE OPTIONS
/// strip is also visible in both shots.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S23_RestoreTargetStep : ScenarioBase
{
    protected override string ScenarioName => "S23-restore-target-step";

    [AvaloniaFact] public async Task Run_S23() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("d") };
        var backup = new Backup { Name = "TargetTest", SourcePaths = { ctx.MakeSource("src", seed: 1, targetBytes: 100_000) } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var vm = new RestoreViewModel
        {
            SelectedBackup = backup,
            SelectedDestination = dest,
            SelectedSourcePath = backup.SourcePaths[0],
            CustomTargetPath = ctx.MakeRestoreTarget("custom"),
        };
        vm.SourcesForBackup.Add(new SourcePickRow(backup.SourcePaths[0], vm, isSelected: true));
        vm.DestinationsForBackup.Add(dest);

        var view = new RestoreView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);

        vm.CurrentStep = RestoreViewModel.Step.PickTarget;

        // 1: to original source — the OVERWRITE warning surfaces.
        vm.TargetOriginalSource = true;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "target original-source",
            "Original-source radio selected; OVERWRITE warning copy visible.");

        // 2: to different folder — TextBox + Browse button enabled.
        vm.TargetOriginalSource = false;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "target different-folder",
            "Different-folder radio selected; path text + Browse enabled, MORE OPTIONS strip below.");

        host.Close();
        return Task.CompletedTask;
    }
}
