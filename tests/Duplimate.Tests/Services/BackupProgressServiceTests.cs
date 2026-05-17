using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pin the Duplicacy stdout → progress percentage parser. The format
/// changes occasionally across CLI versions; if a future release shifts
/// "Uploaded chunk N size M, V MB/s HH:MM:SS X.X%" to something
/// incompatible, these tests will catch it before the live progress
/// bar in the BackupCard goes silently dead.
/// </summary>
public class BackupProgressServiceTests
{
    [Fact]
    public void ReportLine_ParsesUploadedChunkPercent()
    {
        var svc = new BackupProgressService();
        svc.ReportCellStarted("b1", "C:\\src", "Dropbox");
        svc.ReportLine("b1", "C:\\src",
            "Uploaded chunk 1 size 4194304, 2.84MB/s 00:00:00 12.5%");

        var snap = svc.Snapshot("b1");
        Assert.NotNull(snap);
        Assert.Single(snap!.Sources);
        Assert.Equal(12.5, snap.Sources["C:\\src"].PercentComplete, 1);
        Assert.True(snap.Sources["C:\\src"].IsRunning);
    }

    [Fact]
    public void ReportLine_PercentIsMonotonic()
    {
        // A late-arriving line out of order MUST NOT walk progress
        // backward — the UI would visibly stutter and confuse the user.
        var svc = new BackupProgressService();
        svc.ReportCellStarted("b1", "C:\\src", "Dropbox");
        svc.ReportLine("b1", "C:\\src", "Uploaded chunk 1 size 4194304, 1MB/s 00:00:00 25.0%");
        svc.ReportLine("b1", "C:\\src", "Uploaded chunk 2 size 4194304, 1MB/s 00:00:00 5.0%");

        var snap = svc.Snapshot("b1")!;
        Assert.Equal(25.0, snap.Sources["C:\\src"].PercentComplete, 1);
    }

    [Fact]
    public void ReportLine_NonProgressLines_AreIgnored()
    {
        var svc = new BackupProgressService();
        svc.ReportCellStarted("b1", "C:\\src", "Dropbox");
        svc.ReportLine("b1", "C:\\src", "Backup for /repo at revision 12 started");
        svc.ReportLine("b1", "C:\\src", "");

        var snap = svc.Snapshot("b1")!;
        // No percentage parsed — but the cell-start kept it at 0.
        Assert.Equal(0.0, snap.Sources["C:\\src"].PercentComplete);
    }

    [Fact]
    public void ReportCellEnded_ClampsTo100OnSuccess()
    {
        var svc = new BackupProgressService();
        svc.ReportCellStarted("b1", "C:\\src", "Dropbox");
        svc.ReportLine("b1", "C:\\src", "Uploaded chunk 1 size 4194304, 1MB/s 00:00:00 87.5%");
        svc.ReportCellEnded("b1", "C:\\src", success: true);

        var snap = svc.Snapshot("b1")!;
        Assert.Equal(100.0, snap.Sources["C:\\src"].PercentComplete, 1);
        Assert.False(snap.Sources["C:\\src"].IsRunning);
    }

    [Fact]
    public void Overall_AveragesPerSourcePercents()
    {
        var svc = new BackupProgressService();
        svc.ReportCellStarted("b1", "A", "X");
        svc.ReportCellStarted("b1", "B", "X");
        svc.ReportLine("b1", "A", "Uploaded chunk 1 0.0%");
        svc.ReportLine("b1", "A", "Uploaded chunk 2 50.0%");
        svc.ReportLine("b1", "B", "Uploaded chunk 1 100.0%");

        var snap = svc.Snapshot("b1")!;
        // Plain average — A=50, B=100 → 75
        Assert.Equal(75.0, snap.OverallPercent, 1);
    }

    [Fact]
    public void Updated_FiresOnEachReport()
    {
        var svc = new BackupProgressService();
        var fired = 0;
        svc.Updated += _ => fired++;
        svc.ReportCellStarted("b1", "src", "dst");
        svc.ReportLine("b1", "src", "Uploaded chunk 1 50%");
        svc.ReportCellEnded("b1", "src", true);
        Assert.True(fired >= 3);
    }

