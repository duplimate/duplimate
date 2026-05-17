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
/// Single-source restore revision picker. Hand-populates the
/// Revisions list with synthetic entries so the ListBox renders
/// in three states: loading-spinner, empty-state ("no revisions
/// yet"), and a full list with one row selected.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S14_RestoreSingleSourceRevisionPicker : ScenarioBase
{
    protected override string ScenarioName => "S14-restore-single-source-revisions";

    [AvaloniaFact] public async Task Run_S14() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "Local — primary",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var backup = new Backup
        {
            Name = "RevTest",
            SourcePaths = { ctx.MakeSource("source", seed: 1, targetBytes: 200_000) },
            LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddHours(-2),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var vm = new RestoreViewModel();
        vm.SelectedBackup = backup;
        vm.SourcesForBackup.Add(new SourcePickRow(backup.SourcePaths[0], vm, isSelected: true));
        vm.SelectedSourcePath = backup.SourcePaths[0];
        vm.MultiSourceMode = false;
        vm.DestinationsForBackup.Add(dest);
        vm.SelectedDestination = dest;

        var view = new RestoreView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);

        // 1: Loading spinner — Revisions empty + LoadingRevisions=true.
        vm.LoadingRevisions = true;
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "revisions loading",
            "Indeterminate progress bar visible; 'Looking this up…' label.");

        // 2: Empty result — finished loading, zero revisions.
        vm.LoadingRevisions = false;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "revisions empty hint",
            "'No revisions yet — run the backup once first' empty-state callout.");

        // 3: Full list — 5 synthetic revisions, latest selected by default.
        // Toggle LoadingRevisions on→off around the Add calls so the
        // OnLoadingRevisionsChanged partial fires the ShowNoRevisionsHint
        // notification chain, which is what the bound IsVisible
        // ultimately reads alongside the ListBox's own item template.
        vm.LoadingRevisions = true;
        for (int i = 5; i >= 1; i--)
            vm.Revisions.Add(new RevisionSummary(i,
                DateTime.UtcNow.AddDays(-(6 - i)), FileCount: 1234 + i * 12, TotalBytes: 1024L * 1024 * (12 + i)));
        vm.LoadingRevisions = false;
        vm.SelectedRevision = vm.Revisions[0];
        ctx.Driver.Pump();
        ctx.Snapshot(host, "revisions list 5 rows",
            "Five revisions, latest pre-selected, Next: pick files enabled.");

        host.Close();
        return Task.CompletedTask;
    }
}
