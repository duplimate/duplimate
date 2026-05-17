using System;
using System.Linq;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins the invariant that <see cref="LogStore.AppendRun"/> stores
/// FRESH (deserialised) copies of every record in its in-memory
/// cache, not the caller's live reference. The user reported the
/// LogsView's terminal pane stuck on the previous run's content
/// after a fresh backup completed: root cause was AppendRun
/// inserting the orchestrator's still-mutating synthetic
/// "Running" record directly into the cache, leaving subsequent
/// LoadHistoryByBackupId callers with the same reference. When
/// LogsViewModel re-assigned <c>SelectedRun</c> to that "new"
/// entry, CommunityToolkit's reference-equality check skipped
/// the assignment and OnSelectedRunChanged never fired.
/// </summary>
public class LogStoreCacheTests
{
    [Fact]
    public void AppendRun_CachedCopy_IsNotTheCallerInstance()
    {
        ResetConfig();
        var store = ServiceLocator.Logs;

        var live = new RunRecord
        {
            BackupId = "log-cache-test",
            BackupName = "log-cache-test",
            StartedUtc = DateTime.UtcNow,
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success,
            Summary = "ok",
        };
        store.AppendRun(live);

        // Subsequent reads of the cache must return a different
        // instance — the cache should hold a deep copy round-tripped
        // through JSON, never the caller's live reference.
        var fromCache = store.LoadHistory(live.BackupName)
            .First(r => r.StartedUtc == live.StartedUtc);

        Assert.NotSame(live, fromCache);
        // Field equality is preserved.
        Assert.Equal(live.Status,     fromCache.Status);
        Assert.Equal(live.BackupId,   fromCache.BackupId);
        Assert.Equal(live.BackupName, fromCache.BackupName);
        Assert.Equal(live.Summary,    fromCache.Summary);
    }

    [Fact]
    public void AppendRun_LiveRefMutationsAfterAppend_DontLeakIntoCache()
    {
        // The actual scenario the user hit: orchestrator hands its
        // synthetic record to AppendRun, then continues mutating it
        // (Status flips Running→Success, EndedUtc gets stamped). If
        // the cache held a reference, those mutations would visibly
        // leak into the dropdown labels and into reference-equality
        // checks downstream. Pin that they DO NOT.
        ResetConfig();
        var store = ServiceLocator.Logs;

        var live = new RunRecord
        {
            BackupId = "mutation-test",
            BackupName = "mutation-test",
            StartedUtc = DateTime.UtcNow,
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success,
            Summary = "ok",
        };
        store.AppendRun(live);

        // After AppendRun, mutate the caller's live reference. A
        // shared cache reference would let this mutation bleed into
        // the cached entry the LogsViewModel reads — exactly the
        // bug.
        live.Status = BackupRunStatus.Failed;
        live.Summary = "MUTATED AFTER APPEND";

        var fromCache = store.LoadHistory(live.BackupName)
            .First(r => r.StartedUtc == live.StartedUtc);

        Assert.Equal(BackupRunStatus.Success, fromCache.Status);
        Assert.Equal("ok", fromCache.Summary);
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
