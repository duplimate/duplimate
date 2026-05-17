using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

public class SourceSizeProbeTests
{
    [Fact]
    public async Task GetOrComputeAsync_emptyDir_returnsZero()
    {
        using var tmp = new TempDir();
        var probe = new SourceSizeProbe();
        var r = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.True(r.Completed);
        Assert.Equal(0, r.Bytes);
        Assert.Equal(0, r.FileCount);
    }

    [Fact]
    public async Task GetOrComputeAsync_sumsFileBytesAndCounts()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "a.bin"), new byte[1234]);
        File.WriteAllBytes(Path.Combine(tmp.Path, "b.bin"), new byte[5678]);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "sub"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "sub", "c.bin"), new byte[42]);

        var probe = new SourceSizeProbe();
        var r = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.True(r.Completed);
        Assert.Equal(1234L + 5678 + 42, r.Bytes);
        Assert.Equal(3, r.FileCount);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task GetOrComputeAsync_cachesResult_secondCallNoWalk()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "x"), new byte[100]);
        var probe = new SourceSizeProbe();
        var r1 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        // Add a file AFTER the first probe; cache should hide it.
        File.WriteAllBytes(Path.Combine(tmp.Path, "y"), new byte[100]);
        var r2 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.Equal(r1.Bytes, r2.Bytes);
        Assert.Equal(r1.FileCount, r2.FileCount);
    }

    [Fact]
    public async Task Invalidate_dropsCacheEntry()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "x"), new byte[100]);
        var probe = new SourceSizeProbe();
        var r1 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(tmp.Path, "y"), new byte[100]);
        probe.Invalidate(tmp.Path);
        var r2 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.True(r2.Bytes > r1.Bytes, $"Expected re-probe to see new file. r1={r1.Bytes} r2={r2.Bytes}");
    }

    [Fact]
    public async Task GetOrComputeAsync_missingPath_returnsIncomplete()
    {
        var probe = new SourceSizeProbe();
        var r = await probe.GetOrComputeAsync(@"C:\definitely-not-a-real-path-" + System.Guid.NewGuid().ToString("N"),
            CancellationToken.None);
        Assert.False(r.Completed);
        Assert.Equal(0, r.Bytes);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "duplimate-sizeprobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
