using System;
using System.IO;
using System.Text;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services.Platform.Unix;

/// <summary>
/// macOS scheduler — emits a per-backup launchd <c>plist</c> under
/// <c>~/Library/LaunchAgents/</c> and bootstraps it via
/// <c>launchctl bootstrap gui/&lt;uid&gt;</c>. This is the modern
/// (10.11+) replacement for <c>launchctl load</c>; bootstrapping into
/// the GUI domain means the agent runs only when the user is logged
/// in, which matches Windows' "S4U: only when interactive session
/// available" semantics for our purposes.
///
/// <para>
/// Trigger model:
/// <list type="bullet">
///   <item><c>Daily</c> / <c>Weekly</c> → <c>StartCalendarInterval</c>
///         entries pinned to the user's wall-clock TimeOfDay.</item>
///   <item><c>Hourly</c> → <c>StartInterval</c> in seconds
///         (<c>HourlyInterval × 3600</c>).</item>
/// </list>
/// </para>
///
/// <para>
/// Battery / network gating: launchd doesn't expose first-class
/// "skip on battery" or "require network" predicates the way Windows
/// Task Scheduler does. We set <c>RunAtLoad=false</c> and rely on the
/// app's in-process pre-flight (<see cref="MeteredNetworkWatchdog"/>
/// / battery probe) to skip a fired-but-unwanted run. <c>LowPriorityIO</c>
/// + <c>Nice</c> approximate Windows' BelowNormal priority.
/// </para>
/// </summary>
public sealed class MacLaunchdScheduler : IScheduler
{
    private static ILogger _log => AppLogger.For<MacLaunchdScheduler>();

    /// <summary>Reverse-DNS prefix for plist labels. Stays stable so
    /// existing agents are recognized after an app upgrade.</summary>
    public const string LabelPrefix = "com.duplimate.";

    private static string LaunchAgentsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents");

    public string TaskNameFor(Backup backup) => LabelPrefix + UnixSchedulerHelpers.Slug(backup.Name);

    public void UpsertTask(Backup backup, string exePath)
    {
        var label = TaskNameFor(backup);
        var plistPath = Path.Combine(LaunchAgentsDir, label + ".plist");
        Directory.CreateDirectory(LaunchAgentsDir);

        var plist = BuildPlist(label, backup, exePath);
        UnixSchedulerHelpers.AtomicWriteText(plistPath, plist);

        _log.Information(
            "Upserting launchd agent: label={Label} path={Path} freq={Freq} timeOfDay={Tod}",
            label, plistPath, backup.Schedule.Frequency, backup.Schedule.TimeOfDay);

        // Reload by bootout + bootstrap so a definition change actually
        // takes effect — a plain bootstrap on an existing label is a
        // no-op. bootout returns 5 ("not loaded") if the agent wasn't
        // running, which is fine; we tolerate any non-zero exit.
        var domain = $"gui/{Geteuid()}";
        UnixSchedulerHelpers.Run(_log, "launchctl", new[] { "bootout",   $"{domain}/{label}" }, tolerateFailure: true);
        UnixSchedulerHelpers.Run(_log, "launchctl", new[] { "bootstrap", domain, plistPath },   tolerateFailure: false);
        UnixSchedulerHelpers.Run(_log, "launchctl", new[] { "enable",    $"{domain}/{label}" }, tolerateFailure: true);
    }

    public void DeleteTask(Backup backup)
    {
        var label = TaskNameFor(backup);
        var plistPath = Path.Combine(LaunchAgentsDir, label + ".plist");

        UnixSchedulerHelpers.Run(_log, "launchctl", new[] { "bootout", $"gui/{Geteuid()}/{label}" }, tolerateFailure: true);
        try { if (File.Exists(plistPath)) File.Delete(plistPath); }
        catch (Exception ex) { _log.Warning(ex, "Couldn't delete plist {Path}", plistPath); }
        _log.Information("Removed launchd agent {Label}", label);
    }

    // ---- plist generation -------------------------------------------------

    internal static string BuildPlist(string label, Backup backup, string exePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\">");
        sb.AppendLine("<dict>");
        sb.AppendLine("    <key>Label</key>");
        sb.AppendLine($"    <string>{Esc(label)}</string>");
        sb.AppendLine("    <key>ProgramArguments</key>");
        sb.AppendLine("    <array>");
        sb.AppendLine($"        <string>{Esc(exePath)}</string>");
        sb.AppendLine("        <string>--run</string>");
        sb.AppendLine($"        <string>{Esc(backup.Name)}</string>");
        sb.AppendLine("    </array>");
        sb.AppendLine("    <key>RunAtLoad</key>");
        sb.AppendLine("    <false/>");
        sb.AppendLine("    <key>LowPriorityIO</key>");
        sb.AppendLine("    <true/>");
        sb.AppendLine("    <key>Nice</key>");
        sb.AppendLine("    <integer>5</integer>");
        // KeepAlive false: a one-shot run, not a daemon.
        sb.AppendLine("    <key>KeepAlive</key>");
        sb.AppendLine("    <false/>");
        if (backup.Schedule.CatchUpMissedRuns)
        {
            // Without this, a firing missed while the Mac was asleep is
            // dropped on the floor — closest analogue to Windows Task
            // Scheduler's StartWhenAvailable.
            sb.AppendLine("    <key>StartWhenAvailable</key>");
            sb.AppendLine("    <true/>");
        }

        AppendTrigger(sb, backup.Schedule);

        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }

    private static void AppendTrigger(StringBuilder sb, BackupSchedule s)
    {
        switch (s.Frequency)
        {
            case ScheduleFrequency.Hourly:
                var seconds = Math.Max(1, s.HourlyInterval) * 3600;
                sb.AppendLine("    <key>StartInterval</key>");
                sb.AppendLine($"    <integer>{seconds}</integer>");
                break;

            case ScheduleFrequency.Daily:
                sb.AppendLine("    <key>StartCalendarInterval</key>");
                sb.AppendLine("    <dict>");
                sb.AppendLine($"        <key>Hour</key>   <integer>{s.TimeOfDay.Hours}</integer>");
                sb.AppendLine($"        <key>Minute</key> <integer>{s.TimeOfDay.Minutes}</integer>");
                sb.AppendLine("    </dict>");
                break;

            case ScheduleFrequency.Weekly:
                // launchd accepts an array of dicts — one per fire-day.
                sb.AppendLine("    <key>StartCalendarInterval</key>");
                sb.AppendLine("    <array>");
                for (int i = 0; i < 7; i++)
                {
                    if ((s.WeeklyDaysBitmask & (1 << i)) == 0) continue;
                    sb.AppendLine("        <dict>");
                    sb.AppendLine($"            <key>Weekday</key> <integer>{i}</integer>");
                    sb.AppendLine($"            <key>Hour</key>    <integer>{s.TimeOfDay.Hours}</integer>");
                    sb.AppendLine($"            <key>Minute</key>  <integer>{s.TimeOfDay.Minutes}</integer>");
                    sb.AppendLine("        </dict>");
                }
                sb.AppendLine("    </array>");
                break;
        }
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static uint Geteuid()
    {
        try { return geteuid(); }
        catch { return 501; /* default macOS first user */ }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint geteuid();
}
