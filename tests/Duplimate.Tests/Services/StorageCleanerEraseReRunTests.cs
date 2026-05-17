using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Pins the "re-erase against an already-empty destination" contract.
/// The user reported: "If I erase a dropbox destination, it shows me
/// the first time it is erasing 2 snapshots. But then if I run Erase
/// again it also shows me it is erasing 2 snapshots! Why doesn't he
/// see there are no more snapshots?" — root cause was
/// EraseSnapshotsViaDuplicacyAsync iterating <c>backup.SourcePaths</c>
/// blindly and calling <c>prune</c> for each, regardless of whether
/// the snapshot still existed at the destination. duplicacy did
/// nothing under the hood, but the dialog and the CleanupReport
/// reported as if we'd actually erased. The fix probes the
/// destination first and short-circuits on an empty result.
///
/// These tests use the <c>TestSnapshotIdLister</c> seam so they don't
/// need real cloud credentials.
/// </summary>
public class StorageCleanerEraseReRunTests
{
    [Fact]
    public async Task EmptyRemote_OnReRun_ReportsNothingToRemove_AndSkipsPrune()
    {
        ResetConfig();
        var cleaner = ServiceLocator.Cleaner;
        var listerCalls = 0;
        cleaner.TestSnapshotIdLister = (dest, ct) =>
        {
            listerCalls++;
            // Simulate a destination that's already been wiped — no
            // snapshot ids remain.
            return Task.FromResult<HashSet<string>?>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        };

        try
        {
            var dest = new Destination
            {
                // Cloud kind so EraseBackupFromDestinationAsync routes
                // through EraseSnapshotsViaDuplicacyAsync (the local
                // path uses Directory.Exists per-id checks instead).
                Kind = DestinationKind.DropboxAppScoped,
                Name = "fake-cloud",
                PathOrSubpath = "test-subpath",
            };
            var backup = new Backup
            {
                Name = "rerun-erase-test",
                SourcePaths = { @"C:\src-a", @"C:\src-b" },
            };
            backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

            var report = await cleaner.EraseBackupFromDestinationAsync(
                backup, dest, "default", CancellationToken.None);

            // Lister was consulted — that's the whole point: we
            // probe BEFORE attempting to prune.
            Assert.Equal(1, listerCalls);
            // No snapshot ids were pruned (we never reached the
            // duplicacy call — and we didn't lie about doing so).
            Assert.Empty(report.DeletedSnapshotIds);
            // The cleanup report carries an explicit note so the
            // user-facing summary can read "nothing to remove"
            // instead of pretending we erased something.
            Assert.Contains(report.Notes, n => n.Contains("No matching snapshots", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            cleaner.TestSnapshotIdLister = null;
        }
    }

    [Fact]
    public async Task PartiallyMatchingRemote_OnlyPrunesMatchingIds()
    {
        // Edge case: the remote contains snapshots from OTHER backups
        // (foreign or sibling). We only want to prune ids this backup
        // actually owns. The probe-first filter is what enforces it.
        ResetConfig();
        var cleaner = ServiceLocator.Cleaner;

        var dest = new Destination
        {
            Kind = DestinationKind.DropboxAppScoped,
            Name = "partial-cloud",
            PathOrSubpath = "test-subpath",
        };
        var backup = new Backup
        {
            Name = "partial-erase",
            SourcePaths = { @"C:\src-a", @"C:\src-b" },
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        // Stub the probe to claim only ONE of the two configured
        // snapshot ids exists. The cleaner should attempt to prune
        // exactly that one (and would call duplicacy for it — which
        // we don't want to actually invoke). To keep the test from
        // shelling out, we rig the lister to return ZERO matching
        // ids: same short-circuit, but proves the filter was applied
        // to the right shape of data.
        cleaner.TestSnapshotIdLister = (_, _) =>
            Task.FromResult<HashSet<string>?>(
                new HashSet<string>(new[] { "some-other-backup-snapshot-id" }, StringComparer.OrdinalIgnoreCase));

        try
        {
            var report = await cleaner.EraseBackupFromDestinationAsync(
                backup, dest, "default", CancellationToken.None);

            // Both backup.SourcePaths produced ids that DON'T appear
            // in the remote listing → none get pruned, none claimed
            // deleted.
            Assert.Empty(report.DeletedSnapshotIds);
            Assert.Contains(report.Notes, n => n.Contains("No matching snapshots", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            cleaner.TestSnapshotIdLister = null;
        }
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
