using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// First-time-user shape: zero backups, zero destinations. Snapshot
/// the empty-state on the Backups page (the primary landing) and
/// every step of the OnboardingWindow wizard. Pure UI scenario — no
/// duplicacy.exe spawned, no live network calls — so this runs
/// everywhere and gives the visual auditor a baseline of "what does
/// the calm-document chrome look like before anything happens".
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S01_OnboardingFirstTime : ScenarioBase
{
    protected override string ScenarioName => "S01-onboarding-first-time";

    [AvaloniaFact] public async Task Run_S01() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Step 1: empty-state on the Backups page. The user lands here
        // after a clean install with nothing configured — the empty
        // callout is what nudges them into the onboarding flow.
        var backupsView = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = backupsView };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "backups empty-state",
            "First-run landing: 'Let's set up your first backup' callout, no banner, no cards.");

        // Step 2: open the onboarding wizard. Modeled on what
        // BackupsViewModel.AddNew does for an empty config — passing
        // isFirstBackup:true surfaces the welcome copy + larger
        // typography that the regular Add-a-backup flow doesn't show.
        var onboarding = new OnboardingWindow(seed: null, isFirstBackup: true)
        {
            Width = 900,
            Height = 760,
        };
        ctx.Driver.Show(onboarding);
        ctx.Snapshot(onboarding, "onboarding step1 sources",
            "Wizard step 1 — pick source folders. Welcome copy visible.");

        // Step 3: seed a source chip programmatically so step 2
        // (destination) renders with something on the back-button
        // path. Drives via the VM because the file-picker dialog
        // can't be opened in headless mode.
        if (onboarding.DataContext is OnboardingViewModel vm)
        {
            vm.SourceChips.Add(new SourceChipVm(ctx.MakeSource("seed-source", seed: 1, targetBytes: 200_000)));
            vm.CurrentStep = 2;
            ctx.Driver.Pump();
        }
        ctx.Snapshot(onboarding, "onboarding step2 destinations",
            "Wizard step 2 — pick or create a destination.");

        // Close the wizard cleanly so its Dispose runs and the
        // headless lifecycle reclaims the dispatcher slot.
        onboarding.Close();
        host.Close();
        return Task.CompletedTask;
    }
}
