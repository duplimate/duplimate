using System.IO;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Regression coverage for the .duplicacy/ cleanup the engine does
/// after a restore completes. The user reported: "After a folder is
/// restored, I can see a .duplicacy folder inside. It shouldn't be
/// necessary right? If so, it should be deleted to not confuse
/// users." The engine now wipes the restore-shim metadata in its
/// finally block.
///
/// We test the helper directly because the full RunAsync path spawns
/// duplicacy.exe (out of scope for unit tests). The helper is what
/// owns the file-system contract.
/// </summary>
public class RestoreEngineCleanupTests
{
    [Fact]
    public void TryRemoveRestoreMetadata_DeletesDuplicacyFolder_WhenPresent()
    {
        var tmp = NewTempDir();
        try
        {
            var meta = Directory.CreateDirectory(Path.Combine(tmp, ".duplicacy"));
            File.WriteAllText(Path.Combine(meta.FullName, "preferences"), "{}");
            File.WriteAllText(Path.Combine(meta.FullName, "cache.bin"), "abc");

            RestoreEngine.TryRemoveRestoreMetadata(tmp);

            Assert.False(Directory.Exists(Path.Combine(tmp, ".duplicacy")));
            // The target dir itself survives — only the metadata
            // subfolder is removed.
            Assert.True(Directory.Exists(tmp));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void TryRemoveRestoreMetadata_PreservesUserFiles_AlongsideDeletedMetadata()
    {
        // The restored content lives next to the .duplicacy/ shim.
        // Pin that the cleanup ONLY touches .duplicacy/ and never
        // walks sibling directories — a path collision there would
        // delete the user's restored files.
        var tmp = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, ".duplicacy", "cache"));
            File.WriteAllText(Path.Combine(tmp, ".duplicacy", "preferences"), "{}");

            var keep = Path.Combine(tmp, "Documents");
            Directory.CreateDirectory(keep);
            var file = Path.Combine(keep, "letter.txt");
            File.WriteAllText(file, "important");

            RestoreEngine.TryRemoveRestoreMetadata(tmp);

            Assert.False(Directory.Exists(Path.Combine(tmp, ".duplicacy")));
            Assert.True(File.Exists(file));
            Assert.Equal("important", File.ReadAllText(file));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void TryRemoveRestoreMetadata_StripsReadOnlyAttribute_BeforeDelete()
    {
        // Duplicacy's cache files can land marked read-only on
        // Windows; Directory.Delete then refuses with
        // UnauthorizedAccess. The helper strips read-only flags
        // first so the wipe survives that case.
        var tmp = NewTempDir();
        try
        {
            var meta = Directory.CreateDirectory(Path.Combine(tmp, ".duplicacy"));
            var locked = Path.Combine(meta.FullName, "ro.bin");
            File.WriteAllText(locked, "x");
            File.SetAttributes(locked, FileAttributes.ReadOnly);

            RestoreEngine.TryRemoveRestoreMetadata(tmp);

            Assert.False(Directory.Exists(Path.Combine(tmp, ".duplicacy")));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void TryRemoveRestoreMetadata_SilentNoop_WhenNoMetadataDir()
    {
        // Caller invokes the helper unconditionally based on a "did
        // we create it?" check; if the user's target never had a
        // .duplicacy/ in the first place (race, manual cleanup), the
        // helper must not throw.
        var tmp = NewTempDir();
        try
        {
            // Should not throw.
            RestoreEngine.TryRemoveRestoreMetadata(tmp);
            Assert.True(Directory.Exists(tmp));
        }
        finally { TryDelete(tmp); }
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "duplimate-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(p);
        return p;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* test cleanup — ignore */ }
    }
}
