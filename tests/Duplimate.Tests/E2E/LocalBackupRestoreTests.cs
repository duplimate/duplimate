using System;
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
/// End-to-end smoke of the full backup -> restore loop with a local
/// (filesystem) destination. Generates a procedural source tree, backs it
/// up, restores every file into a scratch target, and hashes the tree
/// before/after to prove byte equality.
///
/// This exercises: DuplicacyEmbedder, DuplicacyRunner (init/backup/restore),
/// BackupOrchestrator, RevisionBrowser, RestoreEngine, the filter machinery
/// (here with an empty filter set), and the config/secrets pipeline.
/// </summary>
public class LocalBackupRestoreTests
{
    private readonly ITestOutputHelper _out;

    public LocalBackupRestoreTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Local_Backup_Then_Restore_ContentMatches()
    {
        // NB: ServiceLocator was bootstrapped by the headless app fixture
        // pointing at the suite-wide temp config root. Per-test isolation
        // is provided by TempWorkspace pointing source/dest/restore into
        // their own subdirectories AND by clearing Config between tests.
        ResetConfig();

        using var ws = new TempWorkspace("e2e-local");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");   // where duplicacy writes chunks
        var restoreDir = ws.Sub("restored");  // where we pull back

        const int seed = 1337;
        const long targetBytes = 3_000_000; // ~3 MB, big enough for multiple chunks

        var summary = SyntheticFileTree.Generate(sourceDir, seed, targetBytes);
        _out.WriteLine($"Generated source: {summary}");
        var sourceHash = DirectoryHash.Compute(sourceDir);

        var destination = new Destination
        {
            Name = "local-test",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false, // no password dance in the test
        };
        var backup = new Backup
        {
            Name = "e2e_roundtrip",
            SourcePaths = { sourceDir },
            FiltersText = "",
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            AbortOnMeteredNetwork = false,
            Enabled = true,
            Threads = 2,
            UseVss = false, // VSS needs admin; not required for a folder-source test
            Targets = { new BackupTarget { DestinationId = "", StorageName = "default" } },
        };
        backup.Targets[0].DestinationId = destination.Id;

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        // 1) Run the backup through the orchestrator — same path "Run now" uses.
        var record = await ServiceLocator.Orchestrator
            .RunBackupAsync(backup, CancellationToken.None);

        _out.WriteLine($"Run status: {record.Status}, summary: {record.Summary}");
        Assert.True(
            record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended with status {record.Status}. Errors: " +
            string.Join(" | ", record.Errors) + ". Summary: " + record.Summary);

        // 2) List revisions, pick the one we just created.
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourceDir, destination, "default", CancellationToken.None);
        Assert.NotEmpty(revisions);
        var latest = revisions[0].Number;
        _out.WriteLine($"Latest revision: #{latest}");

        // 3) Enumerate all files the revision contains.
        var files = await ServiceLocator.Revisions.ListFilesAsync(
            backup, sourceDir, destination, "default", latest, CancellationToken.None);
        Assert.NotEmpty(files);
        var filePaths = files.Select(f => f.Path).ToList();
        _out.WriteLine($"Revision contains {filePaths.Count} file(s)");

        // 4) Restore every file into the fresh target directory.
        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = sourceDir,
            Destination = destination,
            StorageName = "default",
            Revision = latest,
            Files = filePaths,
            TargetPath = restoreDir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2, 5 },
        };
        var outcome = await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);

        var restoredCount = outcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Restored);
        var failedCount   = outcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Failed);
        _out.WriteLine($"Restore outcome: {restoredCount} OK, {failedCount} failed");
        Assert.Equal(0, failedCount);
        Assert.Equal(filePaths.Count, restoredCount);

        // 5) Byte-for-byte equivalence of the restored tree vs. the source.
        var restoredHash = DirectoryHash.Compute(restoreDir);
        if (sourceHash != restoredHash)
        {
            _out.WriteLine("---- SOURCE TREE ----");
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).Take(50))
                _out.WriteLine($"  {Path.GetRelativePath(sourceDir, f)} ({new FileInfo(f).Length}B)");
            _out.WriteLine("---- RESTORED TREE ----");
            foreach (var f in Directory.EnumerateFiles(restoreDir, "*", SearchOption.AllDirectories).Take(50))
                _out.WriteLine($"  {Path.GetRelativePath(restoreDir, f)} ({new FileInfo(f).Length}B)");
        }
        Assert.Equal(sourceHash, restoredHash);
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
