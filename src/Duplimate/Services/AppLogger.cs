using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Duplimate.Models;
using Duplimate.Services.Platform;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Duplimate.Services;

/// <summary>
/// Structured application-level logging (Serilog).
///
/// Output: <c>Duplimate.config/logs/app/app-YYYYMMDD.log</c>, daily rolling,
/// retention configured by <see cref="LoggingSettings.RetentionDays"/> (default 30).
///
/// Debug mode: set the <c>DUPLIMATE_DEBUG</c> environment variable to <c>1</c> before
/// launching. <see cref="launch-debug.bat"/> does this automatically. In debug mode:
///   - Minimum level is forced to Verbose (all Log.Debug / Log.Verbose calls emit)
///   - A console is allocated (via AllocConsole) so a separate window can tail
///     stdout in real time; launch-debug.bat also opens a separate PowerShell
///     window that tails the log file.
///
/// Every service should accept an <see cref="ILogger"/> via <see cref="For{T}"/>
/// rather than using the static <see cref="Log"/> directly — this gives each
/// line a SourceContext tag (the service's type name) so logs are filterable.
/// </summary>
public static class AppLogger
{
    /// <summary>
    /// Global logger — used by static services or where DI isn't worthwhile.
    /// For per-class context, prefer <see cref="For{T}"/>.
    /// Exposed as a property over a backing field so the swap-and-
    /// dispose-after pattern in <see cref="Initialize"/> can use
    /// <see cref="System.Threading.Interlocked.Exchange{T}"/> for the
    /// atomic-publish step.
    /// </summary>
    public static ILogger Log => _log;
    private static ILogger _log = Logger.None;

    public static bool IsDebugMode { get; private set; }

    private static int _lastAppliedRetention = -1;
    private static string _lastAppliedLevel = "";
    private static bool _initialized;

    /// <summary>
    /// Idempotent: calling twice with the same settings is a no-op. Calling
    /// with different retention/level reconfigures the logger.
    /// </summary>
    public static void Initialize(LoggingSettings settings)
    {
        // Skip if already initialized with identical settings — avoids
        // re-creating the sink (and spamming the startup line) on every
        // innocuous config.json save.
        if (_initialized &&
            _lastAppliedRetention == settings.RetentionDays &&
            string.Equals(_lastAppliedLevel, settings.MinimumLevel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _lastAppliedRetention = settings.RetentionDays;
        _lastAppliedLevel = settings.MinimumLevel;
        _initialized = true;

        AppPaths.EnsureAll();

        IsDebugMode = string.Equals(
            Environment.GetEnvironmentVariable("DUPLIMATE_DEBUG"),
            "1",
            StringComparison.Ordinal);

        // In debug mode we want to see everything; in release we honor the setting.
        var minLevel = IsDebugMode
            ? LogEventLevel.Verbose
            : ParseLevel(settings.MinimumLevel);

        var logPath = Path.Combine(AppPaths.AppLogsDir, "app-.log");

        var cfg = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("App", "Duplimate")
            .Enrich.WithProperty("Version", AppVersion)
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, settings.RetentionDays),
                shared: true,
                fileSizeLimitBytes: 50_000_000,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

        if (IsDebugMode)
        {
            AllocConsoleIfAttached();
            cfg = cfg.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
            cfg = cfg.WriteTo.Debug();
        }

        // Swap-and-dispose-after pattern: build the new logger first,
        // ATOMICALLY swap the field, THEN dispose the old one with a
        // short grace delay. Earlier this disposed the old logger
        // BEFORE the new one was assigned — concurrent backup threads
        // mid-write through `AppLogger.For<T>().Information(...)` saw
        // an ObjectDisposedException because their ForContext clones
        // share sinks with the parent. The grace delay lets in-flight
        // writes drain before the file handle closes.
        var newLogger = cfg.CreateLogger();
        var previous = System.Threading.Interlocked.Exchange(ref _log, newLogger);
        if (previous is IDisposable prevDisposable && !ReferenceEquals(previous, newLogger))
        {
            // Dispose after a 500ms grace window — long enough for
            // any in-flight log calls on the previous sink to
            // complete. The fire-and-forget Task.Delay is acceptable:
            // worst case (process exits before delay fires) the OS
            // closes the file handle.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await System.Threading.Tasks.Task.Delay(500); } catch { }
                try { prevDisposable.Dispose(); } catch { /* best-effort */ }
            });
        }

