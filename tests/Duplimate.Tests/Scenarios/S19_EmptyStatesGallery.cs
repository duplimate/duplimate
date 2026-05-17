using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Side-by-side empty-state gallery so every primary view's "fresh
/// install, nothing configured" copy stays consistent in tone +
/// layout. The previous app had divergent empty-state styles per
/// view; the unified Border.empty-state class is what every view
/// now uses, and this scenario is the regression-tracking shot.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S19_EmptyStatesGallery : ScenarioBase
{
    protected override string ScenarioName => "S19-empty-states-gallery";

    [AvaloniaFact] public async Task Run_S19() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // ScenarioContext already cleared the config — every view
        // we render here lands on its empty-state branch.
        Cap(ctx, new BackupsView      { DataContext = new BackupsViewModel()      }, "backups empty");
        Cap(ctx, new DestinationsView { DataContext = new DestinationsViewModel() }, "destinations empty");
        Cap(ctx, new LogsView         { DataContext = new LogsViewModel()         }, "logs empty");
        Cap(ctx, new RestoreView      { DataContext = new RestoreViewModel()      }, "restore empty");
        return Task.CompletedTask;
    }

    private static void Cap(ScenarioContext ctx, Avalonia.Controls.Control view, string label)
    {
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, label, "Empty-state callout + 'go set things up' CTA.");
        host.Close();
    }
}
