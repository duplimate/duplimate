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
/// One source folder → one local destination. The source content is
/// synthetic (deterministic seed, ~2 MB) so the scenario is fast and
/// reproducible. The flow:
///
///   1. Generate a source tree, pick a local-folder destination dir.
///   2. Seed the config with the resulting Backup + Destination.
///   3. Snapshot the populated Backups card.
///   4. Stamp a successful LastRun on the backup model and re-render
///      so we have a "what does the green-status pill look like
///      after the first run" reference.
///
/// We don't actually shell out to duplicacy.exe here — that path is
/// covered by <see cref="E2E.LocalBackupRestoreTests"/>. The scenario
/// adds VISUAL coverage on top of those run-correctness tests.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S02_SimpleBackupOneSourceOneLocal : ScenarioBase
{
    protected override string ScenarioName => "S02-simple-backup-one-local";

    [AvaloniaFact] public async Task Run_S02() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var sourcePath = ctx.MakeSource("documents", seed: 42, targetBytes: 1_500_000);
        var destPath   = ctx.MakeLocalDestination("local-backup-dir");

        var dest = new Destination
        {
            Name = "Local — backup-dir",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "Documents",
            SourcePaths = { sourcePath },
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Driver.Pump();
        ctx.Snapshot(host, "card idle never-run",
            "Card with idle (grey) status badge — backup configured but never run.");

        // Stamp a successful run timestamp so the card flips to the
        // ok state (green badge). Tests the new tinted-circle badge
        // recently added on top of the status-icon system.
        ServiceLocator.Config.Update(cfg =>
        {
            var b = cfg.Backups[0];
            b.LastRunStatus = BackupRunStatus.Success;
            b.LastRunSummary = "rev 1 · in 4s";
            b.LastRunEndUtc = DateTime.UtcNow.AddMinutes(-2);
        });
        ctx.Driver.Pump();
        ctx.Snapshot(host, "card ok green-badge",
            "After a successful run: green status badge + 'Last run' label.");

        // Stamp a Failed status to capture the red-badge variant.
        ServiceLocator.Config.Update(cfg =>
        {
            var b = cfg.Backups[0];
            b.LastRunStatus = BackupRunStatus.Failed;
            b.LastRunSummary = "Backup exited with code 100";
            b.LastRunEndUtc = DateTime.UtcNow.AddMinutes(-1);
        });
        ctx.Driver.Pump();
        ctx.Snapshot(host, "card failed red-badge",
            "Failed run: red status badge + health banner with attention pill.");

        // And a Skipped variant for the orange-badge.
        ServiceLocator.Config.Update(cfg =>
        {
            var b = cfg.Backups[0];
            b.LastRunStatus = BackupRunStatus.Skipped;
            b.LastRunSummary = "Manually skipped after 2s";
        });
        ctx.Driver.Pump();
        ctx.Snapshot(host, "card skipped orange-badge",
            "Skipped run: orange status badge.");

        host.Close();
        return Task.CompletedTask;
    }
}
