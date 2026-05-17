using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.E2E;

/// <summary>
/// V1 trust-test soak. Runs the full lifecycle the user would run before
/// pointing Duplimate at their real Documents folder:
///
///   1. Create a folder with realistic mixed content (text files of
///      varying sizes, nested directories — not just random bytes).
///   2. Run a backup to a local storage.
///   3. Modify one file, append a new one.
///   4. Run a SECOND backup so we have two revisions.
///   5. Run the verify drill against the latest revision.
///   6. Restore revision 1 (the OLDER snapshot) into a fresh folder.
///   7. Restore revision 2 into another fresh folder.
///   8. Byte-compare each restore against what the source looked like
///      at the moment of THAT snapshot — proves time travel works.
///
/// Artifacts are written under <c>%TEMP%\duplimate-v1-soak-&lt;UTC&gt;</c>
/// and intentionally NOT cleaned up — the test prints absolute paths
/// for every directory so you can browse the snapshot chunks, the
/// restored files, and the source after the test passes.
///
/// Run with:
///   dotnet test --filter FullyQualifiedName~V1SoakTest \
///     --logger "console;verbosity=detailed"
/// </summary>
public class V1SoakTest
{
    private readonly ITestOutputHelper _out;
    public V1SoakTest(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task EndToEnd_BackupModifyBackupVerifyRestore()
    {
        ResetConfig();

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var soakRoot = Path.Combine(Path.GetTempPath(), $"duplimate-v1-soak-{stamp}");
        Directory.CreateDirectory(soakRoot);

        var sourceDir       = Path.Combine(soakRoot, "source");
        var storageDir      = Path.Combine(soakRoot, "storage");
        var restoreRev1Dir  = Path.Combine(soakRoot, "restored-rev1");
        var restoreRev2Dir  = Path.Combine(soakRoot, "restored-rev2");
        Directory.CreateDirectory(sourceDir);

        Header($"V1 SOAK ROOT: {soakRoot}");
        _out.WriteLine($"  source     → {sourceDir}");
        _out.WriteLine($"  storage    → {storageDir}");
        _out.WriteLine($"  restore #1 → {restoreRev1Dir}");
        _out.WriteLine($"  restore #2 → {restoreRev2Dir}");
        _out.WriteLine("");

        // ---- 1) Realistic source tree (NOT just random bytes) ----------
        Header("STEP 1 — populate source with realistic content");
        SeedRealisticTree(sourceDir);
        var rev1Snapshot = SnapshotTree(sourceDir);
        _out.WriteLine($"Source has {rev1Snapshot.Count} files, {rev1Snapshot.Values.Sum(b => b.Length):N0} bytes total.");
        foreach (var (rel, bytes) in rev1Snapshot.Take(8))
            _out.WriteLine($"  {bytes.Length,9:N0} B  {rel}");
        if (rev1Snapshot.Count > 8) _out.WriteLine($"  ... +{rev1Snapshot.Count - 8} more");

        // ---- 2) Configure backup + run revision 1 ----------------------
        Header("STEP 2 — configure backup, run revision 1");
        var destination = new Destination
        {
            Name = "soak-local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false, // soak focuses on lifecycle, not encryption
        };
        var backup = new Backup
        {
            Name = "v1_soak",
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

        var rec1 = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        _out.WriteLine($"Run #1: status={rec1.Status} summary={rec1.Summary}");
        Assert.True(rec1.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup #1 failed: {string.Join(" | ", rec1.Errors)}");

        var storageFiles = CountAndSize(storageDir);
        _out.WriteLine($"Storage after #1: {storageFiles.count} files, {storageFiles.bytes:N0} bytes");

        // ---- 3) Modify the source between snapshots --------------------
        Header("STEP 3 — modify source (changed file + new file + deleted file)");
        File.AppendAllText(Path.Combine(sourceDir, "docs", "letter.txt"),
            "\n--- second draft, added in revision 2 ---\n");
        File.WriteAllText(Path.Combine(sourceDir, "newly-added.txt"),
            "This file did NOT exist at revision 1.\n");
        File.Delete(Path.Combine(sourceDir, "shopping-list.txt"));
        var rev2Snapshot = SnapshotTree(sourceDir);
        _out.WriteLine($"Source now has {rev2Snapshot.Count} files (rev 1 had {rev1Snapshot.Count}).");
        _out.WriteLine($"  modified: docs/letter.txt now {rev2Snapshot["docs/letter.txt"].Length:N0} B " +
            $"(was {rev1Snapshot["docs/letter.txt"].Length:N0} B)");
        _out.WriteLine($"  added:    newly-added.txt ({rev2Snapshot["newly-added.txt"].Length:N0} B)");
        _out.WriteLine($"  deleted:  shopping-list.txt");

        // ---- 4) Backup revision 2 --------------------------------------
        Header("STEP 4 — run revision 2 backup");
        var rec2 = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        _out.WriteLine($"Run #2: status={rec2.Status} summary={rec2.Summary}");
        Assert.True(rec2.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup #2 failed: {string.Join(" | ", rec2.Errors)}");
        var storageFiles2 = CountAndSize(storageDir);
        _out.WriteLine($"Storage after #2: {storageFiles2.count} files, {storageFiles2.bytes:N0} bytes " +
            $"(grew {storageFiles2.bytes - storageFiles.bytes:N0} B)");

        // List revisions to confirm there are two.
        var revs = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourceDir, destination, "default", CancellationToken.None);
        Assert.Equal(2, revs.Count);
        var rev1Number = revs.OrderBy(r => r.Number).First().Number;
        var rev2Number = revs.OrderBy(r => r.Number).Last().Number;
        _out.WriteLine($"Revision chain: #{rev1Number} (older) and #{rev2Number} (latest).");

        // ---- 5) Verify drill against the latest revision ---------------
        Header("STEP 5 — verify drill (the user's monthly trust-check)");
        var verify = await ServiceLocator.Verifier.VerifyAsync(backup, CancellationToken.None);
        _out.WriteLine($"Verify: pass={verify.OverallPass} summary={verify.Summary}");
        foreach (var f in verify.Files)
            _out.WriteLine($"  [{f.Status,-13}] {f.Path}  ({f.ActualBytes:N0} B)");
        Assert.True(verify.OverallPass, $"Verify failed: {verify.Summary}");
        Assert.Null(verify.FatalError);

        // ---- 6) Restore revision 1 (time travel) -----------------------
        Header($"STEP 6 — restore revision #{rev1Number} (the OLDER snapshot)");
        var rev1Files = await ServiceLocator.Revisions.ListFilesAsync(
            backup, sourceDir, destination, "default", rev1Number, CancellationToken.None);
        Directory.CreateDirectory(restoreRev1Dir);
        var rev1Outcome = await ServiceLocator.Restore.RunAsync(new RestoreRequest
        {
            Backup = backup,
            SourcePath = sourceDir,
            Destination = destination,
            StorageName = "default",
            Revision = rev1Number,
            Files = rev1Files.Select(f => f.Path).ToList(),
            TargetPath = restoreRev1Dir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2 },
        }, CancellationToken.None);
        var rev1Restored = rev1Outcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Restored);
        _out.WriteLine($"Restored {rev1Restored}/{rev1Files.Count} files from revision #{rev1Number}");
        Assert.Equal(rev1Files.Count, rev1Restored);

        // Byte-compare each rev-1-restored file against what the source
        // looked like at rev 1 (cached in rev1Snapshot before we mutated).
        Header($"STEP 6a — byte-compare restored rev #{rev1Number} vs original-at-rev-1");
        AssertTreeMatchesSnapshot(restoreRev1Dir, rev1Snapshot,
            label: $"rev{rev1Number}");

        // ---- 7) Restore revision 2 (latest) ----------------------------
        Header($"STEP 7 — restore revision #{rev2Number} (the LATEST snapshot)");
        var rev2Files = await ServiceLocator.Revisions.ListFilesAsync(
            backup, sourceDir, destination, "default", rev2Number, CancellationToken.None);
        Directory.CreateDirectory(restoreRev2Dir);
        var rev2Outcome = await ServiceLocator.Restore.RunAsync(new RestoreRequest
        {
            Backup = backup,
            SourcePath = sourceDir,
            Destination = destination,
            StorageName = "default",
            Revision = rev2Number,
            Files = rev2Files.Select(f => f.Path).ToList(),
            TargetPath = restoreRev2Dir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2 },
        }, CancellationToken.None);
        var rev2Restored = rev2Outcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Restored);
        _out.WriteLine($"Restored {rev2Restored}/{rev2Files.Count} files from revision #{rev2Number}");
        Assert.Equal(rev2Files.Count, rev2Restored);

