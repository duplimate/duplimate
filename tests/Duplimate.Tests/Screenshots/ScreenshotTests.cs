using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Screenshots;

/// <summary>
/// Renders each view and dialog to an actual PNG under
/// tests/Duplimate.Tests/artifacts/screenshots/. These aren't pass/fail
/// tests against golden images — they're artifacts a human (or Claude)
/// reads back to confirm the UX looks intended.
///
/// Running `dotnet test --filter "FullyQualifiedName~ScreenshotTests"`
/// regenerates every PNG; a reviewer then opens them and checks for
/// contrast, spacing, alignment, missing icons, etc. — the kinds of
/// issues bounds-based tests can't catch.
///
/// The tests use Avalonia.Skia which needs to be registered on the
/// AppBuilder (see HeadlessApp). Without Skia, RenderTargetBitmap.Save
/// produces an empty file.
/// </summary>
public class ScreenshotTests
{
    private static readonly string OutputDir = ResolveOutputDir();

    // Dashboard removed (merged into Backups). The empty/populated
    // screenshots that were previously named dashboard-* are now
    // covered by the Backups view's empty/populated shots — Backups
    // is the new landing page and includes the same health-summary
    // banner the old Dashboard rendered.
    [AvaloniaFact] public void Shot_BackupsView_Empty()     { ResetConfig(); Capture<BackupsView,      BackupsViewModel>("backups-empty"); }
    [AvaloniaFact] public void Shot_DestinationsView_Empty(){ ResetConfig(); Capture<DestinationsView, DestinationsViewModel>("destinations-empty"); }
    [AvaloniaFact] public void Shot_LogsView_Empty()        { ResetConfig(); Capture<LogsView,         LogsViewModel>("logs-empty"); }
    [AvaloniaFact] public void Shot_RestoreView_Empty()     { ResetConfig(); Capture<RestoreView,      RestoreViewModel>("restore-empty"); }
    [AvaloniaFact] public void Shot_SettingsView()          { ResetConfig(); Capture<SettingsView,     SettingsViewModel>("settings"); }

