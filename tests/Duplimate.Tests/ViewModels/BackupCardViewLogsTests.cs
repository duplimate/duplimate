using System;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Pins the inline "View logs" pill on the BackupCard. The user
/// reported it wasn't appearing for a Warning run; this verifies
/// the contract: <c>HasAttentionStatus</c> goes true exactly when
/// the last run was Warning or Failed, and the property fires
/// PropertyChanged so the IsVisible binding picks up the flip.
/// </summary>
public class BackupCardViewLogsTests
{
    [Theory]
    [InlineData(BackupRunStatus.Warning, true)]
    [InlineData(BackupRunStatus.Failed,  true)]
    [InlineData(BackupRunStatus.Success, false)]
    [InlineData(BackupRunStatus.Skipped, false)]
    [InlineData(BackupRunStatus.Running, false)]
    [InlineData(BackupRunStatus.NeverRun, false)]
    public void HasAttentionStatus_TracksLastRunStatus(BackupRunStatus status, bool expected)
    {
        ResetConfig();
        var b = new Backup { Name = "view-logs-test", LastRunStatus = status };
        var card = new BackupCardViewModel(b, showActions: true, showProgress: false);
        Assert.Equal(expected, card.HasAttentionStatus);
    }

    [Fact]
    public void HasAttentionStatus_FiresPropertyChanged_OnStatusFlip()
    {
        // The IsVisible binding on the View-logs pill is a one-way
        // bind to HasAttentionStatus. Without a PropertyChanged
        // notification when StatusClass mutates, the pill would
        // never appear post-construction (Apply(b) flips StatusClass
        // when a fresh RunRecord lands). Pin the notification.
        ResetConfig();
        var b = new Backup { Name = "fire-pc", LastRunStatus = BackupRunStatus.Success };
        var card = new BackupCardViewModel(b, showActions: true, showProgress: false);

        var fired = false;
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BackupCardViewModel.HasAttentionStatus))
                fired = true;
        };

        // Simulate a Warning RunRecord landing on the card.
        card.Apply(new RunRecord
        {
            BackupId = b.Id, BackupName = b.Name,
            StartedUtc = DateTime.UtcNow.AddSeconds(-30),
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Warning,
            Summary = "1 errors · in 30s",
        });

        Assert.True(card.HasAttentionStatus);
        Assert.True(fired,
            "Expected PropertyChanged for HasAttentionStatus when the run record flipped status to Warning.");
    }

    [Fact]
    public void OldPersistedSummary_BareInDuration_RewritesToCompletedAtRender()
    {
        // The user reported: "I am still seeing LAST in 4s — 7d ago
        // (no Completed)". The persisted history.json was written by
        // an older build that emitted bare "in 4s"; my orchestrator
        // change only affects new runs. Pin that the BackupCard
        // catches old records up at render time so they read as
        // "Completed in 4s · 7d ago" too.
        ResetConfig();
        var b = new Backup
        {
            Name = "old-summary",
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "in 4s",
            LastRunEndUtc = DateTime.UtcNow.AddDays(-7),
        };
        var card = new BackupCardViewModel(b, showActions: true, showProgress: false);
        // LastRunLabel is "{summary} · {ago}" — assert the prefix
        // got rewritten regardless of the ago text format.
        Assert.StartsWith("Completed in 4s · ", card.LastRunLabel);
    }

    [Fact]
    public void NewSuccessSummary_AlreadyHasCompletedPrefix_NotDoubled()
    {
        // New runs come in with "Completed in Xs" already. Don't
        // prepend "Completed " again.
        ResetConfig();
        var b = new Backup
        {
            Name = "new-summary",
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "Completed in 3s",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-5),
        };
        var card = new BackupCardViewModel(b, showActions: true, showProgress: false);
        Assert.StartsWith("Completed in 3s · ", card.LastRunLabel);
        Assert.DoesNotContain("Completed Completed", card.LastRunLabel);
    }

    [Fact]
    public void NormaliseSummary_DoesNotMatchInCaseOfFirePrefix()
    {
        // Defensive: the rewrite regex must not false-positive on
        // a summary that happens to start with "in" but isn't a
        // duration. Only "in <digit>…" should trigger.
        ResetConfig();
        var b = new Backup
        {
            Name = "weird-summary",
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "in case of fire please ignore",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        var card = new BackupCardViewModel(b, showActions: true, showProgress: false);
        Assert.DoesNotContain("Completed", card.LastRunLabel);
        Assert.StartsWith("in case of fire", card.LastRunLabel);
    }

    [Fact]
    public void IsActuallyRunning_FalseWhileQueued_TrueWhenRunning()
    {
        // Pin the Running / Queued toggle. The card's top-right
        // indicator binds to IsActuallyRunning vs IsQueued — they
        // must be mutually-exclusive within IsRunning=true so the
        // user never sees BOTH "Running…" and "Queued…" at once.
        ResetConfig();
        var b = new Backup { Name = "queue-flip" };
        var card = new BackupCardViewModel(b, showActions: true, showProgress: false);

        // Idle → both off.
        Assert.False(card.IsRunning);
        Assert.False(card.IsQueued);
        Assert.False(card.IsActuallyRunning);

        // Run starts → IsActuallyRunning true, queue still false.
        card.SetRunning();
        Assert.True(card.IsRunning);
        Assert.False(card.IsQueued);
        Assert.True(card.IsActuallyRunning);

        // Orchestrator parks the run on the destination semaphore.
        card.SetQueuedReason("waiting for «Other»");
        Assert.True(card.IsRunning);
        Assert.True(card.IsQueued);
        Assert.False(card.IsActuallyRunning);
        Assert.StartsWith("Queued — ", card.LastRunLabel);

        // Gate acquired → flip back to actually-running.
        card.SetQueuedReason("");
        Assert.True(card.IsRunning);
        Assert.False(card.IsQueued);
        Assert.True(card.IsActuallyRunning);
        Assert.Equal("Running…", card.LastRunLabel);
    }

    [Fact]
    public void DescriptiveSuccessSummary_NotRewritten()
    {
        // "1.2 GB uploaded · in 5s" already has descriptive context;
        // don't prepend "Completed " — that'd read as "Completed 1.2
        // GB uploaded · in 5s" which is wrong.
        ResetConfig();
        var b = new Backup
        {
            Name = "uploaded-summary",
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "1.2 GB uploaded · in 5s",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        var card = new BackupCardViewModel(b, showActions: true, showProgress: false);
        Assert.StartsWith("1.2 GB uploaded · in 5s · ", card.LastRunLabel);
        Assert.DoesNotContain("Completed", card.LastRunLabel);
    }

    private static void ResetConfig()
    {
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
