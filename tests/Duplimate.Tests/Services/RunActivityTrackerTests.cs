using System;
using System.Linq;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins the in-flight registry that LogsViewModel synthesizes its
/// "Running" Restore + Test-restore RUN dropdown rows from. The user
/// reported: "Doing a test restore shows in the Live view instead of
/// as a Running test restore run (in RUN)." The fix relies on
/// (1) Register publishing the entry, (2) Unregister removing it,
/// (3) GetRunningForBackup filtering by BackupId and ordering newest-
/// first, and (4) RunningChanged firing on both transitions so the
/// dropdown re-loads.
/// </summary>
public class RunActivityTrackerTests
{
    [Fact]
    public void Register_PublishesEntryAndFiresEvent()
    {
        var tracker = new RunActivityTracker();
        var fired = 0;
        tracker.RunningChanged += () => fired++;

        var rec = MakeRunning("b1", "Backup A", RunKind.Restore);
        tracker.Register(rec);

        var inflight = tracker.GetRunningForBackup("b1");
        Assert.Single(inflight);
        Assert.Same(rec, inflight[0]); // SAME instance — dropdown identity hinges on this
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Unregister_RemovesEntryAndFiresEvent()
    {
        var tracker = new RunActivityTracker();
        var rec = MakeRunning("b1", "Backup A", RunKind.TestRestore);
        tracker.Register(rec);

        var fired = 0;
        tracker.RunningChanged += () => fired++;
        tracker.Unregister(rec.Id);

        Assert.Empty(tracker.GetRunningForBackup("b1"));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void GetRunningForBackup_FiltersByBackupId()
    {
        var tracker = new RunActivityTracker();
        tracker.Register(MakeRunning("b1", "Backup A", RunKind.Restore));
        tracker.Register(MakeRunning("b2", "Backup B", RunKind.Restore));
        tracker.Register(MakeRunning("b1", "Backup A", RunKind.TestRestore));

        Assert.Equal(2, tracker.GetRunningForBackup("b1").Count);
        Assert.Single(tracker.GetRunningForBackup("b2"));
        Assert.Empty(tracker.GetRunningForBackup("nope"));
    }

    [Fact]
    public void GetRunningForBackup_OrdersNewestFirst()
    {
        // The LogsView dropdown prepends synthetic in-flight rows above
        // persisted runs and orders them newest-first; the tracker must
        // hand back the same ordering so a Test-restore drill that
        // started AFTER a Restore wizard run is shown above it.
        var tracker = new RunActivityTracker();
        var older = MakeRunning("b1", "Backup A", RunKind.Restore);
        older.StartedUtc = DateTime.UtcNow.AddSeconds(-30);
        var newer = MakeRunning("b1", "Backup A", RunKind.TestRestore);
        newer.StartedUtc = DateTime.UtcNow;

        tracker.Register(older);
        tracker.Register(newer);

        var ordered = tracker.GetRunningForBackup("b1");
        Assert.Equal(2, ordered.Count);
        Assert.Same(newer, ordered[0]);
        Assert.Same(older, ordered[1]);
    }

    [Fact]
    public void Register_EmptyId_IsNoOp()
    {
        // Defensive: an empty-Id record would clobber the registry's
        // dictionary key and become impossible to Unregister, leaving
        // a phantom Running row in the dropdown forever.
        var tracker = new RunActivityTracker();
        var fired = 0;
        tracker.RunningChanged += () => fired++;

        var rec = new RunRecord { Id = "", BackupId = "b1" };
        tracker.Register(rec);

        Assert.Empty(tracker.GetRunningForBackup("b1"));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Unregister_UnknownId_IsNoOp()
    {
        var tracker = new RunActivityTracker();
        var fired = 0;
        tracker.RunningChanged += () => fired++;
        tracker.Unregister("never-registered");
        // Removed-from-empty-dict still fires the event in the current
        // implementation; that's acceptable because it's a cheap
        // dropdown re-render. The contract this test pins is "doesn't
        // throw and doesn't materialize an entry".
        Assert.Empty(tracker.GetRunningForBackup("anything"));
    }

    private static RunRecord MakeRunning(string backupId, string backupName, RunKind kind) => new()
    {
        BackupId   = backupId,
        BackupName = backupName,
        Kind       = kind,
        StartedUtc = DateTime.UtcNow,
        Status     = BackupRunStatus.Running,
        Summary    = "Running…",
    };
}