        Log.Information(
            "Duplimate {Version} starting — debug={Debug} minLevel={MinLevel} retentionDays={Retention} logDir={LogDir}",
            AppVersion, IsDebugMode, minLevel, settings.RetentionDays, AppPaths.AppLogsDir);
    }

    /// <summary>
    /// Returns a logger scoped to the given type as its SourceContext.
    ///
    /// IMPORTANT: callers should NOT cache the result in a <c>static readonly</c>
    /// field — that would freeze a reference to whichever sink was live at
    /// type initialization and stop emitting after <see cref="Initialize"/>
    /// is called again (e.g. when the user changes log level in Settings).
    /// Use a property accessor instead:
    /// <code>
    /// private static ILogger _log => AppLogger.For&lt;MyService&gt;();
    /// </code>
    /// Serilog caches the per-type ForContext binding internally so the
    /// re-evaluation is cheap.
    /// </summary>
    public static ILogger For<T>() => Log.ForContext<T>();

    /// <summary>Returns a logger scoped to an arbitrary name. Same caveat as <see cref="For{T}"/>.</summary>
    public static ILogger For(string name) => Log.ForContext("SourceContext", name);

    public static void Shutdown()
    {
        try
        {
            Log.Information("Shutting down.");
            // Same swap-and-grace pattern as Initialize. Concurrent
            // threads still mid-write through `AppLogger.For<T>()
            // .Information(...)` need a moment to finish flushing
            // their lines before the underlying file handle closes —
            // without the grace, those writes throw
            // ObjectDisposedException. The 250ms used here is half of
            // Initialize's 500ms because shutdown is a one-shot event
            // and the runtime gives us limited time before forced
            // teardown.
            var previous = System.Threading.Interlocked.Exchange(ref _log, Logger.None);
            try
            {
                System.Threading.Thread.Sleep(250);
            }
            catch { }
            (previous as IDisposable)?.Dispose();
        }
        catch { }
    }

    private static LogEventLevel ParseLevel(string? s) =>
        Enum.TryParse<LogEventLevel>(s, ignoreCase: true, out var lvl)
            ? lvl
            : LogEventLevel.Information;

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- Console allocation for debug mode ----

    /// <summary>
    /// Windows GUI processes (<c>OutputType=WinExe</c>) start with no
    /// console attached. In debug mode we allocate one via Win32
    /// <c>AllocConsole</c> so a developer running with DUPLIMATE_DEBUG=1
    /// gets live tail output. macOS / Linux GUI binaries inherit
    /// stdin/stdout from the launching shell — no allocation needed,
    /// the parent terminal is already there.
    /// </summary>
    private static void AllocConsoleIfAttached()
    {
        if (!PlatformInfo.IsWindows) return;
        WindowsConsoleAllocator.Run();
    }

    /// <summary>
    /// Wrapped in a separate type so the JIT only resolves the
    /// kernel32 imports when this code actually runs (i.e. on Windows
    /// in debug mode). On Unix the type is referenced but never
    /// JIT'd, so the unused DllImport stubs never hit ld.so.
    /// </summary>
    private static class WindowsConsoleAllocator
    {
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

        public static void Run()
        {
            try
            {
                if (GetConsoleWindow() == IntPtr.Zero)
                {
                    AllocConsole();
                    // Redirect Console.Out to the new console
                    var stdOut = Console.OpenStandardOutput();
                    var writer = new StreamWriter(stdOut) { AutoFlush = true };
                    Console.SetOut(writer);
                }
            }
            catch { /* non-fatal */ }
        }
    }
}
