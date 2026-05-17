using System;
using System.IO;
using System.Linq;
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
/// Renders the 12 PNGs used on the project website + README.
///
/// Conventions enforced here:
///   • Every shot of a navigable page (Backups / Destinations / Restore /
///     Logs / Settings) is captured INSIDE the full MainWindow so the
///     sidebar is always visible — the previous shots cropped to the
///     content pane only and read as "fragments of the app" rather than
///     "the whole app".
///   • Modal dialogs (Onboarding, editors) are captured standalone
///     because that's how the user actually sees them — a modal IS the
///     whole interface while it's open.
///   • Seed data is "small but plausible": 2-3 backups, 2-3 destinations,
///     mixed status, no PII-looking strings. The same seed is reused
///     across multiple shots so the demo feels continuous.
///   • The "running" shot is data-driven: we flip the card VM's
///     IsRunning + OverallPercent directly rather than spinning up a
///     real Duplicacy run. Avalonia's layout engine doesn't care whether
///     the bytes are moving — only whether the bound values claim they
///     are — so this produces the same visual as a real run captured at
///     the same instant, with no flake risk and no 5 GB scratch file.
///
/// Output: tests/Duplimate.Tests/artifacts/screenshots/<NN>-<slug>.png.
/// The accompanying `scripts/regenerate-website-screenshots.ps1` copies
/// these 12 into `docs/screenshots/` for the website.
/// </summary>
public class WebsiteScreenshotTests
{
    private static readonly string OutputDir = ResolveOutputDir();

    // Window dimensions for full-shell shots. 1280×820 mirrors a typical
    // first-run window on a 1080p display — large enough that the
    // sidebar, the page content, and any health-summary callout fit
    // without scrolling, small enough that nothing inflates beyond a
    // realistic resting size.
    private const int ShellWidth  = 1280;
    private const int ShellHeight = 820;

    // ─── 1. Backups: backup running ──────────────────────────────────

    [AvaloniaFact]
    public void Shot_01_Backups_Running()
    {
        SeedDemoConfig();
        // Flip one of the seeded backups into a "running" visual state
        // so the screenshot shows the progress bar + Running indicator.
        // We mutate the card VM after construction; the layout pass
        // picks the new values up on UpdateLayout below.
        CaptureMainWindow("01-backups-running", NavItem.Backups, vm =>
        {
            var card = vm.Backups.Cards.FirstOrDefault(c => c.Name == "documents");
            if (card is not null)
            {
                card.SetRunning();
                card.OverallPercent = 43;
                card.CurrentPhaseLabel = "Uploading chunks";
            }
        });
    }

    // ─── 2. Backups: healthy overview ────────────────────────────────

    [AvaloniaFact]
    public void Shot_02_Backups_Overview()
    {
        SeedDemoConfig();
        CaptureMainWindow("02-backups-overview", NavItem.Backups);
    }

    // ─── 3. Destinations: populated ──────────────────────────────────

    [AvaloniaFact]
    public void Shot_03_Destinations()
    {
        SeedDemoConfig();
        CaptureMainWindow("03-destinations", NavItem.Destinations);
    }

    // ─── 4. Restore: pick a backup (step 1) ──────────────────────────

    [AvaloniaFact]
    public void Shot_04_Restore_Pick()
    {
        SeedDemoConfig();
        CaptureMainWindow("04-restore-pick", NavItem.Restore);
    }

    // ─── 5. Restore: file picker (step 5) ────────────────────────────

