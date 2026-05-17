using Duplimate.Models;
using Duplimate.Services.Platform.Unix;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// Unit tests for the cross-platform scheduler implementations. Exercise
/// the pure plist / unit-file generation surfaces — they're internal
/// helpers but we expose them via <c>InternalsVisibleTo</c> so we can
/// pin the wire format without spinning up real launchd / systemd
/// daemons. The tests run on a Windows host (which is what CI has);
/// they're testing string-generation logic that's host-independent.
/// </summary>
public class PlatformSchedulerTests
{
    // ---- macOS launchd ---------------------------------------------------

    [Fact]
    public void MacLaunchd_label_is_reverse_dns_with_slugged_name()
    {
        var s = new MacLaunchdScheduler();
        var label = s.TaskNameFor(new Backup { Name = "My Documents" });
        Assert.StartsWith(MacLaunchdScheduler.LabelPrefix, label);
        // Spaces collapse to dashes so the label is filesystem-safe
        // (it's also used to derive the .plist filename).
        Assert.Equal("com.duplimate.my-documents", label);
    }

    [Fact]
    public void MacLaunchd_daily_plist_pins_calendar_interval()
    {
        var b = new Backup { Name = "docs" };
        b.Schedule.Frequency = ScheduleFrequency.Daily;
        b.Schedule.TimeOfDay = new System.TimeSpan(20, 30, 0);
        b.Schedule.CatchUpMissedRuns = true;

        var plist = MacLaunchdScheduler.BuildPlist("com.duplimate.docs", b, "/Applications/Duplimate");

        Assert.Contains("<key>Label</key>", plist);
        Assert.Contains("<string>com.duplimate.docs</string>", plist);
        Assert.Contains("<string>--run</string>", plist);
        Assert.Contains("<string>docs</string>", plist);
        Assert.Contains("<string>/Applications/Duplimate</string>", plist);
        Assert.Contains("<key>StartCalendarInterval</key>", plist);
        Assert.Contains("<key>Hour</key>   <integer>20</integer>", plist);
        Assert.Contains("<key>Minute</key> <integer>30</integer>", plist);
        Assert.Contains("<key>StartWhenAvailable</key>", plist);
    }

    [Fact]
    public void MacLaunchd_hourly_plist_uses_StartInterval_seconds()
    {
        var b = new Backup { Name = "docs" };
        b.Schedule.Frequency = ScheduleFrequency.Hourly;
        b.Schedule.HourlyInterval = 4;

        var plist = MacLaunchdScheduler.BuildPlist("com.duplimate.docs", b, "/usr/local/bin/Duplimate");

        Assert.Contains("<key>StartInterval</key>", plist);
        Assert.Contains("<integer>14400</integer>", plist); // 4h * 3600s
        Assert.DoesNotContain("StartCalendarInterval", plist);
    }

    [Fact]
    public void MacLaunchd_weekly_plist_emits_one_dict_per_day_bit()
    {
        var b = new Backup { Name = "docs" };
        b.Schedule.Frequency = ScheduleFrequency.Weekly;
        b.Schedule.TimeOfDay = new System.TimeSpan(2, 0, 0);
        // Mon (bit 1) + Fri (bit 5) = 0b0100010 = 34
        b.Schedule.WeeklyDaysBitmask = (1 << 1) | (1 << 5);

        var plist = MacLaunchdScheduler.BuildPlist("com.duplimate.docs", b, "/Applications/Duplimate");

        Assert.Contains("<key>Weekday</key> <integer>1</integer>", plist);
        Assert.Contains("<key>Weekday</key> <integer>5</integer>", plist);
        Assert.DoesNotContain("<integer>0</integer></key>", plist); // sanity
        // Two Hour entries (one per day) — one outer array, two dicts.
        Assert.Contains("<key>Hour</key>    <integer>2</integer>", plist);
    }

    [Fact]
    public void MacLaunchd_plist_xml_escapes_metacharacters_in_names()
    {
        // Backup names are constrained to [A-Za-z0-9_-] at validation
        // time, but the plist generator should still emit XML-safe
        // output if a future relaxation lets through an ampersand or
        // angle bracket in the args / label.
        var b = new Backup { Name = "ampersand-test" };
        b.Schedule.Frequency = ScheduleFrequency.Daily;
        b.Schedule.TimeOfDay = System.TimeSpan.FromHours(20);

        var plist = MacLaunchdScheduler.BuildPlist("com.duplimate.test", b, "/path/with&special<chars>/Duplimate");

        // Ampersand AND angle brackets all encoded — anything less
        // produces a plist that launchctl rejects.
        Assert.Contains("/path/with&amp;special&lt;chars&gt;/Duplimate", plist);
        // The raw ampersand must not appear unescaped anywhere in the
        // emitted XML body. (Search for "&" not followed by an entity
        // name like "amp;" / "lt;" / "gt;".)
        Assert.DoesNotMatch(@"&(?!amp;|lt;|gt;|quot;|apos;)", plist);
    }

