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
/// Backup card with several destinations (4) — exercises the
/// destination-pill wrapping logic in the BackupCard. The card
/// should grow vertically as the pill row wraps, never overflow
/// horizontally, and keep the right-side action strip aligned
/// with the card title rather than drifting down.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S17_MultiDestinationCard : ScenarioBase
{
    protected override string ScenarioName => "S17-multi-destination-card";

    [AvaloniaFact] public async Task Run_S17() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var d1 = new Destination { Name = "Local — primary",   Kind = DestinationKind.LocalFolder,      PathOrSubpath = ctx.MakeLocalDestination("p") };
        var d2 = new Destination { Name = "External — passport", Kind = DestinationKind.ExternalDrive,  PathOrSubpath = ctx.MakeLocalDestination("e") };
        var d3 = new Destination { Name = "Dropbox — work",    Kind = DestinationKind.DropboxAppScoped, PathOrSubpath = "store-1" };
        var d4 = new Destination { Name = "S3 — Cloudflare R2", Kind = DestinationKind.S3Compatible,    PathOrSubpath = "prefix",
                                   S3Endpoint = "abc.r2.cloudflarestorage.com", S3Bucket = "bucket" };
        var backup = new Backup
        {
            Name = "BigFanout",
            SourcePaths = { ctx.MakeSource("docs", seed: 1, targetBytes: 200_000) },
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "4 cells · in 2m 14s",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-30),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = d1.Id, StorageName = "p" });
        backup.Targets.Add(new BackupTarget { DestinationId = d2.Id, StorageName = "e" });
        backup.Targets.Add(new BackupTarget { DestinationId = d3.Id, StorageName = "drop" });
        backup.Targets.Add(new BackupTarget { DestinationId = d4.Id, StorageName = "s3"  });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(d1);
            cfg.Destinations.Add(d2);
            cfg.Destinations.Add(d3);
            cfg.Destinations.Add(d4);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "card 4 destinations wraps",
            "Card with 4 dest pills — wrapping is calm, action strip aligned right.");
        host.Close();
        return Task.CompletedTask;
    }
}
