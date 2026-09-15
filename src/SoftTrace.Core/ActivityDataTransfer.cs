using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace SoftTrace.Core;

public sealed record ActivityExportItem(
    [property: JsonPropertyName("syncId")] string SyncId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("startUtc")] DateTimeOffset StartUtc,
    [property: JsonPropertyName("endUtc")] DateTimeOffset EndUtc,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("appName")] string AppName,
    [property: JsonPropertyName("executablePath")] string? ExecutablePath,
    [property: JsonPropertyName("source")] string Source);

public sealed record ActivityExportPackage(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("exportedUtc")] DateTimeOffset ExportedUtc,
    [property: JsonPropertyName("deviceId")] string? FilterDeviceId,
    [property: JsonPropertyName("activities")] IReadOnlyList<ActivityExportItem> Activities);

public sealed record ActivityImportResult(
    int TotalItemsInPackage,
    int ImportedCount,
    TimeSpan ImportedDuration,
    TimeSpan OverlapDuration,
    int AffectedDeviceCount);

public sealed partial class ActivityStore
{
    private static readonly JsonSerializerOptions TransferJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<string> ExportActivitiesToJsonAsync(
        string? deviceId = null,
        DateTimeOffset? rangeStartUtc = null,
        DateTimeOffset? rangeEndUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var query = """
            SELECT sync_id, device_id, device_name, start_utc, end_utc,
                   process_name, app_name, executable_path, source
            FROM activities
            WHERE ($device_id IS NULL OR device_id = $device_id)
              AND ($range_start IS NULL OR end_utc > $range_start)
              AND ($range_end IS NULL OR start_utc < $range_end)
            ORDER BY device_id, start_utc;
            """;
        command.CommandText = query;
        command.Parameters.AddWithValue("$device_id", (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$range_start", rangeStartUtc.HasValue ? ToDatabaseTime(rangeStartUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$range_end", rangeEndUtc.HasValue ? ToDatabaseTime(rangeEndUtc.Value) : DBNull.Value);

        var list = new List<ActivityExportItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var syncId = reader.GetString(0);
            var rowDeviceId = reader.GetString(1);
            var deviceName = reader.GetString(2);
            var startUtc = FromDatabaseTime(reader.GetString(3));
            var endUtc = FromDatabaseTime(reader.GetString(4));
            var processName = reader.GetString(5);
            var appName = reader.GetString(6);
            var executablePath = reader.IsDBNull(7) ? null : reader.GetString(7);
            var source = reader.GetString(8);

            list.Add(new ActivityExportItem(
                syncId,
                rowDeviceId,
                deviceName,
                startUtc,
                endUtc,
                processName,
                appName,
                executablePath,
                source));
        }

        var package = new ActivityExportPackage(
            Version: 1,
            ExportedUtc: DateTimeOffset.UtcNow,
            FilterDeviceId: deviceId,
            Activities: list);

        return JsonSerializer.Serialize(package, TransferJsonOptions);
    }