    [Fact]
    public void Overall_PartialWeights_FallsBackToEqualWeighting_NotCollapse()
    {
        // The user reported a card showing source 1 at 13%, source 2
        // at 0%, and Overall stuck at 0% — the math 13×1 + 0×huge ÷
        // (1 + huge) collapsed because we mixed a "1" fallback weight
        // for the unweighted source with a real-bytes weight for the
        // other. Pin that the partial-weights state DOES NOT happen:
        // when ANY source is missing a weight, every source is
        // weighted equally so a 13%/0% split shows ~6.5%, not ~0%.
        var svc = new BackupProgressService();
        // Two sources, two destinations → 4 cells.
        svc.RegisterRunCells("b1",
            sourcePaths: new[] { "A", "B" },
            destinationNames: new[] { "X", "Y" },
            // PARTIAL: only B has a weight. A is missing — exactly
            // the bug scenario from the SizeProbe cache.
            sourceSizeBytes: new System.Collections.Generic.Dictionary<string, long> { ["B"] = 50_000_000 });
        // ReportLine routes by (backupId, sourcePath); we need to
        // also pass the destination so it lands on a specific cell.
        // Use the explicit-destination overload.
        svc.ReportLine("b1", "A", "X", "Uploaded chunk 1 size 4194304, 2.84MB/s 00:00:00 26.0%");
        // Other cells stay at 0%.

        var snap = svc.Snapshot("b1")!;
        // Source A: avg of cells [26%, 0%] = 13%.
        Assert.Equal(13.0, snap.Sources["A"].PercentComplete, 1);
        // Source B: 0%.
        Assert.Equal(0.0, snap.Sources["B"].PercentComplete, 1);
        // Overall: equal weighting (because A has no weight) →
        // (13 + 0) / 2 = 6.5%. NOT 0% (the bug shape).
        Assert.True(snap.OverallPercent > 5.0,
            $"Overall collapsed to {snap.OverallPercent}% — partial-weights bug is back; should be ~6.5%.");
        Assert.True(snap.OverallPercent < 8.0,
            $"Overall = {snap.OverallPercent}% is higher than the equal-weight max; weighting math is off.");
    }

    [Fact]
    public void ReportCellStarted_ClearsStalePhase_FromPriorRun()
    {
        // The user reported the BackupCard's phase chip showed
        // "Pruning…" the moment they started a backup — long
        // before any prune phase actually ran. Root cause was
        // CellProgress instances being reused across runs (keyed
        // by destination name) with their Phase string sticking
        // to whatever the last marker was — typically "Checking"
        // or "Pruning" because the backup pipeline ends with
        // backup→prune→check. ReportCellStarted now wipes Phase
        // back to empty so the chip stays blank until duplicacy
        // emits the first marker of the NEW run.
        var svc = new BackupProgressService();

        // Simulate a previous run that ended on the Check phase.
        svc.ReportCellStarted("b1", "src", "dest");
        svc.ReportLine("b1", "src", "dest", "10:00:00 --- Backup ---");
        svc.ReportLine("b1", "src", "dest", "Uploaded chunk 1 size 4194304, 2.84MB/s 00:00:00 100.0%");
        svc.ReportLine("b1", "src", "dest", "10:00:30 --- Prune ---");
        svc.ReportLine("b1", "src", "dest", "10:01:00 --- Check ---");
        svc.ReportCellEnded("b1", "src", "dest", success: true);

        var midSnap = svc.Snapshot("b1")!;
        // Sanity: the cell DID record a "Checking" phase from
        // the prior run.
        Assert.Equal("Checking", midSnap.Sources["src"].Cells["dest"].Phase);

        // Now start a NEW run on the same (source, destination) pair.
        svc.ReportCellStarted("b1", "src", "dest");

        var freshSnap = svc.Snapshot("b1")!;
        // Phase MUST be wiped — the new run hasn't emitted any
        // phase marker yet, so the UI chip should stay blank.
        Assert.Equal("", freshSnap.Sources["src"].Cells["dest"].Phase);
    }

    [Fact]
    public void Overall_AllWeighted_UsesByteWeightedAverage()
    {
        // When every source has a real weight, byte-weighted mode
        // kicks in: a 100 GB source dominates a 100 MB source. Pin
        // that this path still works — the partial-weights guard
        // only falls back to equal-weight when at least one source
        // is unweighted.
        var svc = new BackupProgressService();
        svc.RegisterRunCells("b1",
            sourcePaths: new[] { "Big", "Tiny" },
            destinationNames: new[] { "X" },
            sourceSizeBytes: new System.Collections.Generic.Dictionary<string, long>
            {
                ["Big"]  = 100L * 1024 * 1024 * 1024,  // 100 GB
                ["Tiny"] = 100L * 1024 * 1024,          // 100 MB
            });
        svc.ReportLine("b1", "Big", "X", "Uploaded chunk 1 size 4194304, 2.84MB/s 00:00:00 50.0%");
        // Tiny stays at 0%.

        var snap = svc.Snapshot("b1")!;
        // Big: 50%, weight 100 GB. Tiny: 0%, weight 100 MB.
        // Weighted: ~50% (Big dominates).
        Assert.True(snap.OverallPercent > 49.0,
            $"Big-source-dominates expected ~50% but got {snap.OverallPercent}%.");
    }
}
