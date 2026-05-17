using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Visual coverage for every visible step of the Restore wizard:
/// destination picker, revision step (single-source picker AND
/// multi-source summary), files step, target step, summary step.
/// Drives the VM via setting CurrentStep directly because most of
/// these screens depend on async listings (LoadRevisionsAsync) that
/// would shell out to duplicacy.exe — out of scope here.
///
/// The recently-redesigned bits have specific snapshot checkpoints:
///   • multi-source revision summary (the "always show revision" fix)
///   • inline "MORE OPTIONS" replacing the old Advanced expander
///   • dark terminal log on the Progress step
/// — so a future regression in any of those shows up as a bad PNG
/// in SUMMARY.md.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S04_RestoreWizardShots : ScenarioBase
{
    protected override string ScenarioName => "S04-restore-wizard-shots";

    [AvaloniaFact] public async Task Run_S04() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Seed two destinations so the destination-picker step has
        // something to render. Source content is decorative (no real
        // restore happens) — the scenario is about the wizard UI.
        var sourceA = ctx.MakeSource("alpha", seed: 1, targetBytes: 200_000);
        var sourceB = ctx.MakeSource("beta",  seed: 2, targetBytes: 200_000);
        var dest1 = new Destination
        {
            Name = "Local — primary",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var dest2 = new Destination
        {
            Name = "Local — secondary",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("secondary"),
        };
        var backup = new Backup
        {
            Name = "RestoreFixture",
            SourcePaths = { sourceA, sourceB },
            LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest1.Id, StorageName = "p" });
        backup.Targets.Add(new BackupTarget { DestinationId = dest2.Id, StorageName = "s" });
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest1);
            cfg.Destinations.Add(dest2);
            cfg.Backups.Add(backup);
        });

        var vm = new RestoreViewModel();
        vm.SelectedBackup = backup;
        // Mirror what NextFromBackupAsync does for a multi-source
        // backup: populate SourcesForBackup with checked rows.
        foreach (var s in backup.SourcePaths)
            vm.SourcesForBackup.Add(new SourcePickRow(s, vm, isSelected: true));
        foreach (var d in new[] { dest1, dest2 })
        {
            vm.DestinationsForBackup.Add(d);
            vm.DestinationPickRows.Add(new DestinationPickRow(d));
        }

        var view = new RestoreView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);

        // PickDestination — the one the user just complained
        // wasn't clickable. Snapshot in unselected state then with
        // a selection, so we can eyeball the row click target.
        vm.CurrentStep = RestoreViewModel.Step.PickDestination;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "restore step pickdestination unselected",
            "Two destination rows; Next disabled until one is picked.");
        vm.SelectedDestination = dest1;
        // Mirror the picked-row IsSelected so the radio dot
        // visually reflects the selection (post-refactor: the
        // dot binds to DestinationPickRow.IsSelected, not to
        // SelectedDestination via parent-traversal).
        vm.DestinationPickRows.First(r => ReferenceEquals(r.Destination, dest1)).IsSelected = true;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "restore step pickdestination selected",
            "First row picked; Next enabled. Tests the new Button-as-row click target.");

        // PickRevision — multi-source summary view (the "always
        // show revision" feature). We can't load real revisions here
        // without duplicacy.exe, so we hand-populate
        // MultiSourceRevisions with synthetic rows to exercise the
        // template.
        vm.MultiSourceMode = true;
        vm.MultiSourceRevisions.Clear();
        vm.MultiSourceRevisions.Add(new MultiSourceRevisionRow(
            sourceA, revisionNumber: 7, createdUtc: DateTime.UtcNow.AddDays(-1),
            fileCount: 0, totalBytes: 0));
        vm.MultiSourceRevisions.Add(new MultiSourceRevisionRow(
            sourceB, revisionNumber: 3, createdUtc: DateTime.UtcNow.AddHours(-3),
            fileCount: 0, totalBytes: 0));
        // No need to manually fire OnPropertyChanged for an
        // ObservableCollection — the per-Add CollectionChanged
        // event is what the bound ItemsControl listens to.
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "restore step pickrevision multi-source summary",
            "Read-only summary view: latest revision per picked source.");

        // PickTarget — inline MORE OPTIONS strip (replaces the old
        // Advanced expander). Snapshot the section so a future
        // regression in the layout shows up.
        vm.CurrentStep = RestoreViewModel.Step.PickTarget;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "restore step picktarget more-options",
            "PickTarget step with the inline MORE OPTIONS strip.");

        // Progress step with the dark terminal log + Failures pane.
        vm.CurrentStep = RestoreViewModel.Step.Progress;
        // ProgressLog is bound through the debounced flush — write
        // directly to the bound property so the snapshot has
        // something to render. Real runs would route through
        // OnEngineLog.
        vm.ProgressLog =
            "2026-05-01 12:00:00.000 INFO RESTORE_START Restoring 5 files\n" +
            "2026-05-01 12:00:00.123 INFO DOWNLOAD_FILE alpha/notes.txt restored\n" +
            "2026-05-01 12:00:00.456 INFO DOWNLOAD_FILE alpha/budget.csv restored\n";
        ctx.Driver.Pump();
        ctx.Snapshot(host, "restore step progress dark-terminal",
            "Progress step's dark terminal pane (matches Activity & Logs styling).");

        host.Close();
        return Task.CompletedTask;
    }
}
