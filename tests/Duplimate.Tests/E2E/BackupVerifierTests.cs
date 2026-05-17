using System;
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
/// Round-trip drill: generate a synthetic source tree, back it up,
/// then run BackupVerifier and assert that the drill picks files,
/// restores them successfully, and byte-matches each one against the
/// (unchanged) source. This is the same scenario the user runs from
/// the Dashboard "Test restore" button — the goal is end-to-end
/// confidence that a "backup succeeded" claim is actually restorable.
///
/// We force a small source so the verify drill picks all available
/// files when the pool is &lt; <see cref="BackupVerifier.FilesToSample"/>;
/// the assertion is on per-file outcomes rather than counts.
/// </summary>
public class BackupVerifierTests
{
    private readonly ITestOutputHelper _out;
    public BackupVerifierTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task VerifyAsync_freshLocalBackup_allFilesByteIdentical()
    {
        ResetConfig();

        using var ws = new TempWorkspace("e2e-verify");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");

        var summary = SyntheticFileTree.Generate(sourceDir, seed: 42, targetBytes: 800_000);
        _out.WriteLine($"Generated source: {summary}");

        var destination = new Destination
        {
            Name = "verify-test",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "verify_roundtrip",
            SourcePaths = { sourceDir },
            FiltersText = "",
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            AbortOnMeteredNetwork = false,
            Enabled = true,
            Threads = 2,
            UseVss = false,
            Targets = { new BackupTarget { DestinationId = "", StorageName = "default" } },
        };
        backup.Targets[0].DestinationId = destination.Id;

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        // 1) Run the backup so the verifier has something to read back.
        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.True(
            record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended with status {record.Status}. Errors: " +
            string.Join(" | ", record.Errors));

        // 2) Run the verify drill.
        var outcome = await ServiceLocator.Verifier.VerifyAsync(backup, CancellationToken.None);

        _out.WriteLine($"Verify summary: {outcome.Summary}");
        foreach (var f in outcome.Files)
            _out.WriteLine($"  {f.Status,-15} {f.Path}{(f.Detail is null ? "" : "  (" + f.Detail + ")")}");

        // 3) Per-file assertions. Every file should have either matched
        //    bytes (OK) or matched size with a justified "skip compare"
        //    reason. SizeMismatch / BytesMismatch / RestoreFailed are
        //    the failure shapes we care about catching.
        Assert.NotEmpty(outcome.Files);
        Assert.Null(outcome.FatalError);
        Assert.True(outcome.OverallPass,
            $"Verify failed: {outcome.Summary}; per-file = " +
            string.Join(", ", outcome.Files.Select(f => $"{f.Path}:{f.Status}")));

        // Source files weren't modified between backup and verify, so
        // we expect actual byte comparison to have happened (not just
        // size match). At least one file should be OK; OkNoCompare is
        // legal but should be the exception, not the rule, on this
        // synthetic tree.
        Assert.Contains(outcome.Files, f => f.Status == FileVerifyStatus.OK);
        Assert.DoesNotContain(outcome.Files, f =>
            f.Status is FileVerifyStatus.SizeMismatch
                     or FileVerifyStatus.BytesMismatch
                     or FileVerifyStatus.RestoreFailed);

        // 4) PersistOutcome writes back to config — Dashboard reads from there.
        ServiceLocator.Verifier.PersistOutcome(outcome);
        var refreshed = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.True(refreshed.LastVerifyPass);
        Assert.NotNull(refreshed.LastVerifyUtc);
        Assert.Equal(outcome.Summary, refreshed.LastVerifySummary);
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
