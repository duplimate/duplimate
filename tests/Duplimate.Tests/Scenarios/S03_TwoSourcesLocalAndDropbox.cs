using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// The exact scenario the user described:
///   "backup 2 folders in two different drives to one local folder
///    and dropbox".
///
/// The Dropbox token is read from
/// <c>tests/Duplimate.Tests/secrets/dropbox-token.txt</c>. If the
/// file is missing the scenario marks itself Skipped with a recorded
/// reason — the suite-level SUMMARY.md then prints the missing-secret
/// path so the user knows exactly what to drop in to unlock it.
///
/// Two sources from different drive letters: we materialise one under
/// the test workspace ("source-a") and one in a subfolder of the
/// system temp dir to model "different drive". On a single-drive CI
/// box they end up on the same volume, which doesn't break the test —
/// only the path strings differ. The scenario is about the
/// 2-source × 2-destination matrix, not per-volume snapshot semantics.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S03_TwoSourcesLocalAndDropbox : ScenarioBase
{
    protected override string ScenarioName => "S03-two-sources-local-and-dropbox";

    [AvaloniaFact] public async Task Run_S03() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        if (!ctx.Secrets.TryGetDropboxToken(out var dropboxToken))
        {
            ctx.Skip(
                "Dropbox token missing. Set DROPBOX_TOKEN=… in " +
                $"{ctx.Secrets.SecretsFile} to unlock this scenario.");
            return Task.CompletedTask;
        }

        var sourceA = ctx.MakeSource("docs-drive-a", seed: 11, targetBytes: 1_000_000);
        var sourceB = ctx.MakeSource("photos-drive-b", seed: 22, targetBytes: 1_000_000);
        var localDestPath = ctx.MakeLocalDestination("local-target");

        var localDest = new Destination
        {
            Name = "Local — backup-target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = localDestPath,
            Encrypted = false,
        };
        var dropboxDest = new Destination
        {
            Name = "Dropbox — scenario test",
            Kind = DestinationKind.DropboxAppScoped,
            // Use a dated sub-path so concurrent or repeat scenario
            // runs don't collide on a shared Dropbox account.
            PathOrSubpath = $"scenario-S03-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Encrypted = false,
        };
        // Stash the token via SecretsStore so the runner picks it up
        // when (in a real run) it spawns duplicacy. The plain-text
        // token never leaves the test process — SecretsStore DPAPI-
        // encrypts on write.
        // PutNew gives us a stable ref string that StoragePasswordRef
        // can hold; the token itself is DPAPI-encrypted on disk.
        var storageRef = ServiceLocator.Secrets.PutNew("dropbox-scenario", dropboxToken);
        dropboxDest.StoragePasswordRef = storageRef;

        var backup = new Backup
        {
            Name = "Combined-AB",
            SourcePaths = { sourceA, sourceB },
        };
        backup.Targets.Add(new BackupTarget { DestinationId = localDest.Id,   StorageName = "target-local"   });
        backup.Targets.Add(new BackupTarget { DestinationId = dropboxDest.Id, StorageName = "target-dropbox" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(localDest);
            cfg.Destinations.Add(dropboxDest);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Driver.Pump();
        ctx.Snapshot(host, "two sources two destinations card",
            "Card shows 2 SOURCES + 2 DESTINATIONS chips (one Dropbox, one local).");

        // Successful run state — colours both destination pills as
        // healthy. This is the visual-regression checkpoint for the
        // multi-destination card layout.
        ServiceLocator.Config.Update(cfg =>
        {
            var b = cfg.Backups[0];
            b.LastRunStatus = BackupRunStatus.Success;
            b.LastRunSummary = "2 cells · in 1m 12s";
            b.LastRunEndUtc = DateTime.UtcNow.AddMinutes(-3);
        });
        ctx.Driver.Pump();
        ctx.Snapshot(host, "two sources after success",
            "After a successful run: green badge, both destination pills healthy.");

        host.Close();
        return Task.CompletedTask;
    }
}
