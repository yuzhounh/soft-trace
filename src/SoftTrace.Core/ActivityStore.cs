using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SoftTrace.Core;

public sealed class ActivityStore(string databasePath)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString();

    public string DatabasePath { get; } = databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS activities (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sync_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                device_name TEXT NOT NULL,
                start_utc TEXT NOT NULL,
                end_utc TEXT NOT NULL,
                process_name TEXT NOT NULL,
                app_name TEXT NOT NULL,
                executable_path TEXT NULL,
                is_open INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                synced_utc TEXT NULL,
                source TEXT NOT NULL DEFAULT 'local'
            );
            CREATE INDEX IF NOT EXISTS ix_activities_range
                ON activities(start_utc, end_utc);
            CREATE INDEX IF NOT EXISTS ix_activities_app
                ON activities(app_name, process_name);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnAsync(connection, "activities", "sync_id", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "activities", "device_name", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "activities", "updated_utc", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "activities", "synced_utc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "activities", "source", "TEXT NOT NULL DEFAULT 'local'", cancellationToken);

        command.CommandText = """
            UPDATE activities
            SET sync_id = lower(hex(randomblob(16)))
            WHERE sync_id IS NULL OR sync_id = '';
            UPDATE activities
            SET device_name = device_id
            WHERE device_name = '';
            UPDATE activities
            SET updated_utc = CASE
                WHEN created_utc <> '' THEN created_utc
                ELSE end_utc
            END
            WHERE updated_utc = '';
            CREATE UNIQUE INDEX IF NOT EXISTS ux_activities_sync_id
                ON activities(sync_id);
            CREATE INDEX IF NOT EXISTS ix_activities_device_range
                ON activities(device_id, start_utc, end_utc);
            CREATE INDEX IF NOT EXISTS ix_activities_sync_state
                ON activities(updated_utc, synced_utc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecoverInterruptedSegmentsAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE activities
                SET is_open = 0,
                    updated_utc = $updated_utc
                WHERE is_open = 1
                  AND ($device_id IS NULL OR device_id = $device_id);
                """;
            command.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$device_id", (object?)deviceId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task<long> StartSegmentAsync(
        string deviceId,
        AppIdentity app,
        DateTimeOffset startUtc,
        CancellationToken cancellationToken = default) =>
        StartSegmentAsync(deviceId, deviceId, app, startUtc, cancellationToken);

    public async Task<long> StartSegmentAsync(
        string deviceId,
        string deviceName,
        AppIdentity app,
        DateTimeOffset startUtc,
        CancellationToken cancellationToken = default)
    {
        long id = 0;
        var writtenUtc = DateTimeOffset.UtcNow;
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO activities (
                    sync_id, device_id, device_name, start_utc, end_utc,
                    process_name, app_name, executable_path, is_open,
                    created_utc, updated_utc, synced_utc, source
                ) VALUES (
                    $sync_id, $device_id, $device_name, $start_utc, $end_utc,
                    $process_name, $app_name, $executable_path, 1,
                    $created_utc, $updated_utc, NULL, 'local'
                );
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$sync_id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$device_id", deviceId);
            command.Parameters.AddWithValue("$device_name", deviceName);
            command.Parameters.AddWithValue("$start_utc", ToDatabaseTime(startUtc));
            command.Parameters.AddWithValue("$end_utc", ToDatabaseTime(startUtc));
            command.Parameters.AddWithValue("$process_name", app.ProcessName);
            command.Parameters.AddWithValue("$app_name", app.AppName);
            command.Parameters.AddWithValue("$executable_path", (object?)app.ExecutablePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_utc", ToDatabaseTime(writtenUtc));
            command.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(writtenUtc));
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);
        return id;
    }

    public Task CheckpointSegmentAsync(
        long id,
        DateTimeOffset endUtc,
        bool close,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE activities
                SET end_utc = CASE WHEN $end_utc > end_utc THEN $end_utc ELSE end_utc END,
                    is_open = $is_open,
                    updated_utc = $updated_utc
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$end_utc", ToDatabaseTime(endUtc));
            command.Parameters.AddWithValue("$is_open", close ? 0 : 1);
            command.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task AddCompletedSegmentAsync(
        string deviceId,
        ActivitySegment segment,
        CancellationToken cancellationToken = default)
    {
        if (segment.EndUtc <= segment.StartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(segment), "Segment end must be after its start.");
        }

        var id = await StartSegmentAsync(deviceId, segment.App, segment.StartUtc, cancellationToken);
        await CheckpointSegmentAsync(id, segment.EndUtc, close: true, cancellationToken);
    }

    public async Task<IReadOnlyList<UsageSummary>> GetUsageAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        if (rangeEndUtc <= rangeStartUtc)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                app_name,
                process_name,
                MAX(executable_path) AS executable_path,
                SUM((julianday(MIN(end_utc, $range_end)) -
                     julianday(MAX(start_utc, $range_start))) * 86400.0) AS seconds,
                COUNT(*) AS segment_count
            FROM activities
            WHERE start_utc < $range_end
              AND end_utc > $range_start
              AND ($device_id IS NULL OR device_id = $device_id)
            GROUP BY app_name, process_name
            HAVING seconds > 0
            ORDER BY seconds DESC, app_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$range_start", ToDatabaseTime(rangeStartUtc));
        command.Parameters.AddWithValue("$range_end", ToDatabaseTime(rangeEndUtc));
        command.Parameters.AddWithValue("$device_id", (object?)deviceId ?? DBNull.Value);

        var results = new List<UsageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsageSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(3))),
                reader.GetInt32(4)));
        }

        return results;
    }

    public async Task<DateTimeOffset?> GetEarliestActivityUtcAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(start_utc)
            FROM activities
            WHERE $device_id IS NULL OR device_id = $device_id;
            """;
        command.Parameters.AddWithValue("$device_id", (object?)deviceId ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var earliest)
            ? earliest.ToUniversalTime()
            : null;
    }

    public async Task<DeviceIdentity> GetOrCreateDeviceIdentityAsync(
        string suggestedName,
        CancellationToken cancellationToken = default)
    {
        DeviceIdentity? identity = null;
        await ExecuteWriteAsync(async connection =>
        {
            string? deviceId = null;
            await using (var readCommand = connection.CreateCommand())
            {
                readCommand.CommandText = "SELECT value FROM settings WHERE key = 'device_id';";
                deviceId = await readCommand.ExecuteScalarAsync(cancellationToken) as string;
            }

            deviceId = string.IsNullOrWhiteSpace(deviceId)
                ? Guid.NewGuid().ToString("N")
                : deviceId;
            var deviceName = string.IsNullOrWhiteSpace(suggestedName) ? "Windows 设备" : suggestedName.Trim();

            await using (var saveCommand = connection.CreateCommand())
            {
                saveCommand.CommandText = """
                    INSERT INTO settings(key, value) VALUES ('device_id', $device_id)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    INSERT INTO settings(key, value) VALUES ('device_name', $device_name)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                saveCommand.Parameters.AddWithValue("$device_id", deviceId);
                saveCommand.Parameters.AddWithValue("$device_name", deviceName);
                await saveCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var migrateCommand = connection.CreateCommand())
            {
                migrateCommand.CommandText = """
                    UPDATE activities
                    SET device_id = $device_id,
                        device_name = $device_name,
                        updated_utc = $updated_utc
                    WHERE (device_id = $legacy_device_id AND device_id <> $device_id)
                       OR (device_id = $device_id AND device_name <> $device_name);
                    """;
                migrateCommand.Parameters.AddWithValue("$device_id", deviceId);
                migrateCommand.Parameters.AddWithValue("$device_name", deviceName);
                migrateCommand.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(DateTimeOffset.UtcNow));
                migrateCommand.Parameters.AddWithValue("$legacy_device_id", suggestedName);
                await migrateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            identity = new DeviceIdentity(deviceId, deviceName);
        }, cancellationToken);

        return identity!;
    }

    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id,
                   COALESCE(MAX(NULLIF(device_name, '')), device_id) AS display_name
            FROM activities
            GROUP BY device_id
            ORDER BY display_name COLLATE NOCASE;
            """;

        var devices = new List<DeviceSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new DeviceSummary(reader.GetString(0), reader.GetString(1)));
        }
        return devices;
    }

    public async Task<IReadOnlyList<SyncActivity>> GetPendingSyncActivitiesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sync_id, device_id, device_name, process_name, app_name,
                   executable_path, start_utc, end_utc, updated_utc, is_open, source
            FROM activities
            WHERE synced_utc IS NULL OR updated_utc > synced_utc
            ORDER BY updated_utc, sync_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var activities = new List<SyncActivity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            activities.Add(new SyncActivity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                FromDatabaseTime(reader.GetString(6)),
                FromDatabaseTime(reader.GetString(7)),
                FromDatabaseTime(reader.GetString(8)),
                reader.GetInt32(9) != 0,
                reader.GetString(10)));
        }
        return activities;
    }

    public Task MarkActivitiesSyncedAsync(
        IReadOnlyCollection<SyncMarker> markers,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var marker in markers)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE activities
                    SET synced_utc = $updated_utc
                    WHERE sync_id = $sync_id AND updated_utc = $updated_utc;
                    """;
                command.Parameters.AddWithValue("$sync_id", marker.SyncId);
                command.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(marker.UpdatedUtc));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    public async Task<int> MergeRemoteActivitiesAsync(
        IReadOnlyCollection<SyncActivity> activities,
        CancellationToken cancellationToken = default)
    {
        var changed = 0;
        await ExecuteWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var activity in activities)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO activities (
                        sync_id, device_id, device_name, start_utc, end_utc,
                        process_name, app_name, executable_path, is_open,
                        created_utc, updated_utc, synced_utc, source
                    ) VALUES (
                        $sync_id, $device_id, $device_name, $start_utc, $end_utc,
                        $process_name, $app_name, $executable_path, $is_open,
                        $created_utc, $updated_utc, $updated_utc, $source
                    )
                    ON CONFLICT(sync_id) DO UPDATE SET
                        device_id = excluded.device_id,
                        device_name = excluded.device_name,
                        start_utc = excluded.start_utc,
                        end_utc = excluded.end_utc,
                        process_name = excluded.process_name,
                        app_name = excluded.app_name,
                        executable_path = excluded.executable_path,
                        is_open = excluded.is_open,
                        updated_utc = excluded.updated_utc,
                        synced_utc = excluded.updated_utc,
                        source = excluded.source
                    WHERE excluded.updated_utc > activities.updated_utc;
                    """;
                command.Parameters.AddWithValue("$sync_id", activity.SyncId);
                command.Parameters.AddWithValue("$device_id", activity.DeviceId);
                command.Parameters.AddWithValue("$device_name", activity.DeviceName);
                command.Parameters.AddWithValue("$start_utc", ToDatabaseTime(activity.StartUtc));
                command.Parameters.AddWithValue("$end_utc", ToDatabaseTime(activity.EndUtc));
                command.Parameters.AddWithValue("$process_name", activity.ProcessName);
                command.Parameters.AddWithValue("$app_name", activity.AppName);
                command.Parameters.AddWithValue("$executable_path", (object?)activity.ExecutablePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$is_open", activity.IsOpen ? 1 : 0);
                command.Parameters.AddWithValue("$created_utc", ToDatabaseTime(activity.UpdatedUtc));
                command.Parameters.AddWithValue("$updated_utc", ToDatabaseTime(activity.UpdatedUtc));
                command.Parameters.AddWithValue("$source", activity.Source);
                changed += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
        return changed;
    }

    public async Task<DateTimeOffset?> GetLastSyncPullUtcAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'last_sync_pull_utc';";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    public Task SetLastSyncPullUtcAsync(
        DateTimeOffset value,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value)
                VALUES ('last_sync_pull_utc', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", ToDatabaseTime(value));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<SyncPullCursor?> GetLastSyncPullCursorAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, value
            FROM settings
            WHERE key IN ('last_sync_pull_utc', 'last_sync_pull_document');
            """;

        string? timestampText = null;
        string? documentName = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) == "last_sync_pull_utc")
            {
                timestampText = reader.GetString(1);
            }
            else
            {
                documentName = reader.GetString(1);
            }
        }

        return DateTimeOffset.TryParse(
                   timestampText,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out var timestamp) &&
               !string.IsNullOrWhiteSpace(documentName)
            ? new SyncPullCursor(timestamp.ToUniversalTime(), documentName)
            : null;
    }

    public Task SetLastSyncPullCursorAsync(
        SyncPullCursor cursor,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value)
                VALUES ('last_sync_pull_utc', $timestamp)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                INSERT INTO settings(key, value)
                VALUES ('last_sync_pull_document', $document)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$timestamp", ToDatabaseTime(cursor.ServerUpdatedUtc));
            command.Parameters.AddWithValue("$document", cursor.DocumentName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<int> GetIdleThresholdMinutesAsync(
        int defaultValue = 3,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'idle_threshold_minutes';";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            ? Math.Clamp(minutes, 1, 60)
            : defaultValue;
    }

    public Task SetIdleThresholdMinutesAsync(int minutes, CancellationToken cancellationToken = default)
    {
        minutes = Math.Clamp(minutes, 1, 60);
        return ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value)
                VALUES ('idle_threshold_minutes', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", minutes.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task ExecuteWriteAsync(
        Func<SqliteConnection, Task> action,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await action(connection);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var inspectCommand = connection.CreateCommand();
        inspectCommand.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await inspectCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset FromDatabaseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private static string ToDatabaseTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
