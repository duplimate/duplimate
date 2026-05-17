using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Type-to-confirm dialog used by every destructive action in the
/// app: delete backup, delete destination, restore-overwriting-
/// originals, etc. The blocking pattern is "type the literal name"
/// rather than a single yes/no so muscle-memory clicks can't
/// accidentally trigger irrecoverable actions. Snapshot two
/// example invocations so the dialog's copy + accent-coloured
/// confirm button stay visually in line with the rest of the app.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S10_DestructiveConfirmDialog : ScenarioBase
{
    protected override string ScenarioName => "S10-destructive-confirm-dialog";

    [AvaloniaFact] public async Task Run_S10() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Delete-a-backup wording.
        var dlg1 = new DestructiveConfirmWindow(
            title: "Delete backup «WorkingDocs»?",
            subtitle:
                "«WorkingDocs» will no longer run on schedule and will disappear from Duplimate. " +
                "The backup data already stored at your destinations stays where it is — " +
                "you can delete it from there separately if you want. Type the backup name to confirm.",
            confirmString: "WorkingDocs",
            caseInsensitive: true)
        { Width = 560, Height = 420 };
        ctx.Driver.Show(dlg1);
        ctx.Snapshot(dlg1, "confirm delete backup",
            "Type-to-confirm — backup name as the gate string.");
        dlg1.Close();

        // Restore-overwrite wording.
        var dlg2 = new DestructiveConfirmWindow(
            title: "Restore will overwrite the source folder",
            subtitle:
                "Files at C:\\Users\\me\\Documents will be replaced with their state " +
                "at revision 12. There's no undo. Type OVERWRITE to proceed.",
            confirmString: "OVERWRITE",
            caseInsensitive: true)
        { Width = 560, Height = 420 };
        ctx.Driver.Show(dlg2);
        ctx.Snapshot(dlg2, "confirm restore overwrite",
            "Restore-to-original-source guardrail — fixed OVERWRITE token.");
        dlg2.Close();

        return Task.CompletedTask;
    }
}
