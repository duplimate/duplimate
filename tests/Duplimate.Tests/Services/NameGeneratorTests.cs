using System.Collections.Generic;
using System.Linq;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

public class NameGeneratorTests
{
    // Sanity: a generated suffix always lands in our base36 alphabet.
    [Fact]
    public void RandomSuffix_is_six_base36_chars()
    {
        for (int i = 0; i < 500; i++)
        {
            var s = NameGenerator.RandomSuffix();
            Assert.Equal(6, s.Length);
            Assert.True(s.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')),
                $"suffix {s} had non-base36 chars");
        }
    }

    [Fact]
    public void ForBackup_singleSource_usesLeafDirname()
    {
        var name = NameGenerator.ForBackup(
            new[] { @"C:\Users\me\Documents" }, _ => false);
        Assert.StartsWith("documents-", name);
        Assert.Equal(7 + "documents".Length, name.Length); // "documents-" + 6 chars
    }

    [Fact]
    public void ForBackup_multipleSources_usesMultiCountLabel()
    {
        var name = NameGenerator.ForBackup(
            new[] { @"C:\a", @"D:\b", @"E:\c" }, _ => false);
        Assert.StartsWith("multi-3-", name);
    }

    [Fact]
    public void ForBackup_driveRoot_usesDriveLetterLabel()
    {
        var name = NameGenerator.ForBackup(new[] { @"C:\" }, _ => false);
        Assert.StartsWith("drive-c-", name);
    }

    // Uniqueness: the caller owns "already exists" knowledge. We just keep
    // rolling suffixes until we find a fresh one.
    [Fact]
    public void ForBackup_retriesSuffixOnCollision()
    {
        // Force the first two suggestions to collide.
        int calls = 0;
        var name = NameGenerator.ForBackup(
            new[] { @"C:\docs" },
            candidate =>
            {
                calls++;
                if (calls <= 2) return true;  // simulate first 2 colliding
                return false;
            });
        Assert.True(calls >= 3, $"expected at least 3 tries, got {calls}");
        Assert.StartsWith("docs-", name);
    }

    [Fact]
    public void ForDestination_local_usesTailOfPath()
    {
        var name = NameGenerator.ForDestination(
            DestinationKind.LocalFolder, @"C:\Backups\Duplicacy", null, _ => false);
        Assert.Contains("Local", name);
        // tail-2 trimming → shows "Backups\Duplicacy" (plus "…\" prefix)
        Assert.Contains("Duplicacy", name);
    }

    [Fact]
    public void ForDestination_cloud_includesAccountAndSubfolder()
    {
        var name = NameGenerator.ForDestination(
            DestinationKind.DropboxAppScoped, "you@example.com", "my-repo", _ => false);
        Assert.Equal("Dropbox - you@example.com - my-repo", name);
    }

    [Fact]
    public void ForDestination_s3_usesBucketName()
    {
        var name = NameGenerator.ForDestination(
            DestinationKind.S3Compatible, "my-bucket", null, _ => false);
        Assert.Equal("S3 - my-bucket", name);
    }

    [Fact]
    public void ForDestination_collision_appendsNumberedSuffix()
    {
        var takenOnce = false;
        var name = NameGenerator.ForDestination(
            DestinationKind.LocalFolder, @"C:\Bk", null,
            candidate =>
            {
                if (!takenOnce) { takenOnce = true; return true; }
                return false;
            });
        Assert.Contains("(2)", name);
    }
}
