using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Filesystem tree-picker dialog used by the BackupEditor + the
/// onboarding wizard when adding source folders. The headless
/// platform doesn't expose real drives, so the picker tree is
/// mostly empty in this snapshot — the value is in capturing the
/// dialog chrome (header, search, OK/Cancel button placement) so
/// a future redesign of any of those is regression-tracked.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S24_SourceTreePicker : ScenarioBase
{
    protected override string ScenarioName => "S24-source-tree-picker";

    [AvaloniaFact] public async Task Run_S24() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var win = new SourceTreePickerWindow(System.Array.Empty<string>())
        {
            Width = 820,
            Height = 720,
        };
        ctx.Driver.Show(win);
        ctx.Snapshot(win, "source tree picker chrome",
            "Tree-picker dialog: header, search box, drive list, OK / Cancel.");
        win.Close();
        return Task.CompletedTask;
    }
}
