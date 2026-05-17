using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.E2E;

/// <summary>
/// End-to-end smoke of the multi-source fan-out. One "backup" with
/// three sources goes to one local destination; we assert three
/// distinct Duplicacy repos got created (one per source), three
/// CellOutcome entries land on the RunRecord, each source restores
/// back independently, and the per-source trees hash-match.
///
/// This is the feature-exists proof. If someone breaks the outer
/// source loop in BackupOrchestrator, this test explodes with a clear
/// "expected 3 cells, got 1" signal.
/// </summary>
public class MultiSourceBackupRestoreTests
{
    private readonly ITestOutputHelper _out;
    public MultiSourceBackupRestoreTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ThreeSources_OneDestination_AllBackedUp_AllRestoredIndependently()
    {
        ResetConfig();
        using var ws = new TempWorkspace("e2e-multisource");
        var sourceA = ws.Sub("src-a");
        var sourceB = ws.Sub("src-b");
        var sourceC = ws.Sub("src-c");
        var storage = ws.Sub("storage");

        // Give each source a distinct seed so the content across sources
        // is genuinely different — catches a "reused the first source
        // for every backup" regression.
        var summaryA = SyntheticFileTree.Generate(sourceA, seed: 101, targetBytes: 500_000);
        var summaryB = SyntheticFileTree.Generate(sourceB, seed: 202, targetBytes: 500_000);
        var summaryC = SyntheticFileTree.Generate(sourceC, seed: 303, targetBytes: 500_000);
        _out.WriteLine($"src-a: {summaryA}");
        _out.WriteLine($"src-b: {summaryB}");
        _out.WriteLine($"src-c: {summaryC}");

        var hashA = DirectoryHash.Compute(sourceA);
        var hashB = DirectoryHash.Compute(sourceB);
        var hashC = DirectoryHash.Compute(sourceC);

        var destination = new Destination
        {
            Name = "multi-target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storage,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "multi",
            SourcePaths = { sourceA, sourceB, sourceC },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            AbortOnMeteredNetwork = false,
            UseVss = false,
            Threads = 2,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        // 1) Single orchestrator run backs up all three sources.
        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        _out.WriteLine($"Run: status={record.Status} cells={record.Cells.Count}");
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Multi-source run ended {record.Status}: {string.Join(" | ", record.Errors)}");
        Assert.Equal(3, record.Cells.Count);
        Assert.All(record.Cells, c =>
            Assert.True(c.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
                $"Cell {c.SourcePath} ended {c.Status}: {c.LastError}"));

        // 2) Each source has its own repo on disk — no one-repo-holds-all fallback.
        var repoRoot = AppPaths.RepoDirForBackup(backup.Name);
        var subRepos = Directory.EnumerateDirectories(repoRoot).Select(Path.GetFileName).ToList();
        Assert.Equal(3, subRepos.Count);

        // 3) Restore each source independently and hash-compare.
        foreach (var (src, expectedHash) in new[] { (sourceA, hashA), (sourceB, hashB), (sourceC, hashC) })
        {
            var restoreTarget = ws.Sub($"restored-{Path.GetFileName(src)}");
            var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
                backup, src, destination, "default", CancellationToken.None);
            Assert.NotEmpty(revisions);

            var files = await ServiceLocator.Revisions.ListFilesAsync(
                backup, src, destination, "default", revisions[0].Number, CancellationToken.None);
            Assert.NotEmpty(files);

            var req = new RestoreRequest
            {
                Backup = backup,
                SourcePath = src,
                Destination = destination,
                StorageName = "default",
                Revision = revisions[0].Number,
                Files = files.Select(f => f.Path).ToList(),
                TargetPath = restoreTarget,
                Overwrite = true,
                PreserveStructure = true,
                Threads = 2,
                RetryDelaysSeconds = new[] { 1, 2, 5 },
            };
            var outcome = await ServiceLocator.Restore.RunAsync(req, CancellationToken.None);
            var failed = outcome.Files.Values.Where(f => f.Status == RestoreFileStatus.Failed).ToList();
            Assert.Empty(failed);

            Assert.Equal(expectedHash, DirectoryHash.Compute(restoreTarget));
        }
    }

    /// <summary>
    /// Orchestrator should keep going when one cell's destination is
    /// misconfigured. We wire two sources, a healthy destination, and
    /// a broken one (nonexistent path). Expected result: 2 Success cells
    /// for the healthy destination × 2 sources; 2 Failed cells for the
    /// broken destination. Overall run Status = Failed (there were
    /// failures) but the healthy half still ran.
    /// </summary>
    [Fact]
    public async Task CellFailure_DoesNotCancelOtherCells()
    {
        ResetConfig();
        using var ws = new TempWorkspace("e2e-cell-iso");
        var sourceA = ws.Sub("src-a");
        var sourceB = ws.Sub("src-b");
        var healthyStorage = ws.Sub("healthy");

        SyntheticFileTree.Generate(sourceA, seed: 11, targetBytes: 200_000);
        SyntheticFileTree.Generate(sourceB, seed: 22, targetBytes: 200_000);

        var healthy = new Destination
        {
            Name = "healthy",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = healthyStorage,
            Encrypted = false,
        };
        var broken = new Destination
        {
            Name = "broken",
            Kind = DestinationKind.S3Compatible,
            // A plausible-but-wrong S3 config. Duplicacy will fail to
            // init the storage quickly enough that cell retries exhaust
            // in a reasonable test budget.
            S3Endpoint = "s3.example.invalid",
            S3Region = "us-east-1",
            S3Bucket = "does-not-exist",
            PathOrSubpath = "none",
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "cell_iso",
            SourcePaths = { sourceA, sourceB },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            AbortOnMeteredNetwork = false,
            UseVss = false,
            Threads = 1,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = healthy.Id, StorageName = "healthy" });
        backup.Targets.Add(new BackupTarget { DestinationId = broken.Id,  StorageName = "broken"  });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(healthy);
            cfg.Destinations.Add(broken);
            cfg.Backups.Add(backup);
        });

        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);

        // 4 cells total: 2 sources × 2 destinations.
        Assert.Equal(4, record.Cells.Count);

        var healthyCells = record.Cells.Where(c => c.DestinationName == "healthy").ToList();
        var brokenCells  = record.Cells.Where(c => c.DestinationName == "broken").ToList();

        Assert.Equal(2, healthyCells.Count);
        Assert.Equal(2, brokenCells.Count);

        // The healthy cells should have Succeeded (or Warning) even
        // though their sibling cells failed.
        Assert.All(healthyCells, c =>
            Assert.True(c.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
                $"Healthy cell {c.SourcePath} shouldn't have failed; got {c.Status}"));

        // The broken cells should have exhausted retries.
        Assert.All(brokenCells, c =>
        {
            Assert.Equal(BackupRunStatus.Failed, c.Status);
            Assert.True(c.AttemptsMade >= 1,
                $"Broken cell should have attempted at least once; got {c.AttemptsMade}");
        });

        // Overall status reflects the failure class.
        Assert.Equal(BackupRunStatus.Failed, record.Status);
    }

    private static void ResetConfig()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
