using System;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins the run-summary phrasing the BackupCard renders behind
/// "LAST". The user reported: "when a backup is completed, the line
/// LAST in 6m 10s isn't very clear. It'd be best to show LAST
/// 'Completed in 4m 10s'." — that's the no-op success path where
/// nothing was uploaded and no files changed; the only piece of
/// useful info is the duration, and "in 6m 10s" reads as a
/// preposition with no verb.
/// </summary>
public class BackupOrchestratorBuildSummaryTests
{
    [Fact]
    public void NoOpSuccessRun_ReadsAsCompletedInDuration()
    {
        var r = new RunRecord
        {
            BackupId = "b1",
            BackupName = "test",
            Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddMinutes(-6).AddSeconds(-10),
            EndedUtc   = DateTime.UtcNow,
            BytesUploaded = 0,
            FilesNew = 0, FilesModified = 0, FilesRemoved = 0,
        };
        var s = BackupOrchestrator.BuildSummary(r);
        Assert.StartsWith("Completed in ", s);
        // No leading "in " preposition orphaned at the start.
        Assert.False(s.StartsWith("in "), $"Got '{s}' — bare 'in {{dur}}' regression.");
    }

    [Fact]
    public void RunWithUploadedBytes_StillUsesBareInDuration()
    {
        // When the leading parts already describe what happened,
        // "in {dur}" reads naturally — keep the existing phrasing.
        var r = new RunRecord
        {
            BackupId = "b1",
            BackupName = "test",
            Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddMinutes(-2),
            EndedUtc   = DateTime.UtcNow,
            BytesUploaded = 1234567,
            FilesNew = 5,
        };
        var s = BackupOrchestrator.BuildSummary(r);
        Assert.Contains("uploaded", s);
        Assert.Contains(" · in ", s);
        Assert.DoesNotContain("Completed in ", s);
    }

    [Fact]
    public void SkippedRun_StillUsesSkippedAfterPhrasing()
    {
        // The existing "Manually skipped after Xs" phrasing for
        // skipped runs must keep working; my no-op fix only affects
        // the success path.
        var r = new RunRecord
        {
            BackupId = "b1",
            BackupName = "test",
            Status = BackupRunStatus.Skipped,
            StartedUtc = DateTime.UtcNow.AddSeconds(-5),
            EndedUtc   = DateTime.UtcNow,
        };
        var s = BackupOrchestrator.BuildSummary(r, orchestratorCancelReason: "Manually skipped");
        Assert.Contains("Manually skipped", s);
        Assert.Contains("after ", s);
        Assert.DoesNotContain("Completed in ", s);
    }
}
