using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Local-kind paths only — cloud paths shell out to duplicacy.exe and
/// would need an integration harness to exercise. The local code is the
/// part most likely to leave orphan files on disk if it regresses.
/// </summary>
public class StorageCleanerTests
{
    [Fact]
    public async Task EraseBackupFromDestinationAsync_local_removesOnlyMatchingSnapshotIds()
    {
        using var tmp = new TempDir();
        var snapshotsRoot = Path.Combine(tmp.Path, "snapshots");
        Directory.CreateDirectory(Path.Combine(snapshotsRoot, "mybackup"));
        Directory.CreateDirectory(Path.Combine(snapshotsRoot, "another-backup"));
        File.WriteAllText(Path.Combine(snapshotsRoot, "mybackup", "1"), "snap-data");

        var dest = new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Path, Name = "Local" };
        var backup = new Backup
        {
            Name = "mybackup",
            SourcePaths = { @"C:\foo" },
        };

        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), runner: null!);
        var report = await cleaner.EraseBackupFromDestinationAsync(backup, dest, "default", CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(snapshotsRoot, "mybackup")));
        Assert.True(Directory.Exists(Path.Combine(snapshotsRoot, "another-backup")));
        Assert.Empty(report.Errors);
        Assert.Contains("mybackup", report.DeletedSnapshotIds);
    }

    [Fact]
    public async Task EraseBackupFromDestinationAsync_local_noSnapshotsFolder_isNoop()
    {
        using var tmp = new TempDir();
        var dest = new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Path, Name = "Local" };
        var backup = new Backup { Name = "x", SourcePaths = { @"C:\y" } };

        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), runner: null!);
        var report = await cleaner.EraseBackupFromDestinationAsync(backup, dest, "default", CancellationToken.None);

        Assert.Empty(report.DeletedSnapshotIds);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task WipeEntireDestinationAsync_local_deletesRootFolder()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "snapshots", "a"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "chunks"));
        File.WriteAllText(Path.Combine(tmp.Path, "config"), "x");

        var dest = new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Path, Name = "Local" };

        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), runner: null!);
        var report = await cleaner.WipeEntireDestinationAsync(dest, CancellationToken.None);

        Assert.False(Directory.Exists(tmp.Path));
        Assert.Equal(tmp.Path, report.WipedPath);
        // TempDir.Dispose() will best-effort-delete; still works since the
        // path is already gone.
    }

    /// <summary>
    /// Regression: the user reported that pointing a Local destination
    /// at a folder which already contained personal files, then
    /// removing that destination, deleted EVERYTHING inside the folder.
    /// The fix scopes the wipe to known-Duplicacy artefacts only;
    /// anything the user put there must survive.
    /// </summary>
    [Fact]
    public async Task WipeEntireDestinationAsync_local_preservesUnrelatedUserFiles()
    {
        using var tmp = new TempDir();
        // Duplicacy artefacts (must be removed)
        Directory.CreateDirectory(Path.Combine(tmp.Path, "snapshots", "mybackup"));
        File.WriteAllText(Path.Combine(tmp.Path, "snapshots", "mybackup", "1"), "snap");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "chunks", "ab"));
        File.WriteAllText(Path.Combine(tmp.Path, "chunks", "ab", "cdef"), "chunk");
        File.WriteAllText(Path.Combine(tmp.Path, "config"), "duplicacy-config");
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".duplicacy"));

        // User's pre-existing files in the SAME folder (must NOT be touched)
        File.WriteAllText(Path.Combine(tmp.Path, "tax-return-2024.pdf"), "do-not-delete-me");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Family Photos"));
        File.WriteAllText(Path.Combine(tmp.Path, "Family Photos", "vacation.jpg"), "JFIF...");

        var dest = new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Path, Name = "Local" };

        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), runner: null!);
        var report = await cleaner.WipeEntireDestinationAsync(dest, CancellationToken.None);

        // Duplicacy artefacts: gone.
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "snapshots")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "chunks")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, ".duplicacy")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "config")));

        // User files: untouched. THIS is the critical assertion.
        Assert.True(File.Exists(Path.Combine(tmp.Path, "tax-return-2024.pdf")));
        Assert.Equal("do-not-delete-me", File.ReadAllText(Path.Combine(tmp.Path, "tax-return-2024.pdf")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "Family Photos", "vacation.jpg")));

        // Root folder must remain (it still has user content).
        Assert.True(Directory.Exists(tmp.Path));
        Assert.Empty(report.Errors);
    }

    /// <summary>
    /// Pointing the destination at a folder containing only Duplicacy
    /// data results in the folder itself being removed once empty —
    /// otherwise we'd leak an empty parent directory the user clearly
    /// dedicated to backups.
    /// </summary>
    [Fact]
    public async Task WipeEntireDestinationAsync_local_removesEmptyParentWhenNothingElseInside()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "snapshots"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "chunks"));
        File.WriteAllText(Path.Combine(tmp.Path, "config"), "cfg");

        var dest = new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Path, Name = "Local" };

        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), runner: null!);
        var report = await cleaner.WipeEntireDestinationAsync(dest, CancellationToken.None);

        Assert.False(Directory.Exists(tmp.Path));
        Assert.Empty(report.Errors);
    }

    /// <summary>
    /// A destination pointed at a folder that has neither Duplicacy
    /// data nor user files reports a no-op rather than an error. The
    /// folder is left in place because we can't tell whether it's
    /// "ours to delete" if there's nothing to identify it.
    /// </summary>
    [Fact]
    public async Task WipeEntireDestinationAsync_local_emptyUnrelatedFolder_isNoop()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "readme.txt"), "user file");

        var dest = new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Path, Name = "Local" };

        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), runner: null!);
        var report = await cleaner.WipeEntireDestinationAsync(dest, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "readme.txt")));
        Assert.True(Directory.Exists(tmp.Path));
        Assert.Empty(report.Errors);
        Assert.Null(report.WipedPath);
        Assert.Contains(report.Notes, n => n.Contains("nothing to remove"));
    }

    [Fact]
    public async Task WipeEntireDestinationAsync_cloud_failsCleanlyWhenProbeUnset()
    {
        // Cloud wipe relies on Probe to enumerate snapshot ids — if the
        // locator forgot to wire it, we should NOT throw. We log + record
        // an error in the report so the caller can decide what to do.
        var dest = new Destination
        {
            Kind = DestinationKind.DropboxAppScoped,
            PathOrSubpath = "my-repo",
            Name = "Dropbox",
        };

        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), runner: null!);
        var report = await cleaner.WipeEntireDestinationAsync(dest, CancellationToken.None);

        Assert.NotEmpty(report.Errors);

        // No probe means we never reach the synthetic-backup code path,
        // so no _wipe_ repo dir should have been created. Sanity-check
        // the repos folder doesn't accumulate orphans across wipes.
        var reposRoot = Duplimate.Services.AppPaths.ReposRoot;
        if (System.IO.Directory.Exists(reposRoot))
        {
            var orphans = System.IO.Directory.EnumerateDirectories(reposRoot, "_wipe_*").ToList();
            Assert.Empty(orphans);
        }
    }

    private sealed class TempDir : System.IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "duplimate-cleaner-tests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