    [AvaloniaFact]
    public void Shot_05_Restore_Files()
    {
        SeedDemoConfig();
        CaptureMainWindow("05-restore-files", NavItem.Restore, vm =>
        {
            // Synthesize a minimal tree under the file-picker step so
            // the panel has visible content. The VM exposes the step
            // enum directly; jumping past PickBackup/PickRevision is
            // OK for a screenshot because we don't fire the wizard's
            // network/disk side-effects — just paint the step's layout.
            var rvm = vm.Restore;
            var backup = vm.Backups.Cards.FirstOrDefault()?.Snapshot;
            if (backup is not null)
            {
                rvm.SelectedBackup = backup;
            }
            rvm.CurrentStep = RestoreViewModel.Step.PickFiles;
            // Populate a few placeholder files so the column isn't empty.
            // The picker binds to FileTreeRoots; if it's null/empty the
            // panel shows its loading placeholder, which is the wrong
            // story for a marketing shot. Real users see a tree mirroring
            // their snapshot — we synthesize one here.
            SeedRestoreFilePicker(rvm);
        });
    }

    // ─── 6. Logs: populated activity log ─────────────────────────────

    [AvaloniaFact]
    public void Shot_06_Logs()
    {
        SeedDemoConfig();
        SeedRunRecords();
        CaptureMainWindow("06-logs", NavItem.Logs);
    }

    // ─── 7. Settings: general ────────────────────────────────────────

    [AvaloniaFact]
    public void Shot_07_Settings()
    {
        SeedDemoConfig();
        CaptureMainWindow("07-settings", NavItem.Settings);
    }

    // ─── 8-10. Onboarding wizard (standalone dialog, 3 steps) ────────

    [AvaloniaFact]
    public void Shot_08_Onboarding_Sources()
    {
        SeedDemoConfig();
        var window = new OnboardingWindow(seed: null, isFirstBackup: true)
        {
            Width = 880, Height = 700,
        };
        // Step 1 with a couple of source chips already populated so the
        // chip area isn't empty.
        if (window.DataContext is OnboardingViewModel ovm)
        {
            ovm.SourceChips.Add(new SourceChipVm(@"C:\Users\me\Documents"));
            ovm.SourceChips.Add(new SourceChipVm(@"C:\Users\me\Pictures"));
        }
        CaptureWindow(window, "08-onboarding-sources");
    }

    [AvaloniaFact]
    public void Shot_09_Onboarding_Destination()
    {
        SeedDemoConfig();
        var window = new OnboardingWindow(seed: null, isFirstBackup: true)
        {
            Width = 880, Height = 700,
        };
        if (window.DataContext is OnboardingViewModel ovm)
        {
            ovm.SourceChips.Add(new SourceChipVm(@"C:\Users\me\Documents"));
            ovm.CurrentStep = 2;
        }
        CaptureWindow(window, "09-onboarding-destination");
    }

    [AvaloniaFact]
    public void Shot_10_Onboarding_Schedule()
    {
        SeedDemoConfig();
        var window = new OnboardingWindow(seed: null, isFirstBackup: true)
        {
            Width = 880, Height = 700,
        };
        if (window.DataContext is OnboardingViewModel ovm)
        {
            ovm.SourceChips.Add(new SourceChipVm(@"C:\Users\me\Documents"));
            ovm.CurrentStep = 3;
        }
        CaptureWindow(window, "10-onboarding-schedule");
    }

    // ─── 11. Destination editor (Dropbox / cloud variant) ────────────

    [AvaloniaFact]
    public void Shot_11_Destination_Editor_Dropbox()
    {
        SeedDemoConfig();
        var vm = new DestinationEditorViewModel(
            new Destination { Kind = DestinationKind.DropboxAppScoped, Encrypted = true },
            isNew: false);
        var window = new DestinationEditorWindow
        {
            DataContext = vm, Width = 820, Height = 760,
        };
        CaptureWindow(window, "11-destination-editor-dropbox");
    }

    // ─── 11b. Destination editor (Local / external-drive variant) ───
    // Sibling of the Dropbox shot above. The tutorial branches at the
    // "pick a destination" step into local vs cloud; both branches need
    // their own visual so a reader landing on the website can see what
    // the simpler "just point at a folder" flow looks like compared to
    // the OAuth flow.

