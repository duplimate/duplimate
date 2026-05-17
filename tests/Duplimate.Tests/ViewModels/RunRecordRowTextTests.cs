using System;
using System.Globalization;
using Duplimate.Models;
using Duplimate.ViewModels;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Pins how a RunRecord renders in the Logs &amp; runs RUN dropdown.
/// The user reported "the RUN name is {Date - Skipped - Manually
/// skipped - skipped after XXs}. We have skipped 3 times! When you
/// show status, combine everything in one clean sentence when words
/// would otherwise be redundant. Like Date - Manually skipped after
/// XXs (also in the Backup card view)." This test pins the de-dup
/// rules that produce that compact phrasing.
/// </summary>
public class RunRecordRowTextTests
{
    [Fact]
    public void Skipped_WithManuallySkippedSummary_DropsRedundantStatusCell()
    {
        // The summary already says "skipped" — the standalone Status
        // cell would just repeat the word. Drop the cell so the row
        // reads: "{Date} · Manually skipped after 5s".
        var rec = MakeRecord(BackupRunStatus.Skipped, "Manually skipped after 5s");
        var rendered = Render(rec);

        Assert.Contains("Manually skipped after 5s", rendered);
        // Three "skipped" tokens before the fix; one now.
        Assert.Equal(1, CountOccurrences(rendered.ToLowerInvariant(), "skipped"));
    }

    [Fact]
    public void Skipped_WithDriveUnpluggedSummary_DropsRedundantStatusCell()
    {
        // Cell-level reasons can be more elaborate ("Skipped — drive
        // unplugged"). Same fold rule: don't double up the word.
        var rec = MakeRecord(BackupRunStatus.Skipped, "Skipped — drive unplugged after 2s");
        var rendered = Render(rec);

        Assert.Contains("drive unplugged", rendered);
        Assert.Equal(1, CountOccurrences(rendered.ToLowerInvariant(), "skipped"));
    }

    [Fact]
    public void Success_KeepsBothStatusAndSummary()
    {
        // Success summary is descriptive of WHAT was done, not OF the
        // status word. Keep both cells so the user sees the badge AND
        // the per-run detail.
        var rec = MakeRecord(BackupRunStatus.Success, "1.2 GB uploaded · in 2m 11s");
        var rendered = Render(rec);

        Assert.Contains("Success", rendered);
        Assert.Contains("1.2 GB uploaded", rendered);
    }

    [Fact]
    public void Running_FoldsTheTrailingEllipsisSummary()
    {
        // Status="Running", Summary="Running…" → existing fold case;
        // pin it so the new Skipped path doesn't accidentally
        // regress the prior collapse behaviour.
        var rec = MakeRecord(BackupRunStatus.Running, "Running…");
        var rendered = Render(rec);

        // One "Running" token (the summary form, since it's more
        // informative than the bare enum).
        Assert.Equal(1, CountOccurrences(rendered, "Running"));
    }

    [Fact]
    public void Failed_KeepsStatusAndSummary_NoFold()
    {
        // Failed shouldn't fold — the summary describes WHY
        // ("Run threw before completing.") and the badge tells WHAT.
        // Both are useful; only Skipped's vocabulary is contractual
        // enough to consistently double-up.
        var rec = MakeRecord(BackupRunStatus.Failed, "Run threw before completing.");
        var rendered = Render(rec);

        Assert.Contains("Failed", rendered);
        Assert.Contains("Run threw before completing.", rendered);
    }

    private static RunRecord MakeRecord(BackupRunStatus status, string summary) => new()
    {
        BackupId   = "b1",
        BackupName = "Test Backup",
        StartedUtc = new DateTime(2026, 5, 1, 18, 9, 11, DateTimeKind.Utc),
        EndedUtc   = new DateTime(2026, 5, 1, 18, 9, 16, DateTimeKind.Utc),
        Status     = status,
        Summary    = summary,
    };

    private static string Render(RunRecord r) =>
        (string)RunRecordRowText.To.Convert(r, typeof(string), null, CultureInfo.InvariantCulture)!;

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
