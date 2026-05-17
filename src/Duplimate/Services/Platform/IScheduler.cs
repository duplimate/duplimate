using Duplimate.Models;

namespace Duplimate.Services.Platform;

/// <summary>
/// Platform-neutral scheduling surface. Each desktop OS has its own
/// scheduler (Windows Task Scheduler, macOS launchd, Linux systemd
/// user units / cron); this interface is the only contract the rest
/// of the app needs to know about.
///
/// <para>
/// Implementations are expected to be idempotent: <see cref="UpsertTask"/>
/// reconciles whatever's currently registered with the latest definition
/// from <paramref name="backup"/>; <see cref="DeleteTask"/> is a no-op
/// when nothing is registered.
/// </para>
/// </summary>
public interface IScheduler
{
    /// <summary>
    /// Register or update a recurring task that runs the given backup
    /// using the current process exe (with the unattended <c>--run
    /// "name"</c> CLI). Caller passes <paramref name="exePath"/> so the
    /// scheduler doesn't have to second-guess where the user keeps the
    /// app.
    /// </summary>
    void UpsertTask(Backup backup, string exePath);

    /// <summary>Remove the recurring task. Safe to call if it doesn't exist.</summary>
    void DeleteTask(Backup backup);

    /// <summary>Stable name used by the underlying scheduler (label / unit name / task path).</summary>
    string TaskNameFor(Backup backup);
}