    [AvaloniaFact]
    public void Shot_11b_Destination_Editor_Local()
    {
        SeedDemoConfig();
        var vm = new DestinationEditorViewModel(
            new Destination
            {
                Kind = DestinationKind.ExternalDrive,
                Name = "My Passport (external)",
                PathOrSubpath = @"M:\Backup\Duplicacy",
                Encrypted = true,
                ExpectedDriveLetter = "M:",
                ExpectedVolumeLabel = "My Passport",
            },
            isNew: false);
        var window = new DestinationEditorWindow
        {
            DataContext = vm, Width = 820, Height = 760,
        };
        CaptureWindow(window, "11b-destination-editor-local");
    }

    // ─── 12. Backup editor (advanced) ────────────────────────────────

    [AvaloniaFact]
    public void Shot_12_Backup_Editor()
    {
        SeedDemoConfig();
        var cfg = ServiceLocator.Config.Current;
        var b = new Backup { Name = "documents", SourcePaths = { @"C:\Users\me\Documents" } };
        if (cfg.Destinations.Count >= 2)
        {
            b.Targets.Add(new BackupTarget { DestinationId = cfg.Destinations[0].Id, StorageName = "default" });
            b.Targets.Add(new BackupTarget { DestinationId = cfg.Destinations[1].Id, StorageName = "default" });
        }
        var vm = new BackupEditorViewModel(b, isNew: false);
        var window = new BackupEditorWindow
        {
            DataContext = vm, Width = 1020, Height = 820,
        };
        CaptureWindow(window, "12-backup-editor");
    }

    // ─── seed helpers ────────────────────────────────────────────────

    /// <summary>
    /// Lays down a plausible demo configuration used across most shots:
    /// 2 destinations + 3 backups with mixed status. Names are kept
    /// generic on purpose (no real usernames or paths beyond the
    /// canonical `C:\Users\me\…`) so the screenshots don't pin to one
    /// person's workstation.
    /// </summary>
    private static void SeedDemoConfig()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();

            cfg.Destinations.Add(new Destination
            {
                Name = "Dropbox — personal",
                Kind = DestinationKind.DropboxAppScoped,
                PathOrSubpath = "primary-store",
                Encrypted = true,
            });
            cfg.Destinations.Add(new Destination
            {
                Name = "My Passport (external)",
                Kind = DestinationKind.ExternalDrive,
                PathOrSubpath = @"M:\Backup\Duplicacy",
                Encrypted = true,
                ExpectedDriveLetter = "M:",
                ExpectedVolumeLabel = "My Passport",
            });
            cfg.Destinations.Add(new Destination
            {
                Name = "Wasabi (cold copy)",
                Kind = DestinationKind.S3Compatible,
                PathOrSubpath = "s3.wasabisys.com/me-backups/main",
                Encrypted = true,
            });

            var dropboxId  = cfg.Destinations[0].Id;
            var externalId = cfg.Destinations[1].Id;
            var s3Id       = cfg.Destinations[2].Id;

