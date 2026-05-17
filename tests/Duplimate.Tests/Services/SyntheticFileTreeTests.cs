using System.IO;
using System.Linq;
using Duplimate.Tests.TestHelpers;
using Xunit;

namespace Duplimate.Tests.Services;

public class SyntheticFileTreeTests
{
    [Fact]
    public void Generate_WithSameSeed_ProducesByteIdenticalTrees()
    {
        using var wsA = new TempWorkspace("seedA");
        using var wsB = new TempWorkspace("seedB");

        SyntheticFileTree.Generate(wsA.Sub("data"), seed: 1234, targetBytes: 512_000);
        SyntheticFileTree.Generate(wsB.Sub("data"), seed: 1234, targetBytes: 512_000);

        var hashA = DirectoryHash.Compute(Path.Combine(wsA.Root, "data"));
        var hashB = DirectoryHash.Compute(Path.Combine(wsB.Root, "data"));

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void Generate_WithDifferentSeeds_ProducesDifferentTrees()
    {
        using var wsA = new TempWorkspace("seed1");
        using var wsB = new TempWorkspace("seed2");

        SyntheticFileTree.Generate(wsA.Sub("data"), seed: 1, targetBytes: 512_000);
        SyntheticFileTree.Generate(wsB.Sub("data"), seed: 2, targetBytes: 512_000);

        var hashA = DirectoryHash.Compute(Path.Combine(wsA.Root, "data"));
        var hashB = DirectoryHash.Compute(Path.Combine(wsB.Root, "data"));

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void Generate_WithBudget_ReachesRoughlyTargetSize()
    {
        using var ws = new TempWorkspace("budget");
        var root = ws.Sub("data");
        const long budget = 2_000_000;

        var summary = SyntheticFileTree.Generate(root, seed: 42, targetBytes: budget);

        Assert.True(summary.FileCount > 0,        $"expected some files, got {summary.FileCount}");
        Assert.True(summary.TotalBytes > 0,       $"expected some bytes, got {summary.TotalBytes}");
        // Budget is approximate — we stop as soon as we've crossed it.
        // Allow ±50% slack.
        Assert.True(summary.TotalBytes <= budget * 1.5 && summary.TotalBytes >= budget * 0.3,
            $"expected ~{budget} bytes, got {summary.TotalBytes}");
    }

    [Fact]
    public void Generate_ProducesMultipleExtensions_And_NestedDirs()
    {
        using var ws = new TempWorkspace("mix");
        var root = ws.Sub("data");
        SyntheticFileTree.Generate(root, seed: 99, targetBytes: 3_000_000);

        var allFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        var exts = allFiles.Select(p => Path.GetExtension(p).ToLowerInvariant()).Distinct().ToList();
        Assert.True(exts.Count >= 3, $"expected a mix of extensions, got {string.Join(',', exts)}");

        var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList();
        Assert.True(dirs.Count >= 1, "expected nested subdirectories");
    }
}
