using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// The two password-related dialogs that pop up around encrypted
/// destinations: the generated-password reveal that fires when a
/// user encrypts a brand-new destination, and the change-password
/// dialog used to rotate an existing storage's password.
///
/// These are short-lived modals — easy to overlook when the user-
/// facing message gets out of sync with the actual cryptographic
/// behaviour. The snapshots pin the copy + button labels so a
/// future "we forgot to update the warning text" regression
/// shows up.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S09_PasswordDialogs : ScenarioBase
{
    protected override string ScenarioName => "S09-password-dialogs";

    [AvaloniaFact] public async Task Run_S09() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Newly-generated password — the user just encrypted a
        // destination. The dialog's job is to make them write
        // the password down somewhere they can find it later
        // (we cannot recover it).
        var win1 = new GeneratedPasswordWindow("correct-horse-battery-staple") { Width = 640, Height = 460 };
        ctx.Driver.Show(win1);
        ctx.Snapshot(win1, "generated password reveal",
            "After encrypting a new destination — the only chance to copy the password.");
        win1.Close();

        // Change-password flow surfaces an old-password input + new
        // input + confirmation. Snapshot so the layout's symmetry
        // (label width, field width, helper text alignment) is
        // tracked.
        var win2 = new ChangeStoragePasswordWindow { Width = 640, Height = 520 };
        ctx.Driver.Show(win2);
        ctx.Snapshot(win2, "change storage password",
            "Rotate password on an encrypted destination — old + new + confirm.");
        win2.Close();

        return Task.CompletedTask;
    }
}
