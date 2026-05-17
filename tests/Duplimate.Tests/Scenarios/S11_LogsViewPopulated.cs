using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Logs &amp; runs view with a backup selected and a populated
/// history of past runs. The terminal pane renders the loaded
/// run's log file — we materialise a fake one on disk under the
/// LogStore root so the file-load path actually fires (covers
/// the recent flicker fix + the new log-loading wiring).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S11_LogsViewPopulated : ScenarioBase
{
    protected override string ScenarioName => "S11-logs-view-populated";

    [AvaloniaFact] public async Task Run_S11() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "Local — backup-dir",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("seed"),
        };
        var backup = new Backup
        {
            Name = "Documents",
            SourcePaths = { ctx.MakeSource("docs", seed: 1, targetBytes: 200_000) },
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "rev 12 · in 4s",
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        // Build a synthetic per-run log file + persist a RunRecord
        // pointing at it. The LogsView's "Selected run" pane reads
        // this off disk via the LogPath.
        var logPath = Path.Combine(ctx.RootDir, "fake-run.log");
        File.WriteAllText(logPath,
            "2026-04-30 14:00:00 INFO RUN_START Documents → Local — backup-dir\n" +
            "2026-04-30 14:00:01 INFO INDEX_FILES Walking 1,234 files\n" +
            "2026-04-30 14:00:03 INFO UPLOAD_CHUNK 512 KiB → chunk a1b2c3\n" +
            "2026-04-30 14:00:04 INFO SNAPSHOT_INFO revision 12 created\n" +
            "2026-04-30 14:00:04 INFO RUN_END Success — in 4s\n");
        var record = new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            StartedUtc = DateTime.UtcNow.AddHours(-1).AddSeconds(-4),
            EndedUtc = DateTime.UtcNow.AddHours(-1),
            Status = BackupRunStatus.Success,
            Summary = "rev 12 · in 4s",
            LogPath = logPath,
        };
        ServiceLocator.Logs.AppendRun(record);

        var vm = new LogsViewModel();
        vm.SelectedBackup = backup.Name;
        vm.SelectedRun = record;
        var view = new LogsView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Driver.Pump();
        ctx.Snapshot(host, "logs populated past-run",
            "Backup picked, past run selected, terminal renders log file content.");

        // Switch to live mode and feed a few synthetic lines so
        // we cover the LIVE pill + flicker-free terminal path.
        vm.SelectedRun = null;
        ServiceLocator.Runner.RaiseLineWritten("2026-05-01 12:00:00 INFO LIVE_LINE running…");
        ServiceLocator.Runner.RaiseLineWritten("2026-05-01 12:00:01 INFO UPLOAD_CHUNK 1.2 MiB → chunk 7f8e2a");
        ServiceLocator.Runner.RaiseLineWritten("2026-05-01 12:00:02 INFO SNAPSHOT_INFO revision 13 created");
        await ctx.Driver.PumpAsync(220);  // wait for the 150ms debounce flush
        ctx.Snapshot(host, "logs live tail with output",
            "LIVE pill visible, terminal pane shows streaming lines.");

        host.Close();
    }
}
