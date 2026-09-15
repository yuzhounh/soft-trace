using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SoftTrace.Core;

public sealed record ManicTimeImportResult(
    int ReadSegmentCount,
    int ConsolidatedSegmentCount,
    int ImportedSegmentCount,
    TimeSpan SourceDuration,
    TimeSpan ImportedDuration,
    TimeSpan OverlapDuration,
    DateTimeOffset? EarliestUtc,
    DateTimeOffset? LatestUtc);

public sealed partial class ActivityStore
{
    public async Task<ManicTimeImportResult> ImportManicTimeAsync(
        string sourcePath,
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        var resolvedSource = await ResolveManicTimeSourceAsync(sourcePath, cancellationToken);
        try
        {
            var source = await ReadManicTimeSegmentsAsync(resolvedSource.DatabasePath, cancellationToken);
            if (source.Segments.Count == 0)
            {
                return new ManicTimeImportResult(
                    source.ReadSegmentCount,
                    0,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    null,
                    null);
            }

            return await ImportManicTimeSegmentsAsync(
                source,
                deviceId,
                deviceName,
                cancellationToken);
        }
        finally
        {
            if (resolvedSource.TemporaryDatabasePath is not null)
            {
                try
                {
                    File.Delete(resolvedSource.TemporaryDatabasePath);
                }
                catch
                {
                    // A leftover temporary copy is harmless and can be cleaned by Windows later.
                }
            }
        }
    }

