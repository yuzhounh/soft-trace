namespace SoftTrace.Core;

public sealed record UsageHour(int Hour, double ActiveSeconds, double RecordedSeconds, int RecordedDays, double? AverageMinutes);
public sealed record UsageWeekday(int Weekday, string Label, double ActiveSeconds, int RecordedDays, double? AverageHours);
public sealed record AcademicUsage(string Kind, string Label, double ActiveSeconds, int RecordedDays, double? AverageHours);
public sealed record UsagePatternSummary(IReadOnlyList<UsageHour> Hours, IReadOnlyList<UsageWeekday> Weekdays,
    IReadOnlyList<AcademicUsage> AcademicPeriods, int RecordedDays, int MissingDays, double RecordedSeconds,
    string TimeZone, string CalendarBasis, IReadOnlyList<AcademicCalendarSource> CalendarSources);

internal sealed record UsagePatternDay(DateOnly Date, double[] ActiveHours, double[] RecordedHours)
{
    public double ActiveSeconds => ActiveHours.Sum();
    public double RecordedSeconds => RecordedHours.Sum();
}

internal static class UsagePatterns
{
    public static UsagePatternDay Day(DateOnly date, DailyTimeline timeline, TimeZoneInfo zone)
    {
        var active = new double[24];
        var recorded = new double[24];
        foreach (var state in timeline.States)
        {
            if (state.Kind is "unknown" or "fault" or "unavailable" or "disconnected") continue;
            for (var cursor = state.StartUtc; cursor < state.EndUtc;)
            {
                var local = TimeZoneInfo.ConvertTime(cursor, zone);
                var until = cursor.AddTicks(TimeSpan.TicksPerHour - local.TimeOfDay.Ticks % TimeSpan.TicksPerHour);
                if (until > state.EndUtc) until = state.EndUtc;
                var seconds = (until - cursor).TotalSeconds;
                recorded[local.Hour] += seconds;
                if (state.Kind == "active") active[local.Hour] += seconds;
                cursor = until;
            }
        }
        return new(date, active, recorded);
    }

    public static UsagePatternSummary Summarize(IReadOnlyList<UsagePatternDay> days, TimeZoneInfo zone)
    {
        var observed = days.Where(day => day.RecordedSeconds > 0).ToArray();
        var hours = Enumerable.Range(0, 24).Select(hour =>
        {
            var count = observed.Count(day => day.RecordedHours[hour] > 0);
            var seconds = observed.Sum(day => day.ActiveHours[hour]);
            return new UsageHour(hour, seconds, observed.Sum(day => day.RecordedHours[hour]), count,
                observed.Length == 0 ? null : seconds / observed.Length / 60);
        }).ToArray();
        string[] names = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
        var weekdays = Enumerable.Range(0, 7).Select(index =>
        {
            var matches = observed.Where(day => ((int)day.Date.DayOfWeek + 6) % 7 == index).ToArray();
            var seconds = matches.Sum(day => day.ActiveSeconds);
            return new UsageWeekday(index, names[index], seconds, matches.Length,
                matches.Length == 0 ? null : seconds / matches.Length / 3600);
        }).ToArray();
        (string Key, string Label)[] categories = [("spring", "春季学期"), ("autumn", "秋季学期"),
            ("winter", "寒假"), ("summer", "暑假"), ("unknown", "未分类")];
        var academic = categories.Select(category =>
        {
            var matches = observed.Where(day => AcademicCalendar.Classify(day.Date) == category.Key).ToArray();
            var seconds = matches.Sum(day => day.ActiveSeconds);
            return new AcademicUsage(category.Key, category.Label, seconds, matches.Length,
                matches.Length == 0 ? null : seconds / matches.Length / 3600);
        }).ToArray();
        return new(hours, weekdays, academic, observed.Length, days.Count - observed.Length,
            observed.Sum(day => day.RecordedSeconds), zone.Id,
            "按固定月份分组：寒假 2 月，暑假 7–8 月，春季学期 3–6 月，秋季学期 9 月至次年 1 月。",
            AcademicCalendar.Sources);
    }
}
