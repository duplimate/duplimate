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
/// State matrix for the health-summary banner on the Backups
/// landing page. Every shape the user might land on:
///   • Everything healthy (no banner)
///   • One backup never run (idle)
///   • One backup failed (red, single-pill)
///   • One backup with warnings (orange)
///   • One backup skipped (orange, skipped pill)
///   • Mixed: 1 fail + 1 warn + 1 skip + 2 ok (multi-pill row)
/// Snapshot per state so any tone-mapping or pill-layout regression
/// surfaces immediately.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S13_HealthBannerStateMatrix : ScenarioBase
{
    protected override string ScenarioName => "S13-health-banner-state-matrix";

    [AvaloniaFact] public async Task Run_S13() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "Local — backup-dir",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("seed"),
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);

        // 1: All-OK — banner says "Everything looks good", no pills.
        SeedBackups(dest, ("AlphaOk", BackupRunStatus.Success), ("BetaOk", BackupRunStatus.Success));
        ctx.Driver.Pump();
        ctx.Snapshot(host, "banner all ok",
            "Two healthy backups → 'Everything looks good', no attention pills.");

        // 2: Never-run idle — single grey pill.
        SeedBackups(dest, ("FreshlyAdded", BackupRunStatus.NeverRun));
        ctx.Driver.Pump();
        ctx.Snapshot(host, "banner idle never-run",
            "Single never-run backup → idle banner with grey pill.");

        // 3: Failed — red pill.
        SeedBackups(dest, ("Photos", BackupRunStatus.Failed));
        ctx.Driver.Pump();
        ctx.Snapshot(host, "banner fail red",
            "Single failed backup → red banner with red attention pill.");

        // 4: Warning — orange pill.
        SeedBackups(dest, ("Documents", BackupRunStatus.Warning));
        ctx.Driver.Pump();
        ctx.Snapshot(host, "banner warn orange",
            "Single warning backup → orange banner.");

        // 5: Skipped — orange skip-glyph pill.
        SeedBackups(dest, ("Music", BackupRunStatus.Skipped));
        ctx.Driver.Pump();
        ctx.Snapshot(host, "banner skipped orange",
            "Single skipped backup → orange banner with skip glyph.");

        // 6: Mix — fail + warn + skip + 2 ok. Headline summarises
        // counts; the pill row carries the actual names.
        SeedBackups(dest,
            ("Photos",     BackupRunStatus.Failed),
            ("Documents",  BackupRunStatus.Warning),
            ("Music",      BackupRunStatus.Skipped),
            ("Code",       BackupRunStatus.Success),
            ("Notes",      BackupRunStatus.Success));
        ctx.Driver.Pump();
        ctx.Snapshot(host, "banner mixed fail-warn-skip-ok",
            "Fail+warn+skip with 2 ok in detail line — multi-pill row.");

        host.Close();
        return Task.CompletedTask;
    }

    private static void SeedBackups(Destination dest, params (string Name, BackupRunStatus Status)[] backups)
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            int i = 0;
            foreach (var (name, status) in backups)
            {
                var b = new Backup
                {
                    Name = name,
                    SourcePaths = { @"C:\some\path\" + name.ToLowerInvariant() },
                    LastRunStatus = status,
                    LastRunSummary = status == BackupRunStatus.NeverRun ? "" : "rev 1 · in 4s",
                    LastRunEndUtc = status == BackupRunStatus.NeverRun ? null : DateTime.UtcNow.AddMinutes(-(i + 1) * 7),
                };
                b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = name });
                cfg.Backups.Add(b);
                i++;
            }
        });
    }
}
