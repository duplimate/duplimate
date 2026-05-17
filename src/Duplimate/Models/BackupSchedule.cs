using System;

namespace Duplimate.Models;

public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Hourly,
}

/// <summary>
/// Translated into a Windows scheduled task trigger by TaskSchedulerService.
/// Deliberately smaller than cron — the UX is about beginners.
/// </summary>
public sealed class BackupSchedule
{
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;

    /// <summary>Time of day for daily/weekly triggers. TimeSpan since midnight.</summary>
    public TimeSpan TimeOfDay { get; set; } = TimeSpan.FromHours(20); // 8 PM default — matches original

    /// <summary>For weekly. Bitmask: Sunday=1, Monday=2, ..., Saturday=64.</summary>
    public int WeeklyDaysBitmask { get; set; } = 0b0111_1111; // every day

    /// <summary>For hourly. E.g. 4 = "every 4 hours".</summary>
    public int HourlyInterval { get; set; } = 4;

    /// <summary>For custom. Verbatim cron-like string (optional future use).</summary>
    public string CustomExpression { get; set; } = "";

    // ---- preconditions ----

    /// <summary>Don't run on battery (matches original scheduled-task.xml).</summary>
    public bool SkipOnBattery { get; set; } = true;

    /// <summary>Stop if going on battery mid-run.</summary>
    public bool StopOnBattery { get; set; } = true;

    /// <summary>Only run if network is available.</summary>
    public bool RequireNetwork { get; set; } = true;

    /// <summary>
    /// If the scheduled run was missed (PC off/asleep), run it as soon as the PC wakes.
    /// Maps to StartWhenAvailable in Task Scheduler.
    /// </summary>
    public bool CatchUpMissedRuns { get; set; } = true;

    /// <summary>
    /// Pause all runs until this UTC time (user-initiated "snooze"). Task remains
    /// registered but the entry point no-ops until we're past this time.
    /// </summary>
    public DateTime? PausedUntilUtc { get; set; }

    /// <summary>Max execution time before the task is killed. 72h matches original.</summary>
    public TimeSpan ExecutionTimeLimit { get; set; } = TimeSpan.FromHours(72);

    // ----------------------------------------------------------------
    // Human-friendly "next run" formatter
    // ----------------------------------------------------------------

    /// <summary>
    /// Returns a "Tonight 20:00 (Daily)"-style label for the next time
    /// this schedule will fire, computed in local time. Used by the
    /// Backups list and the Dashboard tile so the user can read off
    /// "when does this next happen" at a glance instead of decoding a
    /// cron-ish description.
    /// </summary>
    /// <param name="now">The "now" reference (local time). Caller passes
    /// <see cref="DateTime.Now"/>; an override exists for tests.</param>
    public string DescribeNextRun(DateTime now)
    {
        var next = ComputeNextFire(now);
        // The absolute portion ("Tonight 20:00") already carries the
        // time-of-day; keep the parenthetical period short by dropping
        // the "at HH:MM" suffix DescribePeriod includes. The user's
        // exact complaint: "If period is Tonight 20:00, only show
        // after (Daily), it is redundant to show Daily and the time
        // if the time is the same as the next time."
        var periodShort = DescribePeriodShort();
        if (next is null) return DescribePeriod();
        return $"{HumaniseAbsolute(next.Value, now)} ({periodShort})";
    }

    /// <summary>
    /// Period description with the time-of-day stripped — used inside
    /// the parentheses of <see cref="DescribeNextRun"/> where the
    /// absolute prefix already shows the time. Daily → "Daily";
    /// Weekly with day list → "Weekly (Mon, Fri)"; Hourly variants
    /// have no time-of-day to strip so they stay unchanged.
    /// </summary>
    private string DescribePeriodShort()
    {
        return Frequency switch
        {
            ScheduleFrequency.Daily  => "Daily",
            ScheduleFrequency.Weekly => DescribeWeeklyShort(),
            ScheduleFrequency.Hourly => HourlyInterval == 1 ? "Hourly" : $"Every {HourlyInterval}h",
            _                        => Frequency.ToString(),
        };
    }

    private string DescribeWeeklyShort()
    {
        if (WeeklyDaysBitmask == 0) return "Weekly";
        if (WeeklyDaysBitmask == 0b0111_1111) return "Daily";

        var days = new System.Collections.Generic.List<string>(7);
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        for (int i = 0; i < 7; i++)
            if ((WeeklyDaysBitmask & (1 << i)) != 0) days.Add(names[i]);
        return $"Weekly ({string.Join(", ", days)})";
    }