        Header($"STEP 7a — byte-compare restored rev #{rev2Number} vs source-now");
        AssertTreeMatchesSnapshot(restoreRev2Dir, rev2Snapshot,
            label: $"rev{rev2Number}");

        // ---- 8) Cross-check: rev1 restore should NOT contain the
        //         changes that only exist in rev2 -----------------------
        Header("STEP 8 — cross-check time travel");
        var rev1Letter = File.ReadAllText(Path.Combine(restoreRev1Dir, "docs", "letter.txt"));
        Assert.DoesNotContain("second draft, added in revision 2", rev1Letter);
        Assert.False(File.Exists(Path.Combine(restoreRev1Dir, "newly-added.txt")),
            $"rev{rev1Number} restore should NOT have newly-added.txt — that file was added in rev{rev2Number}");
        Assert.True(File.Exists(Path.Combine(restoreRev1Dir, "shopping-list.txt")),
            $"rev{rev1Number} restore SHOULD have shopping-list.txt — it was deleted in rev{rev2Number}");
        _out.WriteLine($"Confirmed: rev #{rev1Number} restore has shopping-list.txt and lacks newly-added.txt.");
        _out.WriteLine($"Confirmed: rev #{rev1Number}'s letter.txt does NOT contain the rev #{rev2Number} addition.");

