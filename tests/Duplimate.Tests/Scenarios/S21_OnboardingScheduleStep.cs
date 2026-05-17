using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// The onboarding wizard's schedule step. We seed source + a
/// destination so the VM passes its source/destination gates,
/// then jump to step 3 directly. Frequency dropdown, time picker,
/// and the battery / network checkboxes are the layout-sensitive
/// bits this snapshot tracks.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S21_OnboardingScheduleStep : ScenarioBase
{
    protected override string ScenarioName => "S21-onboarding-schedule";

    [AvaloniaFact] public async Task Run_S21() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(new Destination
        {
            Name = "Local — sample",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("seed"),
        }));

        var win = new OnboardingWindow(seed: null, isFirstBackup: true) { Width = 900, Height = 760 };
        ctx.Driver.Show(win);

        if (win.DataContext is OnboardingViewModel vm)
        {
            vm.SourceChips.Add(new SourceChipVm(ctx.MakeSource("seed", seed: 1, targetBytes: 100_000)));
            vm.CurrentStep = 3;
        }
        ctx.Driver.Pump();
        ctx.Snapshot(win, "onboarding step3 schedule",
            "Frequency / time-of-day / battery+network checkboxes.");
        win.Close();
        return Task.CompletedTask;
    }
}
