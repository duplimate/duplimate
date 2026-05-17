using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Settings view in its full populated state. Each accent variant
/// merges its own resource dictionary at runtime (Accent_Ocean,
/// Accent_Plum, etc.) — the snapshot-per-accent matrix below is a
/// quick way to spot any accent that breaks contrast against the
/// cream background or that doesn't propagate to its hover/tint
/// derivatives.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S12_SettingsAndAccents : ScenarioBase
{
    protected override string ScenarioName => "S12-settings-and-accents";

    [AvaloniaFact] public async Task Run_S12() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var vm = new SettingsViewModel();
        var view = new SettingsView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "settings default ocean",
            "Settings — Appearance, Logs & diagnostics, Notifications etc. Default Ocean accent.");

        // Cycle every accent variant — Settings.Accent flips the
        // app-wide resource dictionary at runtime, so the snapshot
        // captures whether the new accent's tint/hover derivatives
        // actually reach the bound controls.
        foreach (var accent in vm.AvailableAccents)
        {
            vm.Accent = accent;
            ctx.Driver.Pump();
            ctx.Snapshot(host, $"settings accent {accent}",
                $"Accent={accent} — primary buttons + active nav + status highlights tinted.");
        }

        host.Close();
        return Task.CompletedTask;
    }
}