    private async Task<ManicTimeImportResult> ImportManicTimeSegmentsAsync(
        ManicTimeReadResult source,
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var earliestUtc = source.Segments[0].StartUtc;
        var latestUtc = source.Segments.Max(segment => segment.EndUtc);
        var sourceTicks = source.Segments.Sum(segment => (segment.EndUtc - segment.StartUtc).Ticks);
        var importedCount = 0;
        long importedTicks = 0;
        var writtenUtc = DateTimeOffset.UtcNow;

        await ExecuteWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            var coverage = await ReadCoverageAsync(
                connection,
                transaction,
                deviceId,
                earliestUtc,
                latestUtc,
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO activities (
                    sync_id, device_id, device_name, start_utc, end_utc,
                    process_name, app_name, executable_path, is_open,
                    created_utc, updated_utc, synced_utc, source
                ) VALUES (
                    $sync_id, $device_id, $device_name, $start_utc, $end_utc,
                    $process_name, $app_name, $executable_path, 0,
                    $created_utc, $updated_utc, NULL, 'manictime'
                )
                ON CONFLICT(sync_id) DO NOTHING;
                """;
            insertCommand.Parameters.Add("$sync_id", SqliteType.Text);
            insertCommand.Parameters.AddWithValue("$device_id", deviceId);
            insertCommand.Parameters.AddWithValue("$device_name", deviceName);
            insertCommand.Parameters.Add("$start_utc", SqliteType.Text);
            insertCommand.Parameters.Add("$end_utc", SqliteType.Text);
            insertCommand.Parameters.Add("$process_name", SqliteType.Text);
            insertCommand.Parameters.Add("$app_name", SqliteType.Text);
            insertCommand.Parameters.Add("$executable_path", SqliteType.Text);
            insertCommand.Parameters.AddWithValue("$created_utc", ToDatabaseTime(writtenUtc));
            insertCommand.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(writtenUtc));

            foreach (var segment in source.Segments)
            {
                foreach (var piece in SubtractCoverage(segment.StartUtc, segment.EndUtc, coverage))
                {
                    insertCommand.Parameters["$sync_id"].Value = CreateManicTimeSyncId(
                        deviceId,
                        segment,
                        piece.StartUtc,
                        piece.EndUtc);
                    insertCommand.Parameters["$start_utc"].Value = ToDatabaseTime(piece.StartUtc);
                    insertCommand.Parameters["$end_utc"].Value = ToDatabaseTime(piece.EndUtc);
                    insertCommand.Parameters["$process_name"].Value = segment.App.ProcessName;
                    insertCommand.Parameters["$app_name"].Value = segment.App.AppName;
                    insertCommand.Parameters["$executable_path"].Value =
                        (object?)segment.App.ExecutablePath ?? DBNull.Value;

                    if (await insertCommand.ExecuteNonQueryAsync(cancellationToken) > 0)
                    {
                        importedCount++;
                        importedTicks += (piece.EndUtc - piece.StartUtc).Ticks;
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

        var sourceDuration = TimeSpan.FromTicks(sourceTicks);
        var importedDuration = TimeSpan.FromTicks(importedTicks);
        return new ManicTimeImportResult(
            source.ReadSegmentCount,
            source.Segments.Count,
            importedCount,
            sourceDuration,
            importedDuration,
            sourceDuration - importedDuration,
            earliestUtc,
            latestUtc);
    }

    private static async Task<List<TimeRange>> ReadCoverageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken cancellationToken)
    {
        var ranges = new List<TimeRange>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT start_utc, end_utc
            FROM activities
            WHERE device_id = $device_id
              AND start_utc < $range_end
              AND end_utc > $range_start
            ORDER BY start_utc, end_utc;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$range_start", ToDatabaseTime(rangeStartUtc));
        command.Parameters.AddWithValue("$range_end", ToDatabaseTime(rangeEndUtc));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var startUtc = FromDatabaseTime(reader.GetString(0));
            var endUtc = FromDatabaseTime(reader.GetString(1));
            if (endUtc <= startUtc)
            {
                continue;
            }

            if (ranges.Count > 0 && startUtc <= ranges[^1].EndUtc)
            {
                if (endUtc > ranges[^1].EndUtc)
                {
                    ranges[^1] = ranges[^1] with { EndUtc = endUtc };
                }
            }
            else
            {
                ranges.Add(new TimeRange(startUtc, endUtc));
            }
        }

        return ranges;
    }

    private static IEnumerable<TimeRange> SubtractCoverage(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IReadOnlyList<TimeRange> coverage)
    {
        var cursor = startUtc;
        var low = 0;
        var high = coverage.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (coverage[middle].EndUtc <= startUtc)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var index = low; index < coverage.Count; index++)
        {
            var occupied = coverage[index];
            if (occupied.StartUtc >= endUtc)
            {
                break;
            }

            if (occupied.StartUtc > cursor)
            {
                yield return new TimeRange(cursor, Min(occupied.StartUtc, endUtc));
            }

            if (occupied.EndUtc > cursor)
            {
                cursor = occupied.EndUtc;
            }
            if (cursor >= endUtc)
            {
                yield break;
            }
        }

        if (cursor < endUtc)
        {
            yield return new TimeRange(cursor, endUtc);
        }
    }

    private static async Task<ManicTimeReadResult> ReadManicTimeSegmentsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var rawSegments = new List<ManicTimeSegment>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timeline.TimelineKey,
                   activity.ActivityId,
                   app_group.Name,
                   app_group.[Key],
                   app_group.Other,
                   activity.StartUtcTime,
                   activity.EndUtcTime
            FROM Ar_Timeline AS timeline
            JOIN Ar_Activity AS activity
              ON activity.ReportId = timeline.ReportId
            JOIN Ar_Group AS app_group
              ON app_group.ReportId = activity.ReportId
             AND app_group.GroupId = activity.GroupId
            WHERE timeline.SchemaName = 'ManicTime/Applications'
            ORDER BY timeline.TimelineKey, activity.StartUtcTime, activity.ActivityId;
            """;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var startUtc = ParseManicTimeUtc(reader.GetString(5));
                var endUtc = ParseManicTimeUtc(reader.GetString(6));
                if (endUtc <= startUtc)
                {
                    continue;
                }

                var activityId = reader.GetInt64(1);
                rawSegments.Add(new ManicTimeSegment(
                    reader.GetString(0),
                    activityId,
                    activityId,
                    ReadAppIdentity(reader.GetString(2), reader.GetString(3), reader.GetString(4)),
                    startUtc,
                    endUtc));
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException(
                "所选文件不是受支持的 ManicTimeReports.db，或其中没有应用时间线。",
                exception);
        }

        var consolidated = new List<ManicTimeSegment>(rawSegments.Count);
        foreach (var segment in rawSegments)
        {
            if (consolidated.Count > 0 && CanConsolidate(consolidated[^1], segment))
            {
                consolidated[^1] = consolidated[^1] with
                {
                    LastActivityId = segment.LastActivityId,
                    EndUtc = Max(consolidated[^1].EndUtc, segment.EndUtc)
                };
            }
            else
            {
                consolidated.Add(segment);
            }
        }

        return new ManicTimeReadResult(rawSegments.Count, consolidated);
    }

