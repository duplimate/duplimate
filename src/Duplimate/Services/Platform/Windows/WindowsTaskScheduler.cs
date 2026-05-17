using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32.TaskScheduler;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services.Platform.Windows;

/// <summary>
/// Windows Task Scheduler implementation of <see cref="IScheduler"/>.
/// Matches the original scheduled-task.xml shape:
///   - S4U (runs whether user is logged on or not, no password stored)
///   - Highest run level (needed for VSS on C:\)
///   - StopIfGoingOnBatteries, DisallowStartIfOnBatteries
///   - RunOnlyIfNetworkAvailable
///   - StartWhenAvailable (catch up missed runs)
///   - ExecutionTimeLimit 72h
///
/// Tasks live under \MyTasks\Duplimate\ so users can find them
/// easily in Task Scheduler MMC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskScheduler : IScheduler
{
    public const string FolderPath = @"\MyTasks\Duplimate";
    private static ILogger _log => AppLogger.For<WindowsTaskScheduler>();

    public string TaskNameFor(Backup backup) => $"EDB — {backup.Name}";

    public void UpsertTask(Backup backup, string exePath)
    {
        using var ts = new TaskService();
        var folder = ts.RootFolder.CreateFolder(FolderPath, exceptionOnExists: false);

        _log.Information(
            "Registering scheduled task: name={TaskName} freq={Frequency} timeOfDay={TimeOfDay} hourly={Hourly} skipOnBattery={SkipOnBattery} stopOnBattery={StopOnBattery} requireNetwork={RequireNetwork} catchUp={CatchUp}",
            TaskNameFor(backup), backup.Schedule.Frequency, backup.Schedule.TimeOfDay, backup.Schedule.HourlyInterval,
            backup.Schedule.SkipOnBattery, backup.Schedule.StopOnBattery, backup.Schedule.RequireNetwork, backup.Schedule.CatchUpMissedRuns);

        var td = ts.NewTask();
        td.RegistrationInfo.Description = $"Duplimate — {backup.Name}";
        td.RegistrationInfo.Author = "Duplimate";

        // Principal
        var userId = $@"{Environment.UserDomainName}\{Environment.UserName}";
        td.Principal.UserId = userId;
        td.Principal.LogonType = TaskLogonType.S4U;
        td.Principal.RunLevel = TaskRunLevel.Highest;

        // Settings
        td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        td.Settings.StartWhenAvailable = backup.Schedule.CatchUpMissedRuns;
        td.Settings.RunOnlyIfNetworkAvailable = backup.Schedule.RequireNetwork;
        td.Settings.DisallowStartIfOnBatteries = backup.Schedule.SkipOnBattery;
        td.Settings.StopIfGoingOnBatteries = backup.Schedule.StopOnBattery;
        td.Settings.Hidden = true;
        td.Settings.Priority = System.Diagnostics.ProcessPriorityClass.BelowNormal;
        td.Settings.ExecutionTimeLimit = backup.Schedule.ExecutionTimeLimit;
        td.Settings.WakeToRun = false;
        td.Settings.RunOnlyIfIdle = false;
        td.Settings.AllowHardTerminate = true;
        td.Settings.UseUnifiedSchedulingEngine = true;
        td.Settings.Enabled = backup.Enabled;

        // Trigger
        td.Triggers.Clear();
        var trigger = BuildTrigger(backup.Schedule);
        if (trigger is not null) td.Triggers.Add(trigger);

        // Action — cmd.exe wrapper that prefers the user's main exe but
        // falls back to the stub at CanonicalInstaller.StubExePath when
        // the main exe is missing (typical case: user moved the .exe
        // and forgot to reopen the app). The stub does ONE thing: pop a
        // "Duplimate needs attention — open the app from its new
        // location" warning. Re-opening the main app then re-upserts
        // every task to the new path (CanonicalInstaller.ReupsertIfExePathChanged).
        td.Actions.Clear();
        td.Actions.Add(BuildExecAction(exePath, CanonicalInstaller.StubExePath, backup.Name));

        folder.RegisterTaskDefinition(
            path: TaskNameFor(backup),
            definition: td,
            createType: TaskCreation.CreateOrUpdate,
            userId: userId,
            logonType: TaskLogonType.S4U);
    }

    public void DeleteTask(Backup backup)
    {
        using var ts = new TaskService();
        var folder = ts.GetFolder(FolderPath);
        if (folder is null)
        {
            _log.Debug("DeleteTask: folder {Folder} doesn't exist; nothing to remove for {TaskName}", FolderPath, TaskNameFor(backup));
            return;
        }

        var existing = folder.GetTasks(new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(TaskNameFor(backup)) + "$"));
        var deleted = 0;
        foreach (var t in existing)
        {
            folder.DeleteTask(t.Name, exceptionOnNotExists: false);
            deleted++;
        }
        _log.Information("Removed {Count} scheduled task entry(ies) for {TaskName}", deleted, TaskNameFor(backup));
    }

    private static Trigger? BuildTrigger(BackupSchedule s)
    {
        // Anchor the StartBoundary on the NEXT occurrence of TimeOfDay,
        // not always Today. If the user registers a daily 02:00 trigger
        // at 09:00, Today.Add(02:00) is in the past — Task Scheduler
        // marks the trigger as already-fired-today and the user sees
        // "no run scheduled for today" until midnight rolls over.
        // StartWhenAvailable on the task settings does catch most of
        // these up, but a fresh next-occurrence anchor is still cleaner
        // (and doesn't depend on the catch-up logic firing in time).
        var next = NextOccurrence(s.TimeOfDay);
        return s.Frequency switch
        {
            ScheduleFrequency.Daily => new DailyTrigger
            {
                StartBoundary = next,
                DaysInterval = 1,
            },
            ScheduleFrequency.Hourly => new TimeTrigger
            {
                StartBoundary = next,
                Repetition = new RepetitionPattern(TimeSpan.FromHours(Math.Max(1, s.HourlyInterval)), TimeSpan.Zero),
            },
            ScheduleFrequency.Weekly => new WeeklyTrigger
            {
                StartBoundary = next,
                WeeksInterval = 1,
                DaysOfWeek = BitmaskToDays(s.WeeklyDaysBitmask),
            },
            _ => null,
        };
    }

    /// <summary>
    /// First wall-clock instant at or after <see cref="DateTime.Now"/>
    /// that lands on the configured time-of-day. Today if the slot
    /// hasn't passed yet, tomorrow if it has.
    /// </summary>
    private static DateTime NextOccurrence(TimeSpan timeOfDay)
    {
        var todayAt = DateTime.Today.Add(timeOfDay);
        return todayAt > DateTime.Now ? todayAt : todayAt.AddDays(1);
    }

    private static DaysOfTheWeek BitmaskToDays(int mask)
    {
        DaysOfTheWeek d = 0;
        if ((mask & 1)  != 0) d |= DaysOfTheWeek.Sunday;
        if ((mask & 2)  != 0) d |= DaysOfTheWeek.Monday;
        if ((mask & 4)  != 0) d |= DaysOfTheWeek.Tuesday;
        if ((mask & 8)  != 0) d |= DaysOfTheWeek.Wednesday;
        if ((mask & 16) != 0) d |= DaysOfTheWeek.Thursday;
        if ((mask & 32) != 0) d |= DaysOfTheWeek.Friday;
        if ((mask & 64) != 0) d |= DaysOfTheWeek.Saturday;
        return d;
    }

    /// <summary>
    /// Builds the cmd.exe-wrapped ExecAction. The wrapper pattern:
    /// <code>
    /// cmd /D /S /C "if exist ""MAIN"" ( ""MAIN"" --run ""NAME"" ) else ( ""STUB"" )"
    /// </code>
    /// <para>
    /// Why cmd: it's the smallest dependable scripting layer present on
    /// every Windows install. /D skips AutoRun, /S preserves the whole
    /// quoted command verbatim (necessary because we have multiple
    /// quoted paths inside), /C runs and exits.
    /// </para>
    /// <para>
    /// Quoting: inside cmd /S /C "...", a literal " is written as "".
    /// So each "..." quoted path here is rendered as ""..."" in the
    /// argument string. Backup names are restricted to ^[A-Za-z0-9_-]+$
    /// at validation time, so they can't contain shell metacharacters.
    /// Path arguments come from Environment.ProcessPath / canonical
    /// stub path; on Windows neither contains a " character.
    /// </para>
    /// <para>
    /// Internal so the unit test can assert the exact command-line shape
    /// without needing a real Task Scheduler environment.
    /// </para>
    /// </summary>
    internal static ExecAction BuildExecAction(string mainExe, string stubExe, string backupName)
    {
        var args = BuildCmdWrapperArguments(mainExe, stubExe, backupName);
        var workDir = Path.GetDirectoryName(mainExe);
        if (string.IsNullOrEmpty(workDir)) workDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ExecAction("cmd.exe", args, workDir);
    }

    internal static string BuildCmdWrapperArguments(string mainExe, string stubExe, string backupName)
    {
        var qm = CmdQuote(mainExe);
        var qs = CmdQuote(stubExe);
        var qn = CmdQuote(backupName);
        return $@"/D /S /C ""if exist {qm} ( {qm} --run {qn} ) else ( {qs} )""";
    }

    /// <summary>
    /// Quote a token for inclusion inside the outer <c>cmd /S /C "..."</c>
    /// string. Each literal <c>"</c> becomes <c>""</c> per cmd's quoting
    /// rule for the quoted command.
    /// </summary>
    private static string CmdQuote(string s) => "\"\"" + s.Replace("\"", "\"\"\"\"") + "\"\"";
}
