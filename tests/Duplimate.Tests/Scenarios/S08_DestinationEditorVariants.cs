using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Duplimate.Models;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Walk through the Destination editor for each kind we support.
/// One screenshot per kind so a layout regression in any kind-
/// specific section (path browser for local, sub-path for cloud,
/// S3 endpoint+bucket+credentials, etc.) is visible at a glance.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S08_DestinationEditorVariants : ScenarioBase
{
    protected override string ScenarioName => "S08-destination-editor-variants";

    [AvaloniaFact] public async Task Run_S08() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // New / blank — Type dropdown empty (the recent UX change
        // "no silent LocalFolder pre-select").
        Open(ctx, new Destination(), isNew: true,    "blank new dropdown empty");
        Open(ctx, new Destination { Kind = DestinationKind.LocalFolder,       Encrypted = true },  isNew: false, "local folder encrypted");
        Open(ctx, new Destination { Kind = DestinationKind.ExternalDrive,     Encrypted = false }, isNew: false, "external drive");
        Open(ctx, new Destination { Kind = DestinationKind.NetworkShare,      Encrypted = true  }, isNew: false, "network share");
        Open(ctx, new Destination { Kind = DestinationKind.DropboxAppScoped,  Encrypted = true  }, isNew: false, "dropbox app-scoped");
        Open(ctx, new Destination { Kind = DestinationKind.OneDrivePersonal,  Encrypted = false }, isNew: false, "onedrive personal");
        Open(ctx, new Destination { Kind = DestinationKind.GoogleDrive,       Encrypted = true  }, isNew: false, "google drive");
        Open(ctx, new Destination { Kind = DestinationKind.S3Compatible,      Encrypted = false,
            S3Endpoint = "abc.r2.cloudflarestorage.com", S3Bucket = "my-backups",
            PathOrSubpath = "prefix" }, isNew: false, "s3 compatible");
        return Task.CompletedTask;
    }

    private void Open(ScenarioContext ctx, Destination dest, bool isNew, string label)
    {
        var vm = new DestinationEditorViewModel(dest, isNew);
        var win = new DestinationEditorWindow { DataContext = vm, Width = 760, Height = 760 };
        ctx.Driver.Show(win);
        ctx.Snapshot(win, label, $"Destination kind={dest.Kind}, isNew={isNew}, encrypted={dest.Encrypted}.");
        win.Close();
    }
}
