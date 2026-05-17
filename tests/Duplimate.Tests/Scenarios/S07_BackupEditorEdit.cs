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
/// BackupEditor opened in "edit" mode against a backup that's
/// been running for a while: a name, two sources, two
/// destinations, a daily schedule, and a previous successful run.
/// The Schedule, Filters, Health-checks, and Run-history sections
/// inside the editor all matter visually — this snapshot is the
/// regression-tracking shot for any of them.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S07_BackupEditorEdit : ScenarioBase
{
    protected override string ScenarioName => "S07-backup-editor-edit";

    [AvaloniaFact] public async Task Run_S07() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var d1 = new Destination
        {
            Name = "Local — primary",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var d2 = new Destination
        {
            Name = "External — My Passport",
            Kind = DestinationKind.ExternalDrive,
            PathOrSubpath = ctx.MakeLocalDestination("passport"),
            ExpectedDriveLetter = "M:",
            ExpectedVolumeLabel = "My Passport",
        };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(d1);
            cfg.Destinations.Add(d2);
        });

        var existing = new Backup
        {
            Name = "WorkingDocs",
            SourcePaths =
            {
                ctx.MakeSource("docs",   seed: 1, targetBytes: 200_000),
                ctx.MakeSource("photos", seed: 2, targetBytes: 200_000),
            },
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "rev 18 · in 4m 12s",
            LastRunEndUtc = DateTime.UtcNow.AddHours(-6),
            Schedule = new BackupSchedule
            {
                Frequency = ScheduleFrequency.Daily,
                TimeOfDay = new TimeSpan(20, 0, 0),
                CatchUpMissedRuns = true,
                SkipOnBattery = true,
                StopOnBattery = true,
                RequireNetwork = true,
            },
        };
        existing.Targets.Add(new BackupTarget { DestinationId = d1.Id, StorageName = "default" });
        existing.Targets.Add(new BackupTarget { DestinationId = d2.Id, StorageName = "passport" });

        var vm = new BackupEditorViewModel(existing, isNew: false);
        var win = new BackupEditorWindow { DataContext = vm, Width = 980, Height = 820 };
        ctx.Driver.Show(win);
        ctx.Snapshot(win, "editor edit-existing populated",
            "Edit mode: schedule + battery options + 2 sources + 2 destinations.");

        win.Close();
        return Task.CompletedTask;
    }
}
