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
/// Restore wizard step 4 — pick files. Three states snapshot:
///   • Empty file list (after a load that returned zero files —
///     covers the dim placeholder text below the search box).
///   • Populated with ~30 synthetic rows, none selected.
///   • Same list with half checked + a search filter applied
///     (so the bottom-bar selected-count text re-renders).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S15_RestoreFilesStep : ScenarioBase
{
    protected override string ScenarioName => "S15-restore-files-step";

    [AvaloniaFact] public async Task Run_S15() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("d") };
        var backup = new Backup { Name = "FilesTest", SourcePaths = { ctx.MakeSource("src", seed: 1, targetBytes: 100_000) } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var vm = new RestoreViewModel
        {
            SelectedBackup = backup,
            SelectedDestination = dest,
            SelectedSourcePath = backup.SourcePaths[0],
            MultiSourceMode = false,
        };
        vm.SourcesForBackup.Add(new SourcePickRow(backup.SourcePaths[0], vm, isSelected: true));
        vm.DestinationsForBackup.Add(dest);
        vm.SelectedRevision = new RevisionSummary(1, DateTime.UtcNow.AddHours(-1), 30, 0);

        var view = new RestoreView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);

        // Empty.
        vm.CurrentStep = RestoreViewModel.Step.PickFiles;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "files step empty",
            "Empty file list (synthetic) — Select all / Clear visible.");

        // 30 synthetic file rows.
        var dirs = new[] { "Documents", "Photos", "Music", "Code/duplimate", "Code/notes-app" };
        var exts = new[] { "txt", "md", "jpg", "png", "csv" };
        var rng  = new Random(1);
        for (int i = 0; i < 30; i++)
        {
            var p = $"{dirs[i % dirs.Length]}/file_{i:D3}.{exts[i % exts.Length]}";
            vm.AllFiles.Add(new RevisionFileRow(
                new RevisionFile(p, SizeBytes: rng.Next(1024, 5_000_000), ModifiedUtc: DateTime.UtcNow.AddDays(-i)), vm));
        }
        // Trigger a filter pass so FilteredFiles populates.
        vm.FileFilter = "";
        ctx.Driver.Pump();
        ctx.Snapshot(host, "files step populated none-selected",
            "30 file rows visible; bottom strip shows '0 files · 0 B selected'.");

        // Half checked + a search filter. Tests selected-count
        // recompute and the search-narrow path.
        for (int i = 0; i < vm.AllFiles.Count; i += 2) vm.AllFiles[i].IsSelected = true;
        vm.RecomputeSelectionTotals();
        vm.FileFilter = "Photos";
        ctx.Driver.Pump();
        ctx.Snapshot(host, "files step half-selected with-filter",
            "Search narrowed to 'Photos'; selected-count reflects ALL ticked rows, not just visible.");

        host.Close();
        return Task.CompletedTask;
    }
}
