using System;
using System.IO;
using System.Text;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services.Platform.Unix;

/// <summary>
/// Linux scheduler — emits per-backup user systemd units (<c>.service</c>
/// + <c>.timer</c>) under <c>~/.config/systemd/user/</c>, then
/// enable+starts the timer via <c>systemctl --user</c>.
///
/// <para>
/// User units mean no root, no sudo. The catch is the user's session
/// must be live (or <c>loginctl enable-linger</c> set) for timers to
/// fire while logged out — closest analogue to Windows S4U logon
/// type. We don't enable lingering automatically; that's a sysadmin
/// decision and surfacing the prompt in the UI is a nicer UX than
/// silently configuring it.
/// </para>
///
/// <para>
/// Trigger model: <c>OnCalendar=</c> for daily/weekly,
/// <c>OnUnitActiveSec=</c> for hourly. <c>Persistent=true</c> on the
/// timer maps to Windows' StartWhenAvailable / launchd's
/// StartWhenAvailable — a missed firing while the box was asleep
/// runs as soon as it wakes.
/// </para>
/// </summary>
public sealed class LinuxSystemdScheduler : IScheduler
{
    private static ILogger _log => AppLogger.For<LinuxSystemdScheduler>();

    /// <summary>Prefix for unit filenames so a `systemctl --user list-timers`
    /// groups all of ours together.</summary>
    public const string UnitPrefix = "duplimate-";

    private static string UserUnitsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user");

    public string TaskNameFor(Backup backup) => UnitPrefix + UnixSchedulerHelpers.Slug(backup.Name);

    public void UpsertTask(Backup backup, string exePath)
    {
        var unitName = TaskNameFor(backup);
        var serviceUnit = unitName + ".service";
        var timerUnit   = unitName + ".timer";

        Directory.CreateDirectory(UserUnitsDir);
        UnixSchedulerHelpers.AtomicWriteText(Path.Combine(UserUnitsDir, serviceUnit), BuildService(backup, exePath));
        UnixSchedulerHelpers.AtomicWriteText(Path.Combine(UserUnitsDir, timerUnit),   BuildTimer(backup, unitName));

        _log.Information(
            "Upserting systemd user units: service={Svc} timer={Timer} freq={Freq} timeOfDay={Tod}",
            serviceUnit, timerUnit, backup.Schedule.Frequency, backup.Schedule.TimeOfDay);

        UnixSchedulerHelpers.Run(_log, "systemctl", new[] { "--user", "daemon-reload" }, tolerateFailure: false);
        // Stop first so a unit-definition change doesn't keep running
        // with the old timer expression. enable --now both enables
        // (autostart on login) and starts the timer right away.
        UnixSchedulerHelpers.Run(_log, "systemctl", new[] { "--user", "stop", timerUnit },          tolerateFailure: true);
        UnixSchedulerHelpers.Run(_log, "systemctl", new[] { "--user", "enable", "--now", timerUnit }, tolerateFailure: false);
    }

    public void DeleteTask(Backup backup)
    {
        var unitName = TaskNameFor(backup);
        var serviceUnit = unitName + ".service";
        var timerUnit   = unitName + ".timer";

        UnixSchedulerHelpers.Run(_log, "systemctl", new[] { "--user", "disable", "--now", timerUnit }, tolerateFailure: true);
        try
        {
            var sp = Path.Combine(UserUnitsDir, serviceUnit);
            var tp = Path.Combine(UserUnitsDir, timerUnit);
            if (File.Exists(sp)) File.Delete(sp);
            if (File.Exists(tp)) File.Delete(tp);
        }
        catch (Exception ex) { _log.Warning(ex, "Couldn't delete systemd units for {Name}", backup.Name); }
        UnixSchedulerHelpers.Run(_log, "systemctl", new[] { "--user", "daemon-reload" }, tolerateFailure: true);
        _log.Information("Removed systemd user units {Name}", unitName);
    }

    // ---- unit-file generation --------------------------------------------

    internal static string BuildService(Backup backup, string exePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Unit]");
        sb.AppendLine($"Description=Duplimate — {backup.Name}");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=oneshot");
        // Quote the args that may contain spaces / shell metas. systemd's
        // ExecStart parses with its own rules — we use the exec-form
        // (vector) syntax to avoid surprises.
        sb.AppendLine($"ExecStart=\"{ShellQuote(exePath)}\" --run \"{ShellQuote(backup.Name)}\"");
        // Match Windows' BelowNormal priority and launchd's LowPriorityIO.
        sb.AppendLine("Nice=5");
        sb.AppendLine("IOSchedulingClass=best-effort");
        sb.AppendLine("IOSchedulingPriority=7");
        return sb.ToString();
    }

    internal static string BuildTimer(Backup backup, string unitName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Unit]");
        sb.AppendLine($"Description=Schedule for {unitName}");
        sb.AppendLine();
        sb.AppendLine("[Timer]");
        sb.AppendLine($"Unit={unitName}.service");
        if (backup.Schedule.CatchUpMissedRuns) sb.AppendLine("Persistent=true");

        switch (backup.Schedule.Frequency)
        {
            case ScheduleFrequency.Hourly:
                var step = Math.Max(1, backup.Schedule.HourlyInterval);
                sb.AppendLine($"OnUnitActiveSec={step}h");
                // First-fire timer must also be set or the very first run
                // is delayed by the hourly interval.
                sb.AppendLine($"OnBootSec={step}min");
                break;

            case ScheduleFrequency.Daily:
                sb.AppendLine($"OnCalendar=*-*-* {backup.Schedule.TimeOfDay.Hours:D2}:{backup.Schedule.TimeOfDay.Minutes:D2}:00");
                break;

            case ScheduleFrequency.Weekly:
                // systemd OnCalendar accepts comma-separated day shorthands.
                var dayCsv = WeeklyDayCsv(backup.Schedule.WeeklyDaysBitmask);
                sb.AppendLine($"OnCalendar={dayCsv} *-*-* {backup.Schedule.TimeOfDay.Hours:D2}:{backup.Schedule.TimeOfDay.Minutes:D2}:00");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=timers.target");
        return sb.ToString();
    }

    private static string WeeklyDayCsv(int mask)
    {
        // Shorthand systemd accepts: Mon Tue Wed Thu Fri Sat Sun.
        // Bit layout in BackupSchedule: Sun=1, Mon=2, ..., Sat=64.
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var picked = new System.Collections.Generic.List<string>(7);
        for (int i = 0; i < 7; i++)
            if ((mask & (1 << i)) != 0) picked.Add(names[i]);
        return picked.Count == 0 ? "Mon..Sun" : string.Join(',', picked);
    }

    /// <summary>Escape a shell token for inside a double-quoted systemd ExecStart entry.</summary>
    private static string ShellQuote(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
