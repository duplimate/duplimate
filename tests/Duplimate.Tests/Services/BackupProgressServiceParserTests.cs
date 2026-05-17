using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins the BACKUP_STATS / Indexed / Packed line parser used by
/// <see cref="BackupProgressService"/> to capture per-source size
/// hints. The user reported that the per-source "(1.2 GB)" labels on
/// the BackupCard never appeared even after a clean run, because
/// Duplicacy emits the human-readable form ("1,219M bytes") and the
/// previous parser only matched raw integers ("1234567890 bytes").
/// These tests pin both the human-readable AND the raw-integer
/// shapes so the BackupCard's per-source size labels light up
/// reliably.
/// </summary>
public class BackupProgressServiceParserTests
{
    [Theory]
    // The exact format from the user's pasted run log.
    [InlineData("2026-05-02 01:46:57.800 INFO BACKUP_STATS Files: 274 total, 1,219M bytes; 0 new, 0 bytes",
                1219L * 1024 * 1024)]
    // Other unit suffixes Duplicacy may emit at different scales.
    [InlineData("INFO BACKUP_STATS Files: 10 total, 512K bytes",
                512L * 1024)]
    [InlineData("INFO BACKUP_STATS Files: 100 total, 4G bytes",
                4L * 1024 * 1024 * 1024)]
    [InlineData("INFO BACKUP_STATS Files: 1 total, 1T bytes",
                1L * 1024 * 1024 * 1024 * 1024)]
    // Raw bytes without a suffix (fallback for older Duplicacy
    // builds and the Indexed/Packed lines).
    [InlineData("INFO BACKUP_STATS Files: 274 total, 1234567890 bytes",
                1234567890L)]
    [InlineData("Indexed 1234 files (1234567 bytes)",
                1234567L)]
    [InlineData("Packed 50 files (12.5M bytes)",
                (long)(12.5 * 1024 * 1024))]
    public void ParsesKnownSizeHints(string line, long expectedBytes)
    {
        var actual = BackupProgressService.TryParseSourceSizeBytes(line);
        Assert.NotNull(actual);
        // Allow ±1 byte rounding for fractional unit conversions.
        Assert.InRange(actual!.Value, expectedBytes - 1, expectedBytes + 1);
    }

    [Theory]
    // Lines that should NOT match — chunk-upload lines with their own
    // "size" / "bytes" wording, status banners, summaries we don't
    // need to grab a size from.
    [InlineData("2026-05-02 01:46:57.800 INFO RUN_INFO Backup completed")]
    [InlineData("Uploaded chunk 1234 size 4194304, 2.84MB/s 00:00:00 12.5%")]
    [InlineData("=== done: Success — rev 5 · in 1m 01s ===")]
    [InlineData("")]
    [InlineData("2026-05-02 01:46:48 INFO REPOSITORY_SET Repository set to C:\\Jts")]
    public void IgnoresUnrelatedLines(string line)
    {
        Assert.Null(BackupProgressService.TryParseSourceSizeBytes(line));
    }

    [Fact]
    public void ZeroByteValues_AreIgnored()
    {
        // The "; 0 new, 0 bytes" suffix would match if the regex were
        // greedy; the parser anchors to "Files: N total," so the
        // suffix is ignored. Pin it explicitly because a regex tweak
        // could regress this.
        var hit = BackupProgressService.TryParseSourceSizeBytes(
            "INFO BACKUP_STATS Files: 0 total, 0 bytes");
        Assert.Null(hit); // 0 bytes is treated as "no useful hint"
    }
}