        // ---- 9) Summary -------------------------------------------------
        Header("V1 SOAK COMPLETE");
        _out.WriteLine("");
        _out.WriteLine("Inspect the artifacts:");
        _out.WriteLine($"  Source (current state)  : {sourceDir}");
        _out.WriteLine($"  Backup storage (chunks) : {storageDir}");
        _out.WriteLine($"  Restored revision #{rev1Number}   : {restoreRev1Dir}");
        _out.WriteLine($"  Restored revision #{rev2Number}   : {restoreRev2Dir}");
        _out.WriteLine("");
        _out.WriteLine("This proves end-to-end:");
        _out.WriteLine("  • A backup can be written to disk.");
        _out.WriteLine("  • Multiple revisions are kept independently.");
        _out.WriteLine("  • The verify drill works against the live snapshot.");
        _out.WriteLine("  • An older revision can be restored without picking up newer changes.");
        _out.WriteLine("  • Restored bytes match the original bytes at the time of that snapshot.");
    }

    // -------------------------------------------------------------------

    private void Header(string h) => _out.WriteLine($"\n=== {h} ===");

    /// <summary>
    /// Build a small but realistic source tree: prose text files of
    /// varying sizes, a nested folder, plus one tiny binary blob to
    /// catch chunker / hashing bugs that don't show on text alone.
    /// </summary>
    private static void SeedRealisticTree(string root)
    {
        var docs = Path.Combine(root, "docs");
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(docs);
        Directory.CreateDirectory(photos);

        File.WriteAllText(Path.Combine(root, "shopping-list.txt"),
            "milk\neggs\nbread\nthe weekly newspaper\n");
        File.WriteAllText(Path.Combine(root, "notes.md"),
            "# Notes\n\nA quick brown fox jumps over the lazy dog. 1234567890.\n\n" +
            string.Join("\n", Enumerable.Range(0, 50).Select(i => $"- item {i}: lorem ipsum dolor sit amet")));
        File.WriteAllText(Path.Combine(docs, "letter.txt"),
            "Dear future self,\n\nThis is what V1 of Duplimate looked like " +
            "the day we ran the trust-test.\n\nWith hope,\nThe past.\n");
        File.WriteAllText(Path.Combine(docs, "report.txt"),
            string.Join("\n", Enumerable.Range(0, 200)
                .Select(i => $"{i,4}: " + new string('x', 50 + (i * 7) % 80))));
        // Pseudo-binary content so chunker exercises a non-text path.
        var bin = new byte[12_345];
        new Random(7).NextBytes(bin);
        File.WriteAllBytes(Path.Combine(photos, "snapshot.bin"), bin);
    }

    /// <summary>
    /// Snapshot a directory tree as relative-path → bytes. Used to
    /// remember what the source looked like at moment X so we can
    /// byte-compare a restore against it later.
    ///
    /// Skips Duplicacy's <c>.duplicacy/</c> metadata folder — the
    /// restore engine creates that inside the target dir to hold the
    /// repo prefs + cache, and it's not part of the user's data.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, byte[]> SnapshotTree(string root)
    {
        var dict = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.StartsWith(".duplicacy/", StringComparison.Ordinal)) continue;
            dict[rel] = File.ReadAllBytes(path);
        }
        return dict;
    }

    private void AssertTreeMatchesSnapshot(
        string restoredRoot,
        System.Collections.Generic.IReadOnlyDictionary<string, byte[]> snapshot,
        string label)
    {
        var restoredFiles = SnapshotTree(restoredRoot);
        Assert.Equal(snapshot.Count, restoredFiles.Count);
        foreach (var (rel, expected) in snapshot)
        {
            Assert.True(restoredFiles.TryGetValue(rel, out var actual),
                $"[{label}] missing in restore: {rel}");
            Assert.Equal(expected.Length, actual.Length);
            Assert.True(expected.SequenceEqual(actual),
                $"[{label}] bytes differ for {rel} (expected {expected.Length} B, got {actual.Length} B)");
        }
        _out.WriteLine($"[{label}] All {snapshot.Count} files byte-identical to the source-at-snapshot.");
    }

    private static (int count, long bytes) CountAndSize(string root)
    {
        if (!Directory.Exists(root)) return (0, 0);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        return (files.Count, files.Sum(f => new FileInfo(f).Length));
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
