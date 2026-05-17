using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Duplimate.Services;

namespace Duplimate;

internal static class Program
{
    /// <summary>
    /// Set when the app is launched via the Explorer right-click "Restore
    /// an older version" verb — carries the file/folder path Windows
    /// passed on the command line. The MainWindow reads this on load
    /// and routes the user to the Restore tab pre-filled. Cleared after
    /// it's been consumed so a stale path doesn't fire on next launch.
    /// </summary>
    public static string? PendingRestorePath { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // CLI dispatch. GUI is the default; anything else runs headlessly and exits.
            var mode = ParseMode(args);

            // Log the invocation up front (before the dispatch) — useful when
            // a scheduled-task run silently fails to reach the orchestrator.
            // Skip the heavy ServiceLocator.InitializeCore for the
            // print-and-exit modes (Version, Help, UsageError); they
            // don't need config / secrets / extracted duplicacy.exe
            // and the user shouldn't pay 200ms+ + AppData dir creation
            // for `--help`.
            var skipInit = mode.Mode is RunMode.Version
                                       or RunMode.Help
                                       or RunMode.UsageError;
            if (!skipInit)
            {
                ServiceLocator.InitializeCore();
                AppLogger.For("Program").Information(
                    "Invoked: mode={Mode} args={Args}",
                    mode.Mode, string.Join(" ", args));
            }

            var exit = mode.Mode switch
            {
                RunMode.Gui         => RunGui(args),
                RunMode.RunOne      => RunUnattended(mode.BackupName!, onlyThisOne: true).GetAwaiter().GetResult(),
                RunMode.RunAll      => RunUnattended(null, onlyThisOne: false).GetAwaiter().GetResult(),
                RunMode.Migrate     => RunMigrate(mode.Path!),
                RunMode.Version     => PrintVersion(),
                RunMode.Help        => PrintUsage(exitCode: 0),
                RunMode.UsageError  => PrintUsage(exitCode: 2),
                _                   => RunGui(args),
            };
            return exit;
        }
        catch (Exception ex)
        {
            // Last-chance catch so an unhandled exception still lands in the log.
            try { AppLogger.Log.Fatal(ex, "Unhandled exception in Main"); } catch { }
            throw;
        }
        finally
        {
            AppLogger.Shutdown();
        }
    }

    /// <summary>
    /// Process-wide single-instance coordinator. Owned by Program for
    /// the lifetime of the GUI; <c>App.OnFrameworkInitializationCompleted</c>
    /// reads <see cref="SingleInstanceCoordinator.SecondInstanceForwarded"/>
    /// to surface forwarded args (right-click "restore" paths, etc.)
    /// to the live MainWindow.
    /// </summary>
    public static SingleInstanceCoordinator? Coordinator { get; private set; }

    // ---- GUI ----

    private static int RunGui(string[] args)
    {
        // Single-instance gate: if another GUI is already running for
        // this user + this exe path, forward our args to it and exit.
        // The primary's pipe server picks them up and the existing
        // MainWindow handles the path (e.g. routes a --restore-prompt
        // path to the Restore tab).
        Coordinator = new SingleInstanceCoordinator();
        if (!Coordinator.TryAcquireMutex())
        {
            var forwarded = Coordinator.ForwardArgsToPrimary(args);
            AppLogger.For("Program").Information(
                "Second instance: forwarded={Forwarded}; exiting", forwarded);
            Coordinator.Dispose();
            // Exit 0 even if forward failed — there's already a primary;
            // failing here would just mean the user clicked too fast.
            return 0;
        }

        Coordinator.StartServer();

        // Broken-install probe + stub-extract + scheduler-task re-upsert
        // are deferred to App.OnFrameworkInitializationCompleted so the
        // Avalonia IAssetLoader is registered before we resolve any
        // avares:// URIs. Calling DuplicacyEmbedder.IsAvailable() here
        // (pre-Avalonia-init) silently failed the embedded-resource
        // probe — falsely claiming the binary was missing on debug
        // builds where it's only present as a bundled AvaloniaResource.
        // App.OnFrameworkInitializationCompleted invokes
        // <see cref="RunPostInitStartup"/> below at the right moment.

        // Wrap the Avalonia run-loop in a defensive catch so a
        // platform-level Win32 exception thrown from a dispatcher post
        // (e.g. icon-handle exhaustion crashing a notification window
        // build) doesn't take down the whole process. Such a bug should
        // be visible in logs as a FATAL — but the user shouldn't lose
        // their session because a toast couldn't allocate a GDI
        // handle. Re-throw if cancellation/shutdown was the cause.
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
        catch (Exception ex)
        {
            try { AppLogger.Log.Fatal(ex, "Avalonia run loop crashed; shutting down"); } catch { }
        }
        Coordinator.Dispose();
        return 0;
    }

    /// <summary>
    /// Post-Avalonia-init startup work. Invoked from
    /// <see cref="App.OnFrameworkInitializationCompleted"/> once the
    /// IAssetLoader service is registered, so avares:// URI lookups
    /// (used by <see cref="DuplicacyEmbedder"/> and
    /// <see cref="StubEmbedder"/>) resolve correctly.
    ///
    /// Runs in two phases:
    ///   1. Synchronous broken-install probe — surfaces a Failure
    ///      toast IFF no Duplicacy binary can be found anywhere.
    ///   2. Background housekeeping — stub extract, scheduled-task
    ///      re-upsert, missed-run check. Off-thread because
    ///      schtasks.exe rebinds for every backup (50-200ms each)
    ///      can cumulatively freeze the splash for users with many
    ///      backups.
    /// </summary>
    public static void RunPostInitStartup()
    {
        // Surface a broken install up front rather than burying the
        // failure mid-backup. The FileNotFoundException thrown by
        // EnsureExtracted() carries a user-friendly message; mirror
        // it here so the user can act before they ever click "Run
        // now" or "Connect to Dropbox".
        if (!DuplicacyEmbedder.IsAvailable())
        {
            var fname = Services.Platform.PlatformInfo.DuplicacyBinaryFileName;
            AppLogger.For("Program").Error(
                "{Binary} is not bundled with this build and no sibling copy was found — backups will fail until this is corrected.",
                fname);
            _ = ServiceLocator.Notifier.NotifyAsync(
                title: "Duplimate install looks incomplete",
                detail:
                    "The Duplicacy binary that does the actual backing-up is " +
                    "missing from this copy. Backups and cloud authentication " +
                    "will fail until you reinstall from the latest release " +
                    $"(or drop a {fname} next to the Duplimate binary).",
                severity: NotificationSeverity.Failure);
        }

        // Fire deferred housekeeping off-thread. By now Avalonia is
        // initialized, so any avares:// resolution inside
        // ExtractStub() / re-upsert succeeds.
        _ = System.Threading.Tasks.Task.Run(RunDeferredStartupHousekeeping);
    }

    /// <summary>
    /// Stub-extract + scheduler-task re-upsert + missed-run check.
    /// Runs after first paint so a slow path (user moved the exe → N
    /// schtasks.exe spawns) doesn't freeze the splash. Idempotent and
    /// failure-tolerant; the GUI is never blocked on this.
    /// </summary>
    private static void RunDeferredStartupHousekeeping()
    {
        try
        {
            CanonicalInstaller.ExtractStub();
            CanonicalInstaller.ReupsertIfExePathChanged(ServiceLocator.Config, ServiceLocator.Scheduler);

            if (CanonicalInstaller.CheckAndClearStubFiredFlag() is DateTime stubFiredAt)
            {
                AppLogger.For("Program").Information(
                    "Stub-fired flag found (last fire {Fired:o}); scheduled tasks have been refreshed to current exe path.",
                    stubFiredAt);
                _ = ServiceLocator.Notifier.NotifyAsync(
                    title: "Scheduled backups are back online",
                    detail: "A scheduled backup couldn't run while Duplimate was moved. " +
                            "Tasks have been refreshed — the next scheduled run will work normally.",
                    severity: NotificationSeverity.Info);
            }
        }
        catch (Exception ex)
        {
            AppLogger.For("Program").Warning(ex, "Deferred startup housekeeping failed");
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // ---- unattended ----

    private static async Task<int> RunUnattended(string? backupName, bool onlyThisOne)
    {
        ServiceLocator.InitializeCore();
        var cfg = ServiceLocator.Config.Current;

        var backups = onlyThisOne
            ? cfg.Backups.Where(b => string.Equals(b.Name, backupName, StringComparison.OrdinalIgnoreCase)).ToList()
            : cfg.Backups.Where(b => b.Enabled).ToList();

        if (backups.Count == 0)
        {
            Console.Error.WriteLine(onlyThisOne
                ? $"No backup named '{backupName}' found."
                : "No enabled backups.");
            return 2;
        }

        int exit = 0;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        foreach (var b in backups)
        {
            Console.WriteLine($"=== {b.Name} ===");

            // Respect schedule's PausedUntilUtc when called headlessly.
            if (b.Schedule.PausedUntilUtc is DateTime until && DateTime.UtcNow < until)
            {
                Console.WriteLine($"Paused until {until:o} — skipping.");
                continue;
            }

            try
            {
                var r = await ServiceLocator.Orchestrator.RunBackupAsync(b, cts.Token);
                Console.WriteLine($"{b.Name}: {r.Status} — {r.Summary}");
                if (r.Status == Models.BackupRunStatus.Failed) exit = 1;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"{b.Name}: cancelled");
                exit = 1;
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{b.Name}: {ex.Message}");
                exit = 1;
            }
        }

        return exit;
    }

    // ---- migration ----

    private static int RunMigrate(string legacyPath)
    {
        ServiceLocator.InitializeCore();
        var report = ServiceLocator.Migrator.MigrateFrom(legacyPath);
        if (report.Skipped)
        {
            Console.WriteLine($"Skipped: {report.Reason}");
            return 0;
        }
        Console.WriteLine($"Imported {report.ImportedBackups.Count} backup(s) from {legacyPath}");
        foreach (var name in report.ImportedBackups) Console.WriteLine("  - " + name);
        foreach (var e in report.Errors) Console.Error.WriteLine("  ! " + e);
        return report.Errors.Count > 0 ? 1 : 0;
    }

    // ---- version ----

    private static int PrintVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version?.ToString(3) ?? "?";
        Console.WriteLine($"Duplimate {ver}");
        return 0;
    }

    /// <summary>
    /// Prints usage and returns exit code 2 (POSIX convention for
    /// usage/argument errors). Triggered by <c>--help</c>, <c>-h</c>,
    /// <c>/?</c>, AND any malformed CLI invocation that
    /// <see cref="ParseMode"/> classifies as <c>RunMode.Help</c>
    /// (e.g. <c>--run</c> with no backup name, <c>--migrate</c> with
    /// no path). Without this dispatch, those misuses silently
    /// fell through to <c>RunGui</c> — a scheduled task with a
    /// broken <c>--run</c> would launch the GUI under SYSTEM.
    /// </summary>
    private static int PrintUsage(int exitCode = 0)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version?.ToString(3) ?? "?";
        // Binary name + scheduler tag vary by platform; keep the help
        // line accurate so a user reading "Task Scheduler entry point"
        // on macOS doesn't go hunting for taskschd.msc.
        var bin = Services.Platform.PlatformInfo.IsWindows ? "Duplimate.exe" : "Duplimate";
        var schedTag = Services.Platform.PlatformInfo.IsWindows ? "Windows Task Scheduler"
                     : Services.Platform.PlatformInfo.IsMacOS   ? "launchd"
                     :                                            "systemd user timer";
        Console.WriteLine($"Duplimate {ver}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine($"  {bin}                    Launch the GUI (default).");
        Console.WriteLine($"  {bin} --run <name>       Run one backup unattended ({schedTag} entry point).");
        Console.WriteLine($"  {bin} --run-all          Run every backup unattended.");
        Console.WriteLine($"  {bin} --migrate <path>   Import an existing Duplicacy setup (a repo folder or a parent of many).");
        Console.WriteLine($"  {bin} --version          Print version and exit.");
        Console.WriteLine($"  {bin} --help | -h | /?   Show this help.");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine($"  • Config + logs live under {AppPaths.ConfigRoot}");
        return exitCode;
    }

    // ---- argv parser ----

    /// <summary>
    /// CLI invocation modes. <see cref="Help"/> = explicit user request
    /// (--help / -h / /?), exits 0 (POSIX). <see cref="UsageError"/> =
    /// malformed CLI (e.g. --run with no name), exits 2 — same printed
    /// usage but a different exit code so a scheduled-task wrapper that
    /// runs `duplimate --help && echo ok` sees the success.
    /// </summary>
    private enum RunMode { Gui, RunOne, RunAll, Migrate, Version, Help, UsageError }

    private readonly record struct ParsedMode(RunMode Mode, string? BackupName = null, string? Path = null);

    private static ParsedMode ParseMode(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run":
                    if (i + 1 < args.Length) return new(RunMode.RunOne, BackupName: args[++i]);
                    return new(RunMode.UsageError);
                case "--run-all":
                    return new(RunMode.RunAll);
                case "--migrate":
                    if (i + 1 < args.Length) return new(RunMode.Migrate, Path: args[++i]);
                    return new(RunMode.UsageError);
                case "--restore-prompt":
                    // Stash the path on the static so MainWindow picks
                    // it up when it's loaded; fall through to GUI mode.
                    if (i + 1 < args.Length) PendingRestorePath = args[++i];
                    return new(RunMode.Gui);
                case "--version":
                case "-v":
                    return new(RunMode.Version);
                case "--help":
                case "-h":
                case "/?":
                    return new(RunMode.Help);
            }
        }
        return new(RunMode.Gui);
    }
}
