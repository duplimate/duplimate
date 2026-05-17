using System;
using System.IO;
using Duplimate.Services.Platform;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Keeps scheduled tasks in sync with where the user keeps the main
/// Duplimate binary. The user's binary stays wherever they put it
/// (we don't relocate it). What we do:
///
/// <list type="number">
///   <item>
///     <see cref="ExtractStub"/> (Windows-only) — drop a tiny standalone
///     exe at <see cref="StubExePath"/> that the scheduled task can fall
///     back to when the main exe is missing (user moved/deleted the
///     download). The stub does ONE thing: pop a "Duplimate needs
///     attention — open the app from its new location" warning so the
///     user knows how to fix it. macOS / Linux schedulers don't have a
///     direct equivalent (launchd / systemd just emit a unit-failure
///     log line for a missing executable), so the stub is skipped on
///     those platforms — the path-changed sentinel below is still the
///     primary recovery mechanism.
///   </item>
///   <item>
///     <see cref="ReupsertIfExePathChanged"/> — every launch, check if
///     the running exe's path differs from the path stored in the
///     sentinel. If it does, the user must have moved the binary;
///     rewrite every scheduled task so it points at the new path.
///     The act of opening the app from the new location automatically
///     heals every schedule.
///   </item>
/// </list>
///
/// <para>
/// If the stub binary isn't present at build time (release builds that
/// skip the stub project, dev builds with no stub asset, all non-Windows
/// builds), task creation silently falls back to a non-wrapped action —
/// schedules still run, they just don't have the warning fallback when
/// the main exe goes missing.
/// </para>
/// </summary>
public static class CanonicalInstaller
{
    private static ILogger _log => AppLogger.For("CanonicalInstaller");

    /// <summary>
    /// Per-user data root for canonical installer state. On Windows
    /// this is %LOCALAPPDATA% to match where the stub binary used to
    /// live; on Unix it's the config root, keeping all per-user state
    /// inside one tree the user can reason about.
    /// </summary>
    private static string DataRoot => PlatformInfo.IsWindows
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : AppPaths.ConfigRoot;

    /// <summary>
    /// Stable path for the fallback stub exe that scheduled tasks call
    /// when the user's main exe is missing. Per-user, always writable.
    /// Deliberately under <c>Programs\</c> rather than the app's data
    /// dir so a future "reset config" path doesn't wipe it. Windows-only
    /// — non-Windows schedulers don't use the stub mechanism (this path
    /// is still computed for parity / test asserts but never written
    /// to off-Windows).
    /// </summary>
    public static string StubExePath { get; } = PlatformInfo.IsWindows
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Duplimate", "Duplimate-stub.exe")
        : Path.Combine(AppPaths.ConfigRoot, "Duplimate-stub"); // never created on Unix

    /// <summary>
    /// Sentinel that records the path the running main exe was at the
    /// last time we re-upserted scheduled tasks. If the current process
    /// path no longer matches, the user moved the exe — rewrite tasks.
    /// </summary>
    public static string SentinelPath { get; } = Path.Combine(DataRoot, "Duplimate", "last-exe-path.txt");

    /// <summary>
    /// Flag file the stub writes when it fires. Presence on app launch
    /// means a scheduled task tried to run with a missing main exe at
    /// some point since the last app launch — useful for surfacing a
    /// "your last backup didn't run" signal to the user.
    /// </summary>
    public static string StubFiredFlagPath { get; } = Path.Combine(DataRoot, "Duplimate", "stub-fired.flag");

    /// <summary>
    /// Drops the embedded stub exe to <see cref="StubExePath"/>. No-op
    /// if the on-disk copy already matches the embedded resource. The
    /// stub is the sole occupant of its folder; we don't put a full
    /// copy of the main app there. No-op on non-Windows (no stub
    /// mechanism on macOS / Linux).
    /// </summary>
    public static void ExtractStub()
    {
        if (!PlatformInfo.IsWindows) return;
        try { StubEmbedder.EnsureExtracted(StubExePath); }
        catch (Exception ex)
        {
            // Stub extraction is a "nice-to-have safety net". Log and
            // continue — the main app + scheduled tasks still work
            // without it; the user just loses the friendly warning when
            // they move the exe and don't reopen.
            _log.Warning(ex, "Stub extract to {Path} failed; tasks will run without the missing-exe fallback", StubExePath);
        }
    }

    /// <summary>
    /// Compares <c>Environment.ProcessPath</c> against the sentinel. If
    /// they differ, every existing scheduled task gets re-upserted with
    /// the new exe path (and the sentinel is rewritten). Idempotent —
    /// when nothing changed, returns 0 without touching the scheduler.
    ///
    /// <para>
    /// Important: this method is the SOLE mechanism that keeps scheduled
    /// tasks pointing at the right exe. It does NOT consult
    /// <see cref="StubFiredFlagPath"/> at all — the flag only ever
    /// drives the user-facing "scheduled run was missed" toast. Path
    /// correctness comes from the <see cref="SentinelPath"/> sentinel
    /// alone, so a missing / corrupt / hand-deleted flag file can never
    /// cause schedules to drift to a stale path.
    /// </para>
    /// </summary>
    public static int ReupsertIfExePathChanged(ConfigStore config, IScheduler scheduler)
    {
        try
        {
            var current = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(current)) return 0;

            var last = SafeReadSentinel();
            if (string.Equals(last, current, StringComparison.OrdinalIgnoreCase))
                return 0;

            int reupserted = 0;
            foreach (var b in config.Current.Backups)
            {
                try
                {
                    scheduler.UpsertTask(b, current);
                    reupserted++;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to re-upsert task for {Name}", b.Name);
                }
            }

            SafeWriteSentinel(current);
            if (reupserted > 0)
                _log.Information(
                    "Exe path changed ({Old} -> {New}); re-upserted {Count} scheduled task(s)",
                    last ?? "(unset)", current, reupserted);
            return reupserted;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ReupsertIfExePathChanged failed; existing tasks unchanged");
            return 0;
        }
    }

    /// <summary>
    /// If the stub fired between the previous app launch and this one,
    /// returns the timestamp it recorded and deletes the flag. Returns
    /// null when no flag is present. Caller should surface a one-line
    /// "scheduled run was missed; tasks have been refreshed" notice.
    ///
    /// This is purely an INFORMATIONAL signal. Whether or not the flag
    /// is present has no bearing on whether scheduled tasks have the
    /// correct exe path — that's <see cref="ReupsertIfExePathChanged"/>'s
    /// job, driven entirely by the sentinel. A user could safely delete
    /// stub-fired.flag any time they wanted; they'd just lose the
    /// "your last backup didn't run" toast on the next app launch.
    /// </summary>
    public static DateTime? CheckAndClearStubFiredFlag()
    {
        try
        {
            if (!File.Exists(StubFiredFlagPath)) return null;
            var raw = File.ReadAllText(StubFiredFlagPath).Trim();
            DateTime.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var when);
            try { File.Delete(StubFiredFlagPath); } catch { /* best effort */ }
            return when == default ? DateTime.UtcNow : when;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "CheckAndClearStubFiredFlag failed; flag treated as absent");
            return null;
        }
    }

    private static string? SafeReadSentinel()
    {
        try { return File.Exists(SentinelPath) ? File.ReadAllText(SentinelPath).Trim() : null; }
        catch { return null; }
    }

    private static void SafeWriteSentinel(string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SentinelPath)!);
            File.WriteAllText(SentinelPath, content);
        }
        catch { /* sentinel is an optimization, not correctness-critical */ }
    }
}
