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
/// Multi-source backups land on a checkbox picker before the
/// destination step. Snapshot two states: all checked (default)
/// and one-of-three checked (the path that flips the wizard out
/// of MultiSourceMode and back into the per-source flow).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S20_RestorePickSourceMulti : ScenarioBase
{
    protected override string ScenarioName => "S20-restore-picksource-multi";

    [AvaloniaFact] public async Task Run_S20() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("d") };
        var backup = new Backup
        {
            Name = "MultiSource",
            SourcePaths =
            {
                ctx.MakeSource("docs",   seed: 1, targetBytes: 200_000),
                ctx.MakeSource("photos", seed: 2, targetBytes: 200_000),
                ctx.MakeSource("music",  seed: 3, targetBytes: 200_000),
            },
            LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var vm = new RestoreViewModel { SelectedBackup = backup };
        foreach (var s in backup.SourcePaths)
            vm.SourcesForBackup.Add(new SourcePickRow(s, vm, isSelected: true));
        var view = new RestoreView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);

        vm.CurrentStep = RestoreViewModel.Step.PickSource;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "picksource all checked default",
            "Default state — every source ticked, Next: pick destination enabled.");

        // Untick the first two so only the third is checked. The
        // wizard then goes back through the single-source path.
        vm.SourcesForBackup[0].IsSelected = false;
        vm.SourcesForBackup[1].IsSelected = false;
        vm.OnSourcePickRowToggled();
        ctx.Driver.Pump();
        ctx.Snapshot(host, "picksource one checked",
            "Only the music source ticked — single-source path on Next.");

        host.Close();
        return Task.CompletedTask;
    }
}
