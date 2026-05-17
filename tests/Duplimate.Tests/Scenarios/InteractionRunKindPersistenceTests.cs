using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Regression coverage for the new <see cref="RunKind"/>-tagged
/// RunRecord persistence. The user explicitly asked: "Restore logs
/// should show up in Activity &amp; Logs … There should be a
/// dedicated entry in RUN" — which means after a real test-restore
/// drill, the per-backup history.json must contain a record with
/// <c>Kind = TestRestore</c>, populated <c>LogPath</c>, and a
/// terminal status that matches the verifier outcome. Without this
/// test, a future "we forgot to persist the kind tag" regression
/// would silently drop restore + test-restore entries from the
/// LogsView dropdown again.
/// </summary>
public class InteractionRunKindPersistenceTests
{
    private readonly ITestOutputHelper _out;
    public InteractionRunKindPersistenceTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public async Task TestRestore_AfterSuccessfulBackup_PersistsRunRecordKind()
    {
        ResetConfig();
        using var ws = new TempWorkspace("runkind-test");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");
        SyntheticFileTree.Generate(sourceDir, seed: 31, targetBytes: 800_000);

        var dest = new Destination
        {
            Name = "Local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "RunKindTest",
            SourcePaths = { sourceDir },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            UseVss = false,
            Threads = 1,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        // Run a real backup so the verifier has revisions to drill.
        var backupRec = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Success, backupRec.Status);

        // Run the test-restore drill — this is the path that should
        // append a Kind=TestRestore RunRecord.
        var verifyOutcome = await ServiceLocator.Verifier.VerifyAsync(backup, CancellationToken.None);
        _out.WriteLine($"Verify summary: {verifyOutcome.Summary}");

        // Read the persisted history. It should contain BOTH a
        // backup record AND a test-restore record.
        var history = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id);
        Assert.NotEmpty(history);
        var backupRow = history.FirstOrDefault(r => r.Kind == RunKind.Backup);
        var testRow   = history.FirstOrDefault(r => r.Kind == RunKind.TestRestore);

        Assert.NotNull(backupRow);
        Assert.NotNull(testRow);
        // Test-restore must have a populated LogPath that points
        // at a file that exists. Without it the LogsView's
        // "Selected run" pane renders empty for test-restore rows.
        Assert.False(string.IsNullOrEmpty(testRow!.LogPath),
            "Test-restore RunRecord.LogPath was empty — user would see a blank terminal when picking it.");
        Assert.True(File.Exists(testRow.LogPath),
            $"Test-restore log file '{testRow.LogPath}' is missing on disk.");

        // Status sanity: a passing drill maps to Success.
        Assert.True(testRow.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Expected Success/Warning for the verified backup but got {testRow.Status}.");
    }

    private static void ResetConfig() =>
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
}
