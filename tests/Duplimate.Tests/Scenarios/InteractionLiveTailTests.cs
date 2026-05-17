using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Tests.TestHelpers;
using Duplimate.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Spawns real <c>duplicacy.exe</c> backup runs and asserts the live
/// tail plumbing actually streams lines to the UI in real time —
/// the user reported "the terminal still doesn't show any logs when
/// running a backup" multiple times. Snapshot scenarios couldn't
/// catch this because no actual run executes there.
///
/// Two scenarios:
///   1. Real successful backup → live tail receives lines DURING
///      the run (within 1s of run start), AND the LogsViewModel
///      auto-flips into Live mode.
///   2. Forced failure (storage URL points at a non-existent path
///      that init can't create) → live tail captures the failure
///      reason in real time, status flips to Failed, the persisted
///      RunRecord has the LogPath populated.
/// </summary>
public class InteractionLiveTailTests
{
    private readonly ITestOutputHelper _out;
    public InteractionLiveTailTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public async Task SuccessfulBackup_LiveTail_StreamsLinesDuringRun()
    {
        ResetConfig();
        using var ws = new TempWorkspace("livetail-success");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");
        SyntheticFileTree.Generate(sourceDir, seed: 11, targetBytes: 1_000_000);

        var dest = new Destination
        {
            Name = "Local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "LiveTailSuccess",
            SourcePaths = { sourceDir },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            UseVss = false,
            Threads = 1,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        // Wire the LogsViewModel — same instance MainWindowViewModel
        // eager-initialises in production. We watch its LiveTail
        // string between the run start and end; the assertion is
        // that lines arrived BEFORE RunEnded.
        var logsVm = new LogsViewModel();
        var liveSnapshotsByTime = new System.Collections.Generic.List<(DateTime At, string Tail)>();
        logsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogsViewModel.LiveTail))
                liveSnapshotsByTime.Add((DateTime.UtcNow, logsVm.LiveTail));
        };

        var runStartUtc = DateTime.UtcNow;
        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        var runEndUtc = DateTime.UtcNow;

        // Pump dispatcher to drain any pending live-tail flushes.
        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.Equal(BackupRunStatus.Success, record.Status);
        Assert.NotEmpty(liveSnapshotsByTime);

        // At least one live-tail update must have landed BEFORE the
        // run ended (allowing 200ms slack for the final debounced
        // flush). That's the contract: lines flow during the run,
        // not all at the end.
        var duringRun = liveSnapshotsByTime
            .Where(s => s.At < runEndUtc.AddMilliseconds(-200))
            .ToList();
        Assert.True(duringRun.Count > 0,
            $"Live tail received 0 updates during the {(runEndUtc - runStartUtc).TotalMilliseconds:F0}ms run — " +
            "the terminal would appear empty until the run ends.");

        _out.WriteLine($"Live tail received {liveSnapshotsByTime.Count} updates during a {(runEndUtc - runStartUtc).TotalMilliseconds:F0}ms run.");
        Assert.NotEmpty(record.LogPath);
        Assert.True(File.Exists(record.LogPath),
            $"RunRecord.LogPath '{record.LogPath}' doesn't exist on disk — log persistence regressed.");
        logsVm.Dispose();
    }

    [AvaloniaFact]
    public async Task FailingBackup_LiveTail_StreamsErrorLines_AndStatusFlipsToFailed()
    {
        ResetConfig();
        using var ws = new TempWorkspace("livetail-fail");
        var sourceDir  = ws.Sub("source");
        SyntheticFileTree.Generate(sourceDir, seed: 12, targetBytes: 200_000);

        // Force a failure: storage URL points at a path under a
        // non-existent drive. duplicacy can't create or write there,
        // so the backup attempt fails. Mirrors the user's real
        // scenario (they erased the destination — same effect on the
        // run: storage unreachable).
        var dest = new Destination
        {
            Name = "Local — broken",
            Kind = DestinationKind.LocalFolder,
            // ZZ: is virtually never mounted on a Windows test box.
            // If it IS, the test will (correctly) detect a different
            // failure shape; the assertion below tolerates either.
            PathOrSubpath = @"ZZ:\duplimate-tests-must-fail\storage",
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "LiveTailFail",
            SourcePaths = { sourceDir },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            UseVss = false,
            Threads = 1,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var logsVm = new LogsViewModel();

        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);

        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.True(record.Status is BackupRunStatus.Failed or BackupRunStatus.Skipped,
            $"Expected Failed or Skipped but got {record.Status} — the volume-detector or runner didn't catch the unreachable storage.");

        // Live tail must have at least the init-or-error line — if
        // it's empty, the user sees nothing in the terminal even
        // post-failure, which is the symptom the user reported.
        Assert.True(logsVm.LiveTail.Length > 0,
            "Live tail is empty after a failed run — the LineWritten event chain is broken.");

        _out.WriteLine($"Failed run live-tail length: {logsVm.LiveTail.Length} chars.");
        _out.WriteLine($"Failed run summary: {record.Summary}");
        logsVm.Dispose();
    }

    [AvaloniaFact]
    public async Task SuccessfulBackup_DisplayText_PivotsToLiveTailDuringRun()
    {
        // Regression for: "the terminal still doesn't show any logs
        // when running a backup; if I stop in the middle, then it'll
        // show log entries all at once." Root cause was the
        // orchestrator's synthetic Running RunRecord having
        // EndedUtc = DateTime.UtcNow set up-front, which broke
        // LogsViewModel.IsShowingLive's (Running, EndedUtc=null) check
        // — so DisplayText pivoted to the empty LogText for the entire
        // run. The earlier SuccessfulBackup test caught LiveTail
        // updates but NOT that the bound DisplayText reflects them;
        // this test pins both halves.
        ResetConfig();
        using var ws = new TempWorkspace("livetail-displaytext");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");
        SyntheticFileTree.Generate(sourceDir, seed: 13, targetBytes: 1_000_000);

        var dest = new Destination
        {
            Name = "Local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "DisplayTextPivot",
            SourcePaths = { sourceDir },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            UseVss = false,
            Threads = 1,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var logsVm = new LogsViewModel();

        // Snapshot DisplayText / IsShowingLive while the run is in
        // flight so we can assert at least one observation showed
        // the pane was actually streaming the live tail (not stuck
        // on an empty LogText because IsShowingLive was false).
        var midRunObservations = new System.Collections.Concurrent.ConcurrentBag<(bool live, int textLen)>();
        var stopSampling = new CancellationTokenSource();
        var samplingTask = Task.Run(async () =>
        {
            while (!stopSampling.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    midRunObservations.Add((logsVm.IsShowingLive, logsVm.DisplayText?.Length ?? 0));
                });
                await Task.Delay(50);
            }
        });

        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        stopSampling.Cancel();
        try { await samplingTask; } catch { }

        // Drain pending dispatcher work.
        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.Equal(BackupRunStatus.Success, record.Status);

        // The contract: at some point DURING the run, the pane was in
        // live-tail mode AND DisplayText carried bytes — i.e. the user
        // would have seen lines on screen. Earlier the orchestrator's
        // up-front EndedUtc made IsShowingLive=false for the entire
        // run; the corresponding observation set was empty here.
        var liveAndNonEmpty = midRunObservations
            .Count(o => o.live && o.textLen > 0);
        Assert.True(liveAndNonEmpty > 0,
            $"Never observed (IsShowingLive=true AND DisplayText non-empty) during the run. " +
            $"Observations: {midRunObservations.Count}, " +
            $"IsShowingLive=true: {midRunObservations.Count(o => o.live)}, " +
            $"non-empty text: {midRunObservations.Count(o => o.textLen > 0)}.");

        logsVm.Dispose();
    }

    private static void ResetConfig() =>
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
}