    public async Task ExportActivitiesToFileAsync(
        string filePath,
        string? deviceId = null,
        DateTimeOffset? rangeStartUtc = null,
        DateTimeOffset? rangeEndUtc = null,
        CancellationToken cancellationToken = default)
    {
        var json = await ExportActivitiesToJsonAsync(deviceId, rangeStartUtc, rangeEndUtc, cancellationToken);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    public async Task<ActivityImportResult> ImportActivitiesFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("导入文件不存在", filePath);
        }

        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return await ImportActivitiesFromJsonAsync(json, cancellationToken);
    }

    public async Task<ActivityImportResult> ImportActivitiesFromJsonAsync(
        string json,
        CancellationToken cancellationToken = default)
    {
        ActivityExportPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<ActivityExportPackage>(json, TransferJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("所选文件不是有效的 SoftTrace 数据导出文件。", ex);
        }

        if (package is null || package.Activities.Count == 0)
        {
            return new ActivityImportResult(0, 0, TimeSpan.Zero, TimeSpan.Zero, 0);
        }

        var importedCount = 0;
        long importedTicks = 0;
        long overlapTicks = 0;
        var affectedDeviceCount = 0;
        var writtenUtc = DateTimeOffset.UtcNow;

        var groupsByDevice = package.Activities.GroupBy(a => a.DeviceId);
        await ExecuteWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();

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
                    $created_utc, $updated_utc, NULL, $source
                )
                ON CONFLICT(sync_id) DO NOTHING;
                """;
            insertCommand.Parameters.Add("$sync_id", SqliteType.Text);
            insertCommand.Parameters.Add("$device_id", SqliteType.Text);
            insertCommand.Parameters.Add("$device_name", SqliteType.Text);
            insertCommand.Parameters.Add("$start_utc", SqliteType.Text);
            insertCommand.Parameters.Add("$end_utc", SqliteType.Text);
            insertCommand.Parameters.Add("$process_name", SqliteType.Text);
            insertCommand.Parameters.Add("$app_name", SqliteType.Text);
            insertCommand.Parameters.Add("$executable_path", SqliteType.Text);
            insertCommand.Parameters.AddWithValue("$created_utc", ToDatabaseTime(writtenUtc));
            insertCommand.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(writtenUtc));
            insertCommand.Parameters.Add("$source", SqliteType.Text);

            foreach (var group in groupsByDevice)
            {
                var deviceId = group.Key;
                affectedDeviceCount++;
                var segments = group.Where(s => s.EndUtc > s.StartUtc).OrderBy(s => s.StartUtc).ToList();
                if (segments.Count == 0)
                {
                    continue;
                }

                var earliestUtc = segments.Min(s => s.StartUtc);
                var latestUtc = segments.Max(s => s.EndUtc);

                var coverage = await ReadCoverageAsync(
                    connection,
                    transaction,
                    deviceId,
                    earliestUtc,
                    latestUtc,
                    cancellationToken);

                foreach (var item in segments)
                {
                    var itemTotalTicks = (item.EndUtc - item.StartUtc).Ticks;
                    long itemImportedTicks = 0;

                    foreach (var piece in SubtractCoverage(item.StartUtc, item.EndUtc, coverage))
                    {
                        var pieceDurationTicks = (piece.EndUtc - piece.StartUtc).Ticks;
                        if (pieceDurationTicks <= 0)
                        {
                            continue;
                        }

                        var pieceSyncId = (piece.StartUtc == item.StartUtc && piece.EndUtc == item.EndUtc)
                            ? item.SyncId
                            : Guid.NewGuid().ToString("N");

                        insertCommand.Parameters["$sync_id"].Value = pieceSyncId;
                        insertCommand.Parameters["$device_id"].Value = deviceId;
                        insertCommand.Parameters["$device_name"].Value = item.DeviceName;
                        insertCommand.Parameters["$start_utc"].Value = ToDatabaseTime(piece.StartUtc);
                        insertCommand.Parameters["$end_utc"].Value = ToDatabaseTime(piece.EndUtc);
                        insertCommand.Parameters["$process_name"].Value = item.ProcessName;
                        insertCommand.Parameters["$app_name"].Value = item.AppName;
                        insertCommand.Parameters["$executable_path"].Value = (object?)item.ExecutablePath ?? DBNull.Value;
                        insertCommand.Parameters["$source"].Value = string.IsNullOrWhiteSpace(item.Source) ? "import" : item.Source;

                        if (await insertCommand.ExecuteNonQueryAsync(cancellationToken) > 0)
                        {
                            importedCount++;
                            itemImportedTicks += pieceDurationTicks;
                            importedTicks += pieceDurationTicks;

                            // Update in-memory coverage so later segments in this batch also don't overlap
                            InsertCoverage(coverage, piece.StartUtc, piece.EndUtc);
                        }
                    }

                    overlapTicks += Math.Max(0, itemTotalTicks - itemImportedTicks);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

        return new ActivityImportResult(
            package.Activities.Count,
            importedCount,
            TimeSpan.FromTicks(importedTicks),
            TimeSpan.FromTicks(overlapTicks),
            affectedDeviceCount);
    }

    private static void InsertCoverage(List<TimeRange> coverage, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (coverage.Count == 0)
        {
            coverage.Add(new TimeRange(startUtc, endUtc));
            return;
        }

        var idx = 0;
        while (idx < coverage.Count && coverage[idx].EndUtc < startUtc)
        {
            idx++;
        }

        if (idx == coverage.Count)
        {
            coverage.Add(new TimeRange(startUtc, endUtc));
            return;
        }

        if (coverage[idx].StartUtc <= endUtc)
        {
            var mergedStart = coverage[idx].StartUtc < startUtc ? coverage[idx].StartUtc : startUtc;
            var mergedEnd = coverage[idx].EndUtc > endUtc ? coverage[idx].EndUtc : endUtc;

            int endIdx = idx + 1;
            while (endIdx < coverage.Count && coverage[endIdx].StartUtc <= mergedEnd)
            {
                if (coverage[endIdx].EndUtc > mergedEnd)
                {
                    mergedEnd = coverage[endIdx].EndUtc;
                }
                endIdx++;
            }

            coverage[idx] = new TimeRange(mergedStart, mergedEnd);
            if (endIdx > idx + 1)
            {
                coverage.RemoveRange(idx + 1, endIdx - (idx + 1));
            }
        }
        else
        {
            coverage.Insert(idx, new TimeRange(startUtc, endUtc));
        }
    }
}