            cfg.Backups.Add(new Backup
            {
                Name = "documents",
                SourcePaths = { @"C:\Users\me\Documents", @"C:\Users\me\Pictures" },
                LastRunStatus = BackupRunStatus.Success,
                LastRunSummary = "12,408 files · 8.7 GB",
                LastRunEndUtc = DateTime.UtcNow.AddHours(-2),
                Targets =
                {
                    new BackupTarget { DestinationId = dropboxId,  StorageName = "default" },
                    new BackupTarget { DestinationId = externalId, StorageName = "default" },
                },
                Threads = 4,
                UseVss  = true,
                KeepPolicy = "0:365 30:90 7:30 1:7",
                PruneAfterBackup = true,
            });
            cfg.Backups.Add(new Backup
            {
                Name = "code-and-projects",
                SourcePaths = { @"C:\Users\me\Projects" },
                LastRunStatus = BackupRunStatus.Success,
                LastRunSummary = "284,510 files · 142.1 GB",
                LastRunEndUtc = DateTime.UtcNow.AddHours(-19),
                Targets =
                {
                    new BackupTarget { DestinationId = dropboxId, StorageName = "default" },
                    new BackupTarget { DestinationId = s3Id,      StorageName = "default" },
                },
                Threads = 8,
                UseVss  = true,
                KeepPolicy = "0:365 30:90 7:30 1:7",
            });
            cfg.Backups.Add(new Backup
            {
                Name = "music-library",
                SourcePaths = { @"D:\Music" },
                LastRunStatus = BackupRunStatus.Warning,
                LastRunSummary = "2 warnings (skipped: locked)",
                LastRunEndUtc = DateTime.UtcNow.AddDays(-1).AddHours(-3),
                Targets =
                {
                    new BackupTarget { DestinationId = externalId, StorageName = "default" },
                },
                Threads = 2,
                UseVss  = false,
                KeepPolicy = "0:730 30:180 7:60",
            });
        });
    }

    /// <summary>
    /// Add a handful of activity-log RunRecords so the Logs view has
    /// something to render. Real users accumulate hundreds; a half-dozen
    /// is enough to demonstrate the column layout (badge / backup / when /
    /// summary / actions) without sprawling beyond the visible area.
    /// </summary>
    private static void SeedRunRecords()
    {
        var store = ServiceLocator.Logs;
        var now = DateTime.UtcNow;
        // RunRecords are append-only via the orchestrator at runtime;
        // for the screenshot we write directly to history.json via the
        // store's append API. We tolerate the dependency on the store's
        // exact append surface by guarding with reflection-free best-
        // effort: if the API isn't available, the Logs panel still
        // renders its empty state, which is also a fine shot.
        try
        {
            store.AppendRun(new RunRecord
            {
                BackupName = "documents",
                Status = BackupRunStatus.Success,
                StartedUtc = now.AddHours(-2).AddMinutes(-7),
                EndedUtc   = now.AddHours(-2),
                Summary  = "12,408 files · 8.7 GB · 6m 53s",
                Kind     = RunKind.Backup,
            });
            store.AppendRun(new RunRecord
            {
                BackupName = "code-and-projects",
                Status = BackupRunStatus.Success,
                StartedUtc = now.AddHours(-19).AddMinutes(-22),
                EndedUtc   = now.AddHours(-19),
                Summary  = "284,510 files · 142.1 GB · 21m 09s",
                Kind     = RunKind.Backup,
            });
            store.AppendRun(new RunRecord
            {
                BackupName = "music-library",
                Status = BackupRunStatus.Warning,
                StartedUtc = now.AddDays(-1).AddHours(-3).AddMinutes(-4),
                EndedUtc   = now.AddDays(-1).AddHours(-3),
                Summary  = "2 warnings (skipped: locked)",
                Kind     = RunKind.Backup,
            });
            store.AppendRun(new RunRecord
            {
                BackupName = "documents",
                Status = BackupRunStatus.Success,
                StartedUtc = now.AddDays(-1).AddHours(-2).AddMinutes(-6),
                EndedUtc   = now.AddDays(-1).AddHours(-2),
                Summary  = "12,402 files · 8.7 GB · 5m 41s",
                Kind     = RunKind.Backup,
            });
            store.AppendRun(new RunRecord
            {
                BackupName = "code-and-projects",
                Status = BackupRunStatus.Success,
                StartedUtc = now.AddDays(-2).AddHours(-19).AddMinutes(-19),
                EndedUtc   = now.AddDays(-2).AddHours(-19),
                Summary  = "284,201 files · 141.9 GB · 19m 47s",
                Kind     = RunKind.Backup,
            });
        }
        catch
        {
            // Best-effort: log seeding is a "nice to have" for the
            // screenshot; if the store's append contract changes we'd
            // rather not block the build, just render an empty Logs
            // page.
        }
    }

    /// <summary>
    /// Populate the restore wizard's PickFiles list so the panel renders
    /// real-looking rows instead of the "0 files selected" empty box.
    /// Uses the public AllFiles/FilteredFiles collections exposed by
    /// <see cref="RestoreViewModel"/>; both are bound by the view so the
    /// painted result matches what a real snapshot listing produces.
    /// </summary>
    private static void SeedRestoreFilePicker(RestoreViewModel rvm)
    {
        // A short, plausible list of files under the Documents source —
        // mixed types and sizes so the row layout (icon / path / size /
        // modified) shows variation.
        var now = DateTime.UtcNow;
        var samples = new[]
        {
            new RevisionFile(@"Documents/Letters/2026-tax-return.pdf",       814_212,         now.AddHours(-4)),
            new RevisionFile(@"Documents/Letters/lease-signed.pdf",          1_117_403,       now.AddDays(-3)),
            new RevisionFile(@"Documents/Photos/2026/01-trip/IMG_2143.jpg",  4_812_004,       now.AddDays(-7)),
            new RevisionFile(@"Documents/Photos/2026/01-trip/IMG_2151.jpg",  5_321_874,       now.AddDays(-7)),
            new RevisionFile(@"Documents/Photos/2026/01-trip/IMG_2168.jpg",  4_944_018,       now.AddDays(-7)),
            new RevisionFile(@"Documents/Projects/website/index.html",       17_402,          now.AddDays(-1)),
            new RevisionFile(@"Documents/Projects/website/styles.css",       8_201,           now.AddDays(-1)),
            new RevisionFile(@"Documents/Projects/website/scripts/app.js",   42_018,          now.AddDays(-2)),
            new RevisionFile(@"Documents/notes/ideas.md",                    3_204,           now.AddHours(-22)),
            new RevisionFile(@"Documents/notes/reading-list.md",             1_876,           now.AddDays(-5)),
            new RevisionFile(@"Documents/work/Q1-plan.docx",                 132_410,         now.AddDays(-2)),
            new RevisionFile(@"Documents/work/proposal-final.docx",          218_004,         now.AddDays(-9)),
        };
        foreach (var f in samples)
        {
            var row = new RevisionFileRow(f, rvm) { IsSelected = false };
            rvm.AllFiles.Add(row);
            rvm.FilteredFiles.Add(row);
        }
        // Pre-select a couple so the bottom-bar counter shows something
        // other than "0 selected" — communicates that selection is real.
        rvm.AllFiles[0].IsSelected      = true;
        rvm.AllFiles[8].IsSelected      = true;
        rvm.AllFiles[10].IsSelected     = true;
        rvm.TotalFiles                  = samples.Length;
        rvm.SelectedFilesCount          = 3;
        rvm.SelectedFilesBytes          = samples[0].SizeBytes + samples[8].SizeBytes + samples[10].SizeBytes;
    }

    // ─── rendering plumbing ──────────────────────────────────────────

    /// <summary>
    /// Builds the full main shell, points its nav at <paramref name="nav"/>,
    /// runs an optional VM mutator, then snapshots the window to PNG.
    /// </summary>
    private static void CaptureMainWindow(
        string name,
        NavItem nav,
        Action<MainWindowViewModel>? mutate = null)
    {
        var vm = new MainWindowViewModel();
        vm.SelectedNav = nav; // also flips CurrentPage

        // Force the Backups page to refresh so its Cards are constructed
        // from the seed data we wrote before this call. The VM listens
        // for Config.Changed, but the change happens before the VM
        // subscribed (during the seed update), so an explicit refresh
        // is needed for the first paint.
        vm.Backups.Refresh();

        // Apply the per-shot mutator AFTER refresh so e.g. a SetRunning()
        // call lands on the freshly-constructed card.
        mutate?.Invoke(vm);

        var window = new MainWindow
        {
            DataContext = vm,
            Width  = ShellWidth,
            Height = ShellHeight,
        };
        CaptureWindow(window, name);
    }

    private static void CaptureWindow(Window window, string name)
    {
        window.Show();
        // Two layout passes — Avalonia's headless host occasionally
        // settles a binding only on the second pass when a property
        // changes during the first pass (running-state Override).
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, $"{name}.png");

        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);

        window.Close();
    }

    private static string ResolveOutputDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(WebsiteScreenshotTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "artifacts", "screenshots");
    }
}
