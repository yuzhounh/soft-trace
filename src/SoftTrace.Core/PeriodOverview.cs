using System.Globalization;

namespace SoftTrace.Core;

public sealed record OverviewDay(string Date, double ActiveSeconds, double IdleSeconds);
public sealed record OverviewApp(string AppName, string ProcessName, double Seconds, int SegmentCount);
public sealed record PeriodOverview(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<OverviewDay> Days, IReadOnlyList<OverviewApp> Apps,
    double ActiveSeconds, double IdleSeconds, int ActiveDays, int SegmentCount, UsagePatternSummary? Patterns = null);

public sealed partial class ActivityStore
{
    public async Task<PeriodOverview> GetPeriodOverviewAsync(string deviceId, DateTimeOffset? startUtc,
        DateTimeOffset endUtc, TimeZoneInfo zone, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var earliest = startUtc ?? await GetEarliestActivityUtcAsync(deviceId, cancellationToken) ?? endUtc.AddDays(-1);
        var localStart = TimeZoneInfo.ConvertTime(earliest, zone).Date;
        DateTimeOffset Boundary(DateTime date) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), zone));
        var start = startUtc ?? Boundary(localStart);
        if (endUtc <= start || endUtc-start > TimeSpan.FromDays(36600)) throw new ArgumentException("Invalid overview range");
        var buckets = new Dictionary<DateTime, List<TimelineEntry>>();
        for (var day = TimeZoneInfo.ConvertTime(start, zone).Date; Boundary(day) < endUtc; day = day.AddDays(1))
            buckets[day] = [];
        var entries = new List<TimelineEntry>();
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sync_id,kind,app_name,process_name,start_utc,end_utc
                FROM activities WHERE device_id=$device AND start_utc<$end AND end_utc>$start
                ORDER BY start_utc,end_utc;
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$start", ToDatabaseTime(start));
            command.Parameters.AddWithValue("$end", ToDatabaseTime(endUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var from = FromDatabaseTime(reader.GetString(4)); var until = FromDatabaseTime(reader.GetString(5));
                from = from < start ? start : from; until = until > endUtc ? endUtc : until;
                if (until <= from) continue;
                var entry = new TimelineEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), null, from, until, false, "overview");
                entries.Add(entry);
                for (var cursor = from; cursor < until;)
                {
                    var date = TimeZoneInfo.ConvertTime(cursor, zone).Date;
                    var next = Boundary(date.AddDays(1)); var stop = next < until ? next : until;
                    buckets[date].Add(entry with { StartUtc = cursor, EndUtc = stop }); cursor = stop;
                }
            }
        }
        var patternDays = new List<UsagePatternDay>();
        var days = buckets.Select(pair =>
        {
            var from = Boundary(pair.Key); var until = Boundary(pair.Key.AddDays(1));
            var timeline = DailyTimeline.Create(from < start ? start : from, until > endUtc ? endUtc : until, pair.Value);
            patternDays.Add(UsagePatterns.Day(DateOnly.FromDateTime(pair.Key), timeline, zone));
            return new OverviewDay(pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeline.ActiveSeconds, timeline.IdleSeconds);
        }).ToArray();
        var software = entries.Where(e => e.Kind == "application").ToArray();
        var apps = software.GroupBy(e => (e.AppName, e.ProcessName)).Select(group => new OverviewApp(
            group.Key.AppName, group.Key.ProcessName, group.Sum(e => (e.EndUtc-e.StartUtc).TotalSeconds), group.Count()))
            .OrderByDescending(app => app.Seconds).ThenBy(app => app.AppName).ToArray();
        return new(start, endUtc, days, apps, days.Sum(d => d.ActiveSeconds), days.Sum(d => d.IdleSeconds),
            days.Count(d => d.ActiveSeconds > 0), software.Length, UsagePatterns.Summarize(patternDays, zone));
    }
}
