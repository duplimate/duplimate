using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Snapshots key views in BOTH light and dark themes. The app
/// supports runtime theme switching; this scenario flips
/// Application.Current.RequestedThemeVariant between Light and Dark
/// and re-renders the same view, so SUMMARY.md ends up with paired
/// PNGs the human can A/B-compare for contrast / colour-balance
/// regressions.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S05_LightDarkThemeShots : ScenarioBase
{
    protected override string ScenarioName => "S05-light-dark-theme-shots";

    [AvaloniaFact] public async Task Run_S05() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Seed a populated config so the snapshots are visually
        // interesting (status badges, destination pills, banner).
        var dest = new Destination
        {
            Name = "Local — backup-dir",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("seed-dest"),
        };
        var ok = new Backup
        {
            Name = "Documents",
            SourcePaths = { ctx.MakeSource("docs", seed: 1, targetBytes: 200_000) },
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "rev 12 · in 4s",
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
        };
        ok.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        var fail = new Backup
        {
            Name = "Photos",
            SourcePaths = { ctx.MakeSource("photos", seed: 2, targetBytes: 200_000) },
            LastRunStatus = BackupRunStatus.Failed,
            LastRunSummary = "Backup exited with code 100",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-10),
        };
        fail.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "photos" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(ok);
            cfg.Backups.Add(fail);
        });

        // Light theme snapshots.
        SetTheme(ThemeVariant.Light);
        SnapshotEachView(ctx, "light");

        // Dark theme snapshots.
        SetTheme(ThemeVariant.Dark);
        SnapshotEachView(ctx, "dark");

        // Restore default to avoid leaking the dark theme to
        // subsequent scenarios — the suite shares a process-wide
        // Application instance.
        SetTheme(ThemeVariant.Light);
        return Task.CompletedTask;
    }

    private static void SetTheme(ThemeVariant variant)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = variant;
    }

    private void SnapshotEachView(ScenarioContext ctx, string label)
    {
        // Backups (the landing page).
        var backups = new BackupsView { DataContext = new BackupsViewModel() };
        var host1 = new Window { Width = 1100, Height = 760, Content = backups };
        ctx.Driver.Show(host1);
        ctx.Snapshot(host1, $"backups populated {label}",
            "Status badges + health banner in the current theme.");
        host1.Close();

        // Destinations.
        var dests = new DestinationsView { DataContext = new DestinationsViewModel() };
        var host2 = new Window { Width = 1100, Height = 760, Content = dests };
        ctx.Driver.Show(host2);
        ctx.Snapshot(host2, $"destinations {label}", null);
        host2.Close();

        // Logs.
        var logs = new LogsView { DataContext = new LogsViewModel() };
        var host3 = new Window { Width = 1100, Height = 760, Content = logs };
        ctx.Driver.Show(host3);
        ctx.Snapshot(host3, $"logs {label}", null);
        host3.Close();

        // Settings.
        var settings = new SettingsView { DataContext = new SettingsViewModel() };
        var host4 = new Window { Width = 1100, Height = 760, Content = settings };
        ctx.Driver.Show(host4);
        ctx.Snapshot(host4, $"settings {label}", null);
        host4.Close();
    }
}
