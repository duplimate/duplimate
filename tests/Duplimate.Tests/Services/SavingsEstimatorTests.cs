using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

public class SavingsEstimatorTests
{
    [Fact]
    public async Task ComputeAsync_excludesMatchingFiles_includesRest()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "keep.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(tmp.Path, "junk.tmp"), new byte[1000]);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Cache"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "Cache", "x.bin"), new byte[2000]);

        var filters = "e:(?i)\\.tmp$\ne:(?i)(^|/)Cache/";

        var est = new SavingsEstimator();
        var r = await est.ComputeAsync(new[] { tmp.Path }, filters, CancellationToken.None);

        // junk.tmp + everything under Cache should be excluded.
        Assert.Equal(1000 + 2000, r.ExcludedBytes);
        Assert.Equal(100, r.IncludedBytes);
        Assert.False(r.Truncated);
        Assert.Equal(1, r.SourcesScanned);
    }

    [Fact]
    public async Task ComputeAsync_emptyFilters_returnsZeroExcluded()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "a"), new byte[42]);
        var est = new SavingsEstimator();
        var r = await est.ComputeAsync(new[] { tmp.Path }, "", CancellationToken.None);
        Assert.Equal(0, r.ExcludedBytes);
        Assert.Equal(0, r.IncludedBytes);
    }

    [Fact]
    public async Task ComputeAsync_lastMatchingRuleWins_atSameLevel()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "junk.tmp"), new byte[200]);

        // Last-match-wins for files: an include after an exclude on
        // the same path should win.
        var filters = "e:(?i)\\.tmp$\ni:(?i)^junk\\.tmp$";

        var est = new SavingsEstimator();
        var r = await est.ComputeAsync(new[] { tmp.Path }, filters, CancellationToken.None);

        Assert.Equal(0, r.ExcludedBytes);
        Assert.Equal(200, r.IncludedBytes);
    }

    [Fact]
    public async Task ComputeAsync_dirExclusion_winsOverFileLevelInclude()
    {
        // Mirrors actual Duplicacy semantics: once a directory is
        // excluded, its contents are not scanned. An `i:` rule that
        // names a specific file inside the excluded dir has no effect
        // (matching Duplicacy's behaviour, not just "what we'd like").
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "AppData", "Local"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "AppData", "Local", "important.bin"), new byte[500]);

        var filters = "e:(?i)^AppData/\ni:(?i)^AppData/Local/important\\.bin$";

        var est = new SavingsEstimator();
        var r = await est.ComputeAsync(new[] { tmp.Path }, filters, CancellationToken.None);

        // AppData/ is excluded as a whole; the include can't reach into
        // a pruned subtree. Total under AppData = 500 → ExcludedBytes.
        Assert.Equal(500, r.ExcludedBytes);
        Assert.Equal(0, r.IncludedBytes);
    }

    [Fact]
    public async Task ComputeAsync_excludedDir_walksContentsForAccurateSavings()
    {
        using var tmp = new TempDir();
        // Build a deep tree under "skip/" — every file should be
        // counted as excluded so the user sees the real savings.
        var deep = Path.Combine(tmp.Path, "skip");
        for (int i = 0; i < 5; i++)
        {
            deep = Path.Combine(deep, $"level{i}");
            Directory.CreateDirectory(deep);
            File.WriteAllBytes(Path.Combine(deep, "f.bin"), new byte[100]);
        }
        File.WriteAllBytes(Path.Combine(tmp.Path, "keep.txt"), new byte[50]);

        var filters = "e:(?i)^skip/";

        var est = new SavingsEstimator();
        var r = await est.ComputeAsync(new[] { tmp.Path }, filters, CancellationToken.None);

        Assert.Equal(50, r.IncludedBytes);
        Assert.Equal(5 * 100, r.ExcludedBytes);
    }

    [Fact]
    public async Task ComputeAsync_globRules_translateCorrectly()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "a.log"), new byte[10]);
        File.WriteAllBytes(Path.Combine(tmp.Path, "b.txt"), new byte[20]);

        var filters = "-*.log";

        var est = new SavingsEstimator();
        var r = await est.ComputeAsync(new[] { tmp.Path }, filters, CancellationToken.None);
        Assert.Equal(10, r.ExcludedBytes);
        Assert.Equal(20, r.IncludedBytes);
    }

    [Fact]
    public void IsExcluded_nonMatchingPath_defaultsToIncluded()
    {
        var rules = SavingsEstimator.ParseRules("e:(?i)^something-else/");
        Assert.False(SavingsEstimator.IsExcluded(rules, "Documents/letter.docx"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "duplimate-savings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
