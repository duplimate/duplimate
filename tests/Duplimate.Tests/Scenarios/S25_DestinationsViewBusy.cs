using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Destinations view with several entries already configured —
/// covers the "list rendering with mixed Kinds" path. Mirrors the
/// kinds row a heavy user is likely to accumulate (one local, one
/// external, one Dropbox, one S3) so the per-kind icon + path
/// formatting both render in the same screenshot for comparison.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S25_DestinationsViewBusy : ScenarioBase
{
    protected override string ScenarioName => "S25-destinations-view-busy";

    [AvaloniaFact] public async Task Run_S25() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(new Destination { Name = "Local — backup-dir",      Kind = DestinationKind.LocalFolder,      PathOrSubpath = ctx.MakeLocalDestination("local") });
            cfg.Destinations.Add(new Destination { Name = "External — Toshiba 4TB",  Kind = DestinationKind.ExternalDrive,    PathOrSubpath = @"M:\Backup\Duplicacy", ExpectedDriveLetter = "M:", ExpectedVolumeLabel = "Toshiba 4TB" });
            cfg.Destinations.Add(new Destination { Name = "Network — \\\\nas\\backup", Kind = DestinationKind.NetworkShare,   PathOrSubpath = @"\\nas\backup\duplimate" });
            cfg.Destinations.Add(new Destination { Name = "Dropbox — personal",      Kind = DestinationKind.DropboxAppScoped, PathOrSubpath = "duplimate-personal" });
            cfg.Destinations.Add(new Destination { Name = "Dropbox — work",          Kind = DestinationKind.DropboxFullAccess, PathOrSubpath = "Apps/Duplicacy" });
            cfg.Destinations.Add(new Destination { Name = "OneDrive — personal",     Kind = DestinationKind.OneDrivePersonal, PathOrSubpath = "Apps/Duplicacy" });
            cfg.Destinations.Add(new Destination { Name = "OneDrive — business",     Kind = DestinationKind.OneDriveBusiness, PathOrSubpath = "Apps/Duplicacy" });
            cfg.Destinations.Add(new Destination { Name = "Google Drive",            Kind = DestinationKind.GoogleDrive,      PathOrSubpath = "Apps/Duplicacy" });
            cfg.Destinations.Add(new Destination { Name = "S3 — Cloudflare R2",      Kind = DestinationKind.S3Compatible,
                                                   S3Endpoint = "abc.r2.cloudflarestorage.com", S3Bucket = "backups",
                                                   PathOrSubpath = "primary" });
        });

        var view = new DestinationsView { DataContext = new DestinationsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "destinations all kinds",
            "9 destinations spanning every Kind — per-kind icons, names, paths align cleanly.");
        host.Close();
        return Task.CompletedTask;
    }
}
