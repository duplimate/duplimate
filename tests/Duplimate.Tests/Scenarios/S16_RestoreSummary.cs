using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Restore wizard step 7 — summary. Two snapshots: a clean success
/// (no failures, just "Open target folder" + "Restore another") and
/// a partial-failure variant (failures list + Retry-all-failed
/// button) so we cover both happy-path copy and the retry surface.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S16_RestoreSummary : ScenarioBase
{
    protected override string ScenarioName => "S16-restore-summary";

    [AvaloniaFact] public async Task Run_S16() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Success-only summary.
        var ok = new RestoreViewModel
        {
            SummaryLine = "Restored 1,234 files (12.3 MB) to C:\\Users\\me\\Documents.",
            AnyFailures = false,
        };
        ok.CurrentStep = RestoreViewModel.Step.Summary;
        var view1 = new RestoreView { DataContext = ok };
        var host1 = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view1 };
        ctx.Driver.Show(host1);
        ctx.Snapshot(host1, "summary success",
            "All files restored — Open target folder + Restore another buttons.");
        host1.Close();

        // Failures + retry-all surface. We hand-populate FailuresList
        // so the bottom card renders rows.
        var bad = new RestoreViewModel
        {
            SummaryLine = "Restored 1,228 of 1,234 files. 6 failed and need attention.",
            AnyFailures = true,
        };
        bad.FailuresList.Add(new FailedFileRow("Documents/big-file-1.bin", "S3 read timed out"));
        bad.FailuresList.Add(new FailedFileRow("Documents/big-file-2.bin", "S3 read timed out"));
        bad.FailuresList.Add(new FailedFileRow("Photos/IMG_2025.jpg", "Chunk decryption failed"));
        bad.FailuresList.Add(new FailedFileRow("Photos/IMG_2026.jpg", "Chunk decryption failed"));
        bad.FailuresList.Add(new FailedFileRow("Music/track.mp3", "Connection reset"));
        bad.FailuresList.Add(new FailedFileRow("Music/podcast.mp3", "Connection reset"));
        bad.CurrentStep = RestoreViewModel.Step.Summary;
        var view2 = new RestoreView { DataContext = bad };
        var host2 = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view2 };
        ctx.Driver.Show(host2);
        ctx.Snapshot(host2, "summary with failures",
            "Failures list visible + Retry all failed primary button.");
        host2.Close();

        return Task.CompletedTask;
    }
}
