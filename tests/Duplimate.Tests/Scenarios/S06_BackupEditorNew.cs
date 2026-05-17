using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// BackupEditor opened in "new" mode. Snapshot the empty form,
/// then snapshot it with a name + source seeded so the validation
/// + Save button states are visible.
///
/// Why bother: the editor is the single most-touched dialog in the
/// app — every field it surfaces is a touch-point for layout
/// regressions. A blank-form + populated-form pair caught the
/// previous "Add disabled-with-no-reason" bug as soon as it
/// regressed.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S06_BackupEditorNew : ScenarioBase
{
    protected override string ScenarioName => "S06-backup-editor-new";

    [AvaloniaFact] public async Task Run_S06() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Seed at least one destination so the destination picker
        // inside the editor has something to render.
        var dest = new Destination
        {
            Name = "Local — sample-target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("sample"),
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

        // Empty form — what the user sees the first time they
        // click "Add a backup". Save should be disabled (no name,
        // no source).
        var blankVm = new BackupEditorViewModel(new Backup(), isNew: true);
        var blankWin = new BackupEditorWindow { DataContext = blankVm, Width = 980, Height = 820 };
        ctx.Driver.Show(blankWin);
        ctx.Snapshot(blankWin, "editor blank form",
            "New BackupEditor — no name, no sources. Save disabled.");

        // Now seed the form so the Save / Save-and-Run buttons go
        // live. Tests the binding pipeline that gates the primary
        // action on (name + ≥1 source + ≥1 target).
        var sourcePath = ctx.MakeSource("editor-seed", seed: 7, targetBytes: 200_000);
        var seeded = new Backup { Name = "MyBackup", SourcePaths = { sourcePath } };
        seeded.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        var seededVm = new BackupEditorViewModel(seeded, isNew: true);
        var seededWin = new BackupEditorWindow { DataContext = seededVm, Width = 980, Height = 820 };
        ctx.Driver.Show(seededWin);
        ctx.Snapshot(seededWin, "editor populated",
            "After name+source+destination — Save enabled, Save-and-Run visible.");

        blankWin.Close();
        seededWin.Close();
        return Task.CompletedTask;
    }
}
