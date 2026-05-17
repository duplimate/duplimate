using System;
using System.Globalization;
using Duplimate.Models;
using Xunit;

namespace Duplimate.Tests.Models;

/// <summary>
/// Regression tests for the next-run + period describer. The user
/// asked for the parens to carry the full periodicity (not just
/// "Daily") and for the time format to respect the user's locale.
/// These tests pin both behaviors so a future refactor doesn't
/// silently regress them back to "Daily" / hardcoded HH:mm.
/// </summary>
public class BackupScheduleDescribeTests
{
    [Fact]
    public void DescribePeriod_Daily_includesTimeOfDay()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Daily,
            TimeOfDay = TimeSpan.FromHours(20),
        };
        // Period uses the current culture's short-time pattern, so we
        // just assert the prefix + presence of a time token.
        var period = s.DescribePeriod();
        Assert.StartsWith("Daily at ", period);
        Assert.Contains("20", period); // 24h locales render "20:00"; 12h "8:00 PM" still contains "0".
    }

    [Fact]
    public void DescribePeriod_Hourly_doesNotShowTimeOfDay()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Hourly,
            HourlyInterval = 4,
        };
        Assert.Equal("Every 4h", s.DescribePeriod());
    }

    [Fact]
    public void DescribePeriod_Hourly_intervalOne_isHourly()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Hourly,
            HourlyInterval = 1,
        };
        Assert.Equal("Hourly", s.DescribePeriod());
    }

    [Fact]
    public void DescribePeriod_WeeklyEveryDay_collapsesToDailyAt()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Weekly,
            TimeOfDay = TimeSpan.FromHours(14),
            WeeklyDaysBitmask = 0b0111_1111,
        };
        Assert.StartsWith("Daily at ", s.DescribePeriod());
    }

    [Fact]
    public void DescribePeriod_WeeklyMonFri_listsDays()
    {
        // Mon=2, Fri=32 → 0b0010_0010 = 34
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Weekly,
            TimeOfDay = TimeSpan.FromHours(14),
            WeeklyDaysBitmask = (1 << 1) | (1 << 5),
        };
        var period = s.DescribePeriod();
        Assert.Contains("Weekly at ", period);
        Assert.Contains("Mon", period);
        Assert.Contains("Fri", period);
    }

    [Fact]
    public void DescribeNextRun_includesAbsoluteAndPeriodInParens()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Daily,
            TimeOfDay = TimeSpan.FromHours(20),
        };
        // 14:00 today → next fire is "tonight 20:00" (still on the same day)
        var now = DateTime.Today.Add(TimeSpan.FromHours(14));
        var label = s.DescribeNextRun(now);
        // The parens must say "(Daily)" — without the "at HH:MM" suffix
        // since the absolute prefix already carries the time. The user
        // flagged the previous "(Daily at 20:00)" trailing the absolute
        // "Tonight 20:00" as redundant: showing the same time twice on
        // the same line.
        Assert.Contains("(Daily)", label);
        Assert.DoesNotContain("Daily at", label);
    }
}
