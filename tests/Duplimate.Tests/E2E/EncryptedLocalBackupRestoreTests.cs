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
/// Same shape as LocalBackupRestoreTests but with storage encryption on.
/// Proves the full password-bearing pipeline: secrets-store round-trip,
/// DUPLICACY_PASSWORD env-var plumbing through DuplicacyRunner, init -encrypt,
/// restore reading back the encrypted chunks.
///
/// Why this test matters separately: encrypted storage is the *default*
/// for all real cloud destinations (StoragePasswordRef auto-generated on
/// save in the Destination editor). If the password-plumbing breaks,
/// every cloud backup silently fails — not just this test.
/// </summary>
public class EncryptedLocalBackupRestoreTests
{
    private readonly ITestOutputHelper _out;
    public EncryptedLocalBackupRestoreTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task EncryptedLocal_Backup_Then_Restore_ContentMatches()
    {
        ResetConfig();

        using var ws = new TempWorkspace("e2e-encrypted");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");
        var restoreDir = ws.Sub("restored");

        var summary = SyntheticFileTree.Generate(sourceDir, seed: 2024, targetBytes: 2_000_000);
        _out.WriteLine($"Source: {summary}");
        var sourceHash = DirectoryHash.Compute(sourceDir);

        // Stash the storage password in SecretsStore the same way the
        // Destination editor's Save() path does, then reference it via
        // StoragePasswordRef. Exercises the real secrets round-trip.
        const string storagePassword = "correct-horse-battery-staple-2024";
        var passwordRef = $"test-storage-{Guid.NewGuid():N}";
        ServiceLocator.Secrets.Set(passwordRef, storagePassword);

        var destination = new Destination
        {
            Name = "encrypted-local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = true,
            StoragePasswordRef = passwordRef,
        };
        var backup = new Backup
        {
            Name = "e2e_encrypted",
            SourcePaths = { sourceDir },
            PruneAfterBackup = false,
            CheckAfterBackup = false,
            AbortOnMeteredNetwork = false,
            Threads = 2,
            UseVss = false,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        var record = await ServiceLocator.Orchestrator
            .RunBackupAsync(backup, CancellationToken.None);
        _out.WriteLine($"Backup status: {record.Status}. Summary: {record.Summary}");
        Assert.True(
            record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Encrypted backup ended {record.Status}: {string.Join(" | ", record.Errors)}");

        // Critical sanity-check: on disk, the chunk files shouldn't contain
        // the source tree's text in plain — if they do, encryption didn't
        // actually apply. Pick one word that's guaranteed to appear in the
        // lorem-ipsum-generated text files and grep every chunk.
        AssertStorageIsEncrypted(storageDir, probeWord: "lorem");

        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourceDir, destination, "default", CancellationToken.None);
        Assert.NotEmpty(revisions);

        var files = await ServiceLocator.Revisions.ListFilesAsync(
            backup, sourceDir, destination, "default", revisions[0].Number, CancellationToken.None);
        Assert.NotEmpty(files);

        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = sourceDir,
            Destination = destination,
            StorageName = "default",
            Revision = revisions[0].Number,
            Files = files.Select(f => f.Path).ToList(),
            TargetPath = restoreDir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2, 5 },
        };
        var outcome = await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);

        var failed = outcome.Files.Values.Where(f => f.Status == RestoreFileStatus.Failed).ToList();
        Assert.Empty(failed);

        var restoredHash = DirectoryHash.Compute(restoreDir);
        Assert.Equal(sourceHash, restoredHash);
    }

    /// <summary>
    /// Smoke-checks that the stored chunks are actually encrypted by
    /// scanning every file under the storage tree for a plaintext probe
    /// word that definitely appears in the source data. If we find it,
    /// the -encrypt flag didn't take effect.
    /// </summary>
    private void AssertStorageIsEncrypted(string storageDir, string probeWord)
    {
        var probe = System.Text.Encoding.UTF8.GetBytes(probeWord);
        int filesChecked = 0;
        foreach (var file in Directory.EnumerateFiles(storageDir, "*", SearchOption.AllDirectories))
        {
            // Skip tiny metadata files — the concern is chunk contents.
            var info = new FileInfo(file);
            if (info.Length < 1024) continue;

            var bytes = File.ReadAllBytes(file);
            if (IndexOf(bytes, probe) >= 0)
            {
                Assert.Fail($"Plaintext '{probeWord}' found inside encrypted storage file {file} — " +
                            $"encryption didn't apply. Check that -encrypt was passed to duplicacy init and " +
                            $"DUPLICACY_PASSWORD was read by duplicacy.");
            }
            filesChecked++;
        }
        _out.WriteLine($"Encryption probe OK — scanned {filesChecked} chunk file(s), no plaintext '{probeWord}' found.");
        Assert.True(filesChecked > 0, "Expected at least one chunk file in the encrypted storage tree.");
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
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