    [AvaloniaFact]
    public void Shot_BackupsView_Populated()
    {
        ResetConfig();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(new Destination
            {
                Name = "My Passport (external)",
                Kind = DestinationKind.ExternalDrive,
                PathOrSubpath = @"M:\Backup\Duplicacy",
            });
            cfg.Destinations.Add(new Destination
            {
                Name = "Dropbox — personal",
                Kind = DestinationKind.DropboxAppScoped,
                PathOrSubpath = "local_c",
            });
            cfg.Backups.Add(new Backup
            {
                Name = "local_c",
                SourcePaths = { @"C:\" },
                LastRunStatus = BackupRunStatus.Success,
                LastRunSummary = "42,120 files · 12.3 GB",
                LastRunEndUtc = DateTime.UtcNow.AddHours(-3),
                Targets = { new BackupTarget { DestinationId = cfg.Destinations[0].Id, StorageName = "default" } },
            });
            cfg.Backups.Add(new Backup
            {
                Name = "documents",
                SourcePaths = { @"C:\Users\me\Documents" },
                LastRunStatus = BackupRunStatus.Warning,
                LastRunSummary = "2 warnings (skipped: locked)",
                LastRunEndUtc = DateTime.UtcNow.AddHours(-26),
                Targets = { new BackupTarget { DestinationId = cfg.Destinations[1].Id, StorageName = "default" } },
            });
        });
        Capture<BackupsView, BackupsViewModel>("backups-populated");
    }

    // Shot_DashboardView_Populated removed — see comment above the
    // empty-shot deletion. The same fixture data is captured by
    // Shot_BackupsView_Populated.

    [AvaloniaFact]
    public void Shot_DestinationEditor_New()
    {
        // Brand-new destination — Type dropdown is empty (the whole point
        // of the recent UX change: no silent LocalFolder pre-select).
        var vm = new DestinationEditorViewModel(new Destination(), isNew: true);
        var window = new DestinationEditorWindow { DataContext = vm, Width = 740, Height = 760 };
        CaptureWindow(window, "editor-destination-new");
    }

    [AvaloniaFact]
    public void Shot_DestinationEditor_LocalFolder()
    {
        // Editing an existing local-folder destination. isNew:false tells
        // the VM to pre-select the saved Kind rather than blanking it.
        var vm = new DestinationEditorViewModel(
            new Destination { Kind = DestinationKind.LocalFolder, Encrypted = true }, isNew: false);
        var window = new DestinationEditorWindow { DataContext = vm, Width = 740, Height = 760 };
        CaptureWindow(window, "editor-destination-local");
    }

    [AvaloniaFact]
    public void Shot_DestinationEditor_Dropbox()
    {
        var vm = new DestinationEditorViewModel(
            new Destination { Kind = DestinationKind.DropboxAppScoped, Encrypted = true }, isNew: false);
        var window = new DestinationEditorWindow { DataContext = vm, Width = 740, Height = 760 };
        CaptureWindow(window, "editor-destination-dropbox");
    }

    [AvaloniaFact]
    public void Shot_DestinationEditor_Dropbox_EasyMode()
    {
        // Same Dropbox destination as the regular Dropbox shot, but
        // opened with simplifiedMode=true (Easy-mode entry point).
        // The Cloud subfolder field should NOT render — Easy-mode
        // users get the auto-fill path silently.
        var vm = new DestinationEditorViewModel(
            new Destination { Kind = DestinationKind.DropboxAppScoped, Encrypted = true },
            isNew: true, simplifiedMode: true);
        var window = new DestinationEditorWindow { DataContext = vm, Width = 820, Height = 780 };
        CaptureWindow(window, "editor-destination-dropbox-easymode");
    }

    [AvaloniaFact]
    public void Shot_DestinationEditor_LockedPassword()
    {
        // Encrypted destination that ALREADY has a saved password —
        // exercises StoragePasswordIsLocked = true, which renders the
        // disabled obfuscated TextBox + the Change password… button.
        // Without a non-empty StoragePasswordRef the lock predicate
        // doesn't fire and we'd render the editable case instead (see
        // Shot_DestinationEditor_LocalFolder above).
        var vm = new DestinationEditorViewModel(
            new Destination
            {
                Kind = DestinationKind.LocalFolder,
                PathOrSubpath = @"M:\Backup\Duplicacy",
                Encrypted = true,
                StoragePasswordRef = "dest:fixture:storagepw",
            },
            isNew: false);
        var window = new DestinationEditorWindow { DataContext = vm, Width = 820, Height = 780 };
        CaptureWindow(window, "editor-destination-locked-password");
    }

    [AvaloniaFact]
    public void Shot_BackupEditor()
    {
        var vm = new BackupEditorViewModel(new Backup { Name = "sample", SourcePaths = { @"C:\" } }, isNew: true);
        var window = new BackupEditorWindow { DataContext = vm, Width = 980, Height = 840 };
        CaptureWindow(window, "editor-backup");
    }

    [AvaloniaFact]
    public void Shot_BackupEditor_WithTargets()
    {
        // Capture the Advanced editor populated with multiple targets
        // so the destinations chip layout (Prune / Erase / Remove on
        // the right) is visible. Without seeded targets the chip
        // template never renders and we'd never catch a regression.
        ResetConfig();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(new Destination
            {
                Name = "Dropbox — personal",
                Kind = DestinationKind.DropboxAppScoped,
                PathOrSubpath = "duplicacy-store",
            });
            cfg.Destinations.Add(new Destination
            {
                Name = "Local — D:\\Backup",
                Kind = DestinationKind.LocalFolder,
                PathOrSubpath = @"D:\Backup",
            });
        });
        var cfgNow = ServiceLocator.Config.Current;
        var b = new Backup { Name = "sample", SourcePaths = { @"C:\" } };
        b.Targets.Add(new BackupTarget { DestinationId = cfgNow.Destinations[0].Id, StorageName = "default" });
        b.Targets.Add(new BackupTarget { DestinationId = cfgNow.Destinations[1].Id, StorageName = "default" });
        var vm = new BackupEditorViewModel(b, isNew: false);
        var window = new BackupEditorWindow { DataContext = vm, Width = 980, Height = 900 };
        CaptureWindow(window, "editor-backup-with-targets");
    }

    [AvaloniaFact]
    public void Shot_MainWindow_Sidebar()
    {
        // Capture the full main window so the sidebar footer (the one
        // the user keeps reworking) is visible. Default Backups view
        // shows in the content pane.
        ResetConfig();
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
            Width = 1240,
            Height = 820,
        };
        CaptureWindow(window, "main-window-sidebar");
    }

    [AvaloniaFact]
    public void Shot_OnboardingWindow()
    {
        // Capture Easy mode (the default). Source chips + selected
        // destinations seeded so the chips render. Seed must be in
        // config first so the destination dropdown isn't empty.
        ResetConfig();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(new Destination
            {
                Name = "My Passport (external)",
                Kind = DestinationKind.ExternalDrive,
                PathOrSubpath = @"M:\Backup\Duplicacy",
            });
        });
        var window = new OnboardingWindow(seed: null, isFirstBackup: true)
        {
            Width = 820,
            Height = 720,
        };
        CaptureWindow(window, "onboarding-easy");
    }

    [AvaloniaFact]
    public void Shot_OnboardingWindow_Step2()
    {
        // Capture Step 2 (Pick a destination) with the "Add" button in
        // its disabled state — user-flagged the disabled styling as
        // "ugly, bad corners" and we need a regression-tracked render
        // of the fix. To land here we navigate the VM to step 2
        // directly after seeding a source.
        ResetConfig();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(new Destination
            {
                Name = "Dropbox — personal",
                Kind = DestinationKind.DropboxAppScoped,
                PathOrSubpath = "duplicacy-store",
            });
            cfg.Destinations.Add(new Destination
            {
                Name = "My Passport (external)",
                Kind = DestinationKind.ExternalDrive,
                PathOrSubpath = @"M:\Backup\Duplicacy",
            });
        });
        var window = new OnboardingWindow(seed: null, isFirstBackup: true)
        {
            Width = 820,
            Height = 720,
        };
        // Reach into the VM to seed a source + jump to step 2 — the
        // window's normal flow needs UI events we can't fire reliably
        // from a headless test.
        if (window.DataContext is OnboardingViewModel vm)
        {
            vm.SourceChips.Add(new SourceChipVm(@"C:\Users\me\Documents"));
            vm.CurrentStep = 2;
        }
        CaptureWindow(window, "onboarding-step2");
    }

    [AvaloniaFact]
    public void Shot_DestinationsView_Populated()
    {
        ResetConfig();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(new Destination
            {
                Name = "My Passport (external)",
                Kind = DestinationKind.ExternalDrive,
                PathOrSubpath = @"M:\Backup\Duplicacy",
            });
            cfg.Destinations.Add(new Destination
            {
                Name = "Dropbox — personal",
                Kind = DestinationKind.DropboxAppScoped,
                PathOrSubpath = "local_c",
            });
            cfg.Destinations.Add(new Destination
            {
                Name = "Cloudflare R2",
                Kind = DestinationKind.S3Compatible,
                S3Endpoint = "abc.r2.cloudflarestorage.com",
                S3Bucket = "backups",
                PathOrSubpath = "primary",
            });
        });
        Capture<DestinationsView, DestinationsViewModel>("destinations-populated");
    }

    [AvaloniaFact]
    public void Shot_DestructiveConfirm_Dialog()
    {
        var dlg = new DestructiveConfirmWindow(
            title: "Remove this backup?",
            subtitle: "«local_c» will no longer run on schedule and will disappear from Duplimate. " +
                      "The backup data already stored at your destinations stays where it is — you " +
                      "can delete it from there separately if you want. Type the backup name to confirm.",
            confirmString: "local_c",
            caseInsensitive: true);
        dlg.Width = 540;
        dlg.SizeToContent = SizeToContent.Height;
        CaptureWindow(dlg, "dialog-destructive-confirm");
    }

    // ----- dark mode ---------------------------------------------------
    //
    // Re-capture the big three surfaces in dark theme so theme regressions
    // (contrast, missing dark-mode brush overrides) surface next to the
    // light-theme PNGs.

    // Dark Dashboard shot removed — replaced by dark-backups-empty.
    [AvaloniaFact] public void Shot_Dark_BackupsView_Empty()     { ResetConfig(); CaptureDark<BackupsView,      BackupsViewModel>("dark-backups-empty"); }
    [AvaloniaFact] public void Shot_Dark_SettingsView()          { ResetConfig(); CaptureDark<SettingsView,     SettingsViewModel>("dark-settings"); }

    [AvaloniaFact]
    public void Shot_Dark_DestinationEditor_Dropbox()
    {
        var vm = new DestinationEditorViewModel(
            new Destination { Kind = DestinationKind.DropboxAppScoped, Encrypted = true }, isNew: false);
        var window = new DestinationEditorWindow { DataContext = vm, Width = 740, Height = 760 };
        CaptureDarkWindow(window, "dark-editor-destination-dropbox");
    }

    // ----- plumbing -----

    private static void Capture<TView, TVm>(string name)
        where TView : Control, new()
        where TVm   : class, new()
    {
        var view = new TView { DataContext = new TVm() };
        var window = new Window
        {
            Content = view,
            Width = 1240,
            Height = 820,
            SystemDecorations = SystemDecorations.None,
            Background = Avalonia.Media.Brushes.White,
        };
        CaptureWindow(window, name);
    }

    private static void CaptureDark<TView, TVm>(string name)
        where TView : Control, new()
        where TVm   : class, new()
    {
        var view = new TView { DataContext = new TVm() };
        var window = new Window
        {
            Content = view,
            Width = 1240,
            Height = 820,
            SystemDecorations = SystemDecorations.None,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Background = Avalonia.Media.Brushes.Black,
        };
        CaptureWindow(window, name);
    }

    private static void CaptureDarkWindow(Window window, string name)
    {
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        CaptureWindow(window, name);
    }

    private static void CaptureWindow(Window window, string name)
    {
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, $"{name}.png");

        // RenderTargetBitmap needs a PixelSize + DPI. 96 is the Avalonia
        // logical-px baseline (1:1 device px to DIP), matching what the
        // layout engine computed during UpdateLayout.
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);

        window.Close();
    }

    /// <summary>
    /// Write PNGs next to the source tree (not next to the test binary),
    /// so they live under git and can be diffed between runs without
    /// hunting through bin/Debug/….
    /// </summary>
    private static string ResolveOutputDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(ScreenshotTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "artifacts", "screenshots");
    }

    private static void ResetConfig()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
