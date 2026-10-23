namespace SoftTrace.Core;

public sealed record TimelineEntry(string Id, string Kind, string AppName, string ProcessName,
    string? WindowTitle, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool IsOpen, string Source);

public sealed record DailyTimeline(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<TimelineEntry> Entries, IReadOnlyList<TimelineEntry> States,
    double ActiveSeconds, double IdleSeconds, DateTimeOffset? FirstActivityUtc,
    DateTimeOffset? LastActivityUtc)
{
    public static DailyTimeline Create(DateTimeOffset start, DateTimeOffset end,
        IReadOnlyList<TimelineEntry> observations)
    {
        var entries = observations.Where(e => e.EndUtc > start && e.StartUtc < end)
            .Select(e => e with { StartUtc = e.StartUtc < start ? start : e.StartUtc,
                EndUtc = e.EndUtc > end ? end : e.EndUtc })
            .Where(e => e.EndUtc > e.StartUtc).OrderBy(e => e.StartUtc).ToList();
        var states = new List<TimelineEntry>();
        var cursor = start;
        foreach (var entry in entries)
        {
            if (entry.StartUtc > cursor)
                AddState(new TimelineEntry($"gap-{cursor.UtcTicks}", "unknown", "未记录", "", null,
                    cursor, entry.StartUtc, false, "gap"));
            // Imported observations can overlap; count their union rather than double counting.
            if (entry.EndUtc <= cursor) continue;
            var kind = entry.Kind == "application" ? "active" : entry.Kind;
            AddState(entry with { Kind = kind, AppName = kind == "active" ? "使用中" : entry.AppName,
                WindowTitle = null, StartUtc = entry.StartUtc < cursor ? cursor : entry.StartUtc });
            cursor = entry.EndUtc;
        }
        if (cursor < end)
            AddState(new TimelineEntry($"gap-{cursor.UtcTicks}", "unknown", "未记录", "", null,
                cursor, end, false, "gap"));
        var apps = entries.Where(e => e.Kind == "application").ToList();
        return new DailyTimeline(start, end, entries, states,
            states.Where(e => e.Kind == "active").Sum(e => (e.EndUtc - e.StartUtc).TotalSeconds),
            states.Where(e => e.Kind == "idle").Sum(e => (e.EndUtc - e.StartUtc).TotalSeconds),
            apps.Count == 0 ? null : apps.Min(e => e.StartUtc),
            apps.Count == 0 ? null : apps.Max(e => e.EndUtc));

        void AddState(TimelineEntry entry)
        {
            if (states.Count > 0 && states[^1].Kind == entry.Kind && states[^1].EndUtc == entry.StartUtc)
                states[^1] = states[^1] with { EndUtc = entry.EndUtc, IsOpen = entry.IsOpen };
            else states.Add(entry);
        }
    }
}

public sealed partial class ActivityStore
{
    // A changing window title may have created provisional software segments after the last
    // input. Once the idle threshold is reached, retract that whole tail, including synced rows.
    public Task TrimIdleTailAsync(string deviceId, DateTimeOffset activeRunStart,
        DateTimeOffset idleStart, CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE activities SET end_utc = MAX(start_utc, $idle), is_open = 0, updated_utc = $now
                WHERE device_id = $device AND kind = 'application' AND source = 'local'
                  AND start_utc >= $run AND end_utc > $idle;
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$run", ToDatabaseTime(activeRunStart));
            command.Parameters.AddWithValue("$idle", ToDatabaseTime(idleStart));
            command.Parameters.AddWithValue("$now", ToDatabaseTime(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<DailyTimeline> GetDailyTimelineAsync(string deviceId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        if (end <= start || end - start > TimeSpan.FromHours(26))
            throw new ArgumentException("请选择一个有效的日期范围。");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sync_id, kind, app_name, process_name, window_title, start_utc, end_utc, is_open, source
            FROM activities
            WHERE device_id = $device AND start_utc < $end AND end_utc > $start
            ORDER BY start_utc, end_utc;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$start", ToDatabaseTime(start));
        command.Parameters.AddWithValue("$end", ToDatabaseTime(end));
        var entries = new List<TimelineEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new TimelineEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                FromDatabaseTime(reader.GetString(5)), FromDatabaseTime(reader.GetString(6)),
                reader.GetInt32(7) != 0, reader.GetString(8)));
        return DailyTimeline.Create(start, end, entries);
    }
}
