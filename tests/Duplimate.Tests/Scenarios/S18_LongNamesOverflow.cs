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
/// Stress the layout with adversarially long strings — long backup
/// name, long source path, long destination name + path. Common
/// regression source: a TextBlock that lacks TextTrimming swallows
/// the right edge of the card, or wraps onto a new line and pushes
/// the action strip off-screen. This snapshot is the layout-stress
/// regression checkpoint.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S18_LongNamesOverflow : ScenarioBase
{
    protected override string ScenarioName => "S18-long-names-overflow";

    [AvaloniaFact] public async Task Run_S18() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "External — Toshiba Canvio Basics 4TB Portable Hard Drive (Black) — slot B",
            Kind = DestinationKind.ExternalDrive,
            PathOrSubpath = @"M:\Backups\Duplicacy Storage\duplimate-store-2026\nested\deeply\with\many\levels",
        };
        var backup = new Backup
        {
            Name = "Photography Archive — Full RAW + Lightroom catalogues — 2014→2026",
            SourcePaths =
            {
                @"D:\Photography\2014-2026 — Lightroom Catalogues + RAW masters\Master collection (Canon R5 + Sony α7 IV)",
                @"D:\Photography\2014-2026 — Lightroom Catalogues + RAW masters\Tethered Captures — overflow folder",
            },
            LastRunStatus = BackupRunStatus.Warning,
            LastRunSummary = "1 file skipped (locked by Lightroom) · in 12m 47s with very long summary text that should ellipsis cleanly somewhere",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-15),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "card long backup name source path",
            "Long backup name should ellipsis; sources should ellipsis; action strip stays right-aligned.");
        host.Close();
        return Task.CompletedTask;
    }
}