    private static bool CanConsolidate(ManicTimeSegment left, ManicTimeSegment right) =>
        left.TimelineKey == right.TimelineKey &&
        left.App.StableKey == right.App.StableKey &&
        string.Equals(left.App.AppName, right.App.AppName, StringComparison.OrdinalIgnoreCase) &&
        right.StartUtc <= left.EndUtc;

    private static AppIdentity ReadAppIdentity(string groupName, string groupKey, string other)
    {
        string? fileName = null;
        string? fullPath = null;
        string? appName = null;
        try
        {
            using var json = JsonDocument.Parse(other);
            var root = json.RootElement;
            fileName = ReadJsonString(root, "fileName");
            fullPath = ReadJsonString(root, "fullPath");
            appName = ReadJsonString(root, "name");
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = groupKey.Split(';', 2)[0];
        }

        var processName = Path.GetFileNameWithoutExtension(fileName?.Trim());
        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = "Unknown";
        }
        appName = FirstNonEmpty(appName, groupName, processName);
        fullPath = string.IsNullOrWhiteSpace(fullPath) ? null : fullPath.Trim();
        return new AppIdentity(processName, appName, fullPath);
    }

    private static string? ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private static DateTimeOffset ParseManicTimeUtc(string value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"ManicTime 包含无法识别的 UTC 时间：{value}");
    }

    private static string CreateManicTimeSyncId(
        string deviceId,
        ManicTimeSegment segment,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        var identity = string.Join(
            "|",
            "manictime",
            deviceId,
            segment.TimelineKey,
            segment.FirstActivityId.ToString(CultureInfo.InvariantCulture),
            segment.LastActivityId.ToString(CultureInfo.InvariantCulture),
            segment.App.StableKey,
            ToDatabaseTime(startUtc),
            ToDatabaseTime(endUtc));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static async Task<ResolvedManicTimeSource> ResolveManicTimeSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (Directory.Exists(sourcePath))
        {
            var reportsDatabase = Directory
                .EnumerateFiles(sourcePath, "ManicTimeReports.db", SearchOption.AllDirectories)
                .FirstOrDefault();
            return reportsDatabase is not null
                ? new ResolvedManicTimeSource(reportsDatabase, null)
                : throw new FileNotFoundException("所选目录中没有 ManicTimeReports.db。", sourcePath);
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到 ManicTime 数据源。", sourcePath);
        }
        if (!string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedManicTimeSource(sourcePath, null);
        }

        using var archive = ZipFile.OpenRead(sourcePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(
                Path.GetFileName(candidate.FullName),
                "ManicTimeReports.db",
                StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidDataException("所选 ZIP 中没有 ManicTimeReports.db。");
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"softtrace-manictime-{Guid.NewGuid():N}.db");
        try
        {
            await using var input = entry.Open();
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
            return new ResolvedManicTimeSource(temporaryPath, temporaryPath);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private sealed record ResolvedManicTimeSource(string DatabasePath, string? TemporaryDatabasePath);

    private sealed record ManicTimeReadResult(
        int ReadSegmentCount,
        IReadOnlyList<ManicTimeSegment> Segments);

    private sealed record ManicTimeSegment(
        string TimelineKey,
        long FirstActivityId,
        long LastActivityId,
        AppIdentity App,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    private sealed record TimeRange(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}
