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
/// Backup card while a run is in flight: the Run button hides,
/// the Stop button appears, the inline running indicator pulses,
/// and a per-source progress bar shows current bytes/total.
///
/// We can't truly run a backup in headless mode, so we seed the
/// VM into a "running" state via SetRunning + a synthetic progress
/// snapshot. The visual outcome is what we care about — the live-
/// run wiring itself is covered by E2E.LocalBackupRestoreTests.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S22_BackupCardRunningState : ScenarioBase
{
    protected override string ScenarioName => "S22-backup-card-running";

    [AvaloniaFact] public async Task Run_S22() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("d") };
        var backup = new Backup
        {
            Name = "RunningBackup",
            SourcePaths = { ctx.MakeSource("src", seed: 1, targetBytes: 100_000) },
            LastRunStatus = BackupRunStatus.Running,
            LastRunSummary = "Running…",
            LastRunEndUtc = null,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var listVm = new BackupsViewModel();
        // Force the card into a running state — mirrors what
        // OnRunStarted does at runtime.
        if (listVm.Cards.Count > 0) listVm.Cards[0].SetRunning();

        var view = new BackupsView { DataContext = listVm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "card running blue-badge stop-button",
            "Running state — blue circular badge, inline 'Running…' indicator, Stop button.");
        host.Close();
        return Task.CompletedTask;
    }
}
