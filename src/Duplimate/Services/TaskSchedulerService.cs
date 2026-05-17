using Duplimate.Services.Platform;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Static factory that picks the platform-appropriate
/// <see cref="IScheduler"/> implementation. The viewmodels reach the
/// scheduler via <c>ServiceLocator.Scheduler</c> (typed as
/// <see cref="IScheduler"/>); this factory is the single point that
/// resolves the concrete impl at startup.
///
/// <para>
/// Kept as a separate file (rather than a one-liner in
/// <see cref="ServiceLocator"/>) so the <c>#if WINDOWS</c> gate around
/// the Windows-only impl reference doesn't leak into the
/// service-wiring code.
/// </para>
/// </summary>
public static class TaskSchedulerService
{
    private static ILogger _log => AppLogger.For(nameof(TaskSchedulerService));

    public static IScheduler Create()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return new Platform.Windows.WindowsTaskScheduler();
#endif
        if (PlatformInfo.IsMacOS) return new Platform.Unix.MacLaunchdScheduler();
        if (PlatformInfo.IsLinux) return new Platform.Unix.LinuxSystemdScheduler();
        _log.Warning("No native scheduler for this platform — scheduled backups will be inert. Run unattended via your own cron / launchd entry.");
        return new NoOpScheduler();
    }
}

/// <summary>
/// Last-resort scheduler for platforms with no built-in cron-style
/// service we can target. Logs a warning on every call; the user
/// can still trigger backups via the GUI's "Run now" button.
/// </summary>
internal sealed class NoOpScheduler : IScheduler
{
    private static ILogger _log => AppLogger.For<NoOpScheduler>();
    public void UpsertTask(Models.Backup backup, string exePath) =>
        _log.Warning("Scheduler is inert on this platform; ignoring upsert for {Name}", backup.Name);
    public void DeleteTask(Models.Backup backup) => _log.Debug("Scheduler is inert; ignoring delete for {Name}", backup.Name);
    public string TaskNameFor(Models.Backup backup) => "edb-" + backup.Name;
}