    /// <summary>
    /// "Daily at 20:00", "Weekly at 14:00 (Mon, Fri)", "Every 4h"
    /// — full-fat periodicity blurb used inside the parentheses next to
    /// the next-fire absolute. Was previously just "Daily" / "Weekly" /
    /// "Every 4h" which dropped the time-of-day info that the absolute
    /// already shows; user feedback was that the parens were redundant
    /// when the time wasn't explicit. Time format respects the user's
    /// Windows locale (12h vs 24h).
    /// </summary>
    public string DescribePeriod()
    {
        var time = FormatLocalTime(TimeOfDay);
        return Frequency switch
        {
            ScheduleFrequency.Daily  => $"Daily at {time}",
            ScheduleFrequency.Weekly => DescribeWeekly(time),
            ScheduleFrequency.Hourly => HourlyInterval == 1 ? "Hourly" : $"Every {HourlyInterval}h",
            _                        => Frequency.ToString(),
        };
    }

    private string DescribeWeekly(string time)
    {
        // No bits set is a degenerate state, fall back gracefully.
        if (WeeklyDaysBitmask == 0) return $"Weekly at {time}";
        if (WeeklyDaysBitmask == 0b0111_1111) return $"Daily at {time}";

        var days = new System.Collections.Generic.List<string>(7);
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        for (int i = 0; i < 7; i++)
            if ((WeeklyDaysBitmask & (1 << i)) != 0) days.Add(names[i]);
        return $"Weekly at {time} ({string.Join(", ", days)})";
    }

    /// <summary>
    /// Format a TimeOfDay using the current Windows short-time pattern.
    /// Honors the user's 12h vs 24h preference (Region settings) — what
    /// the user sees in the time picker is what they see in the schedule
    /// description. Falls back to 24h on any culture-conversion error
    /// rather than failing the whole formatter.
    /// </summary>
    private static string FormatLocalTime(TimeSpan t)
    {
        try
        {
            var d = DateTime.Today.Add(t);
            var fmt = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
            return d.ToString(fmt, System.Globalization.CultureInfo.CurrentCulture);
        }
        catch
        {
            return t.ToString(@"hh\:mm");
        }
    }

    /// <summary>
    /// Compute the next concrete fire time after <paramref name="now"/>.
    /// Returns null for frequencies we can't predict (custom expressions
    /// — currently unused, but defensive).
    /// </summary>
    public DateTime? ComputeNextFire(DateTime now)
    {
        switch (Frequency)
        {
            case ScheduleFrequency.Daily:
            {
                var todayAt = now.Date.Add(TimeOfDay);
                return todayAt > now ? todayAt : todayAt.AddDays(1);
            }
            case ScheduleFrequency.Hourly:
            {
                var step = Math.Max(1, HourlyInterval);
                // Next aligned hour boundary at TimeOfDay.Minutes past the hour.
                var minute = TimeOfDay.Minutes;
                var anchor = new DateTime(now.Year, now.Month, now.Day, now.Hour, minute, 0, now.Kind);
                if (anchor <= now) anchor = anchor.AddHours(1);
                // Step forward in `step`-sized hops from the day's start
                // anchor so the cadence stays predictable.
                var dayStart = now.Date.Add(TimeOfDay);
                while (anchor < dayStart) anchor = anchor.AddHours(step);
                while ((anchor - dayStart).TotalHours % step != 0)
                    anchor = anchor.AddHours(1);
                return anchor;
            }
            case ScheduleFrequency.Weekly:
            {
                // Walk forward up to 7 days to find the first day that's
                // ticked in the bitmask AND whose TimeOfDay is in the
                // future of `now`.
                for (int delta = 0; delta < 8; delta++)
                {
                    var candidate = now.Date.AddDays(delta).Add(TimeOfDay);
                    var dow = (int)candidate.DayOfWeek; // Sunday = 0
                    var dayBit = 1 << dow;
                    if ((WeeklyDaysBitmask & dayBit) == 0) continue;
                    if (candidate > now) return candidate;
                }
                return null;
            }
            default:
                return null;
        }
    }

    private static string HumaniseAbsolute(DateTime when, DateTime now)
    {
        var date = when.Date;
        var today = now.Date;
        // Respect user's locale time format — Windows users on US locales
        // see "8:00 PM", on EU locales "20:00".
        var time = FormatLocalTime(when.TimeOfDay);

        if (date == today)
        {
            // "Tonight" reads better than "Today" once the user's mental
            // model is "evening". Below noon → "this morning", noon-18 →
            // "this afternoon", 18+ → "tonight".
            return when.Hour switch
            {
                <  6 => $"Early today {time}",
                < 12 => $"This morning {time}",
                < 18 => $"This afternoon {time}",
                _    => $"Tonight {time}",
            };
        }
        if (date == today.AddDays(1)) return $"Tomorrow {time}";
        if ((date - today).TotalDays < 7)
            return $"{when:ddd} {time}";
        return $"{when:ddd dd MMM} {time}";
    }
}