    // ---- Linux systemd ---------------------------------------------------

    [Fact]
    public void LinuxSystemd_unit_name_is_prefixed_and_slugged()
    {
        var s = new LinuxSystemdScheduler();
        var name = s.TaskNameFor(new Backup { Name = "My Documents" });
        Assert.Equal(LinuxSystemdScheduler.UnitPrefix + "my-documents", name);
    }

    [Fact]
    public void LinuxSystemd_service_unit_has_oneshot_and_quoted_paths()
    {
        var b = new Backup { Name = "docs" };
        var svc = LinuxSystemdScheduler.BuildService(b, "/opt/duplimate backup/Duplimate");

        Assert.Contains("[Service]", svc);
        Assert.Contains("Type=oneshot", svc);
        // ExecStart double-quotes both the binary path AND the backup
        // name so spaces / quotes round-trip cleanly through systemd's
        // own arg parser.
        Assert.Contains("ExecStart=\"/opt/duplimate backup/Duplimate\" --run \"docs\"", svc);
        // Below-normal priority + best-effort IO match the
        // Windows / launchd implementations.
        Assert.Contains("Nice=5", svc);
        Assert.Contains("IOSchedulingClass=best-effort", svc);
    }

    [Fact]
    public void LinuxSystemd_daily_timer_uses_OnCalendar_with_zero_padded_time()
    {
        var b = new Backup { Name = "docs" };
        b.Schedule.Frequency = ScheduleFrequency.Daily;
        b.Schedule.TimeOfDay = new System.TimeSpan(2, 5, 0);
        b.Schedule.CatchUpMissedRuns = true;

        var timer = LinuxSystemdScheduler.BuildTimer(b, "duplimate-docs");

        Assert.Contains("[Timer]", timer);
        Assert.Contains("Unit=duplimate-docs.service", timer);
        Assert.Contains("Persistent=true", timer);
        Assert.Contains("OnCalendar=*-*-* 02:05:00", timer);
        Assert.Contains("WantedBy=timers.target", timer);
    }

    [Fact]
    public void LinuxSystemd_hourly_timer_uses_OnUnitActiveSec_and_OnBootSec()
    {
        var b = new Backup { Name = "docs" };
        b.Schedule.Frequency = ScheduleFrequency.Hourly;
        b.Schedule.HourlyInterval = 6;

        var timer = LinuxSystemdScheduler.BuildTimer(b, "duplimate-docs");

        Assert.Contains("OnUnitActiveSec=6h", timer);
        // First-fire shim — OnUnitActiveSec only fires after the unit
        // has been active once, so OnBootSec gets the very first tick
        // going.
        Assert.Contains("OnBootSec=6min", timer);
        Assert.DoesNotContain("OnCalendar=", timer);
    }

    [Fact]
    public void LinuxSystemd_weekly_timer_csv_picks_active_days()
    {
        var b = new Backup { Name = "docs" };
        b.Schedule.Frequency = ScheduleFrequency.Weekly;
        b.Schedule.TimeOfDay = new System.TimeSpan(20, 0, 0);
        // Mon (bit 1) + Wed (bit 3) + Fri (bit 5)
        b.Schedule.WeeklyDaysBitmask = (1 << 1) | (1 << 3) | (1 << 5);

        var timer = LinuxSystemdScheduler.BuildTimer(b, "duplimate-docs");

        Assert.Contains("OnCalendar=Mon,Wed,Fri *-*-* 20:00:00", timer);
    }

    [Fact]
    public void LinuxSystemd_weekly_with_no_days_falls_back_to_full_range()
    {
        var b = new Backup { Name = "docs" };
        b.Schedule.Frequency = ScheduleFrequency.Weekly;
        b.Schedule.WeeklyDaysBitmask = 0;
        b.Schedule.TimeOfDay = System.TimeSpan.Zero;

        var timer = LinuxSystemdScheduler.BuildTimer(b, "duplimate-docs");

        // No bits set is a degenerate state — fall back to "every day"
        // rather than emit a malformed empty CSV that systemd would
        // reject.
        Assert.Contains("OnCalendar=Mon..Sun *-*-* 00:00:00", timer);
    }
}
