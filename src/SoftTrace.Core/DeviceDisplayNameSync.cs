namespace SoftTrace.Core;

public sealed partial class ActivityStore
{
    public Task SetDeviceDisplayNameAsync(
        string deviceId,
        string displayName,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        displayName = displayName.Trim();
        if (displayName.Length > 64)
        {
            throw new ArgumentException("设备名称不能超过 64 个字符。", nameof(displayName));
        }
        return ExecuteWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO settings(key, value) VALUES ('device_display_name:' || $device, $name)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                INSERT INTO device_aliases(user_id, device_id, display_name, revision, is_pending, is_legacy)
                VALUES ($user, $device, $name, $revision, 1, 0)
                ON CONFLICT(user_id, device_id) DO UPDATE SET
                    display_name = excluded.display_name, revision = excluded.revision,
                    is_pending = 1, is_legacy = 0;
                """;
            command.Parameters.AddWithValue("$user", userId ?? string.Empty);
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$name", displayName);
            command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceDisplayNameChange>> GetPendingDeviceDisplayNamesAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        // Names edited while signed out are assigned to the next signed-in account once.
        await ExecuteWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO device_aliases(user_id, device_id, display_name, revision, is_pending, is_legacy)
                SELECT $user, device_id, display_name, revision, is_pending, is_legacy
                FROM device_aliases WHERE user_id = ''
                ON CONFLICT(user_id, device_id) DO UPDATE SET
                    display_name = excluded.display_name, revision = excluded.revision,
                    is_pending = 1, is_legacy = 0
                WHERE excluded.is_legacy = 0;
                DELETE FROM device_aliases WHERE user_id = '';
                """;
            command.Parameters.AddWithValue("$user", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }, cancellationToken);

        await using var readConnection = await OpenAsync(cancellationToken);
        await using var read = readConnection.CreateCommand();
        read.CommandText = """
            SELECT device_id, display_name, revision, is_legacy FROM device_aliases
            WHERE user_id = $user AND is_pending = 1 ORDER BY device_id;
            """;
        read.Parameters.AddWithValue("$user", userId);
        var names = new List<DeviceDisplayNameChange>();
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }
        return names;
    }

    public Task MarkDeviceDisplayNamesSyncedAsync(
        string userId, IReadOnlyCollection<DeviceDisplayNameChange> names,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(async connection =>
        {
            foreach (var name in names)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE device_aliases SET is_pending = 0, is_legacy = 0
                    WHERE user_id = $user AND device_id = $device AND revision = $revision;
                    """;
                command.Parameters.AddWithValue("$user", userId);
                command.Parameters.AddWithValue("$device", name.DeviceId);
                command.Parameters.AddWithValue("$revision", name.Revision);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }, cancellationToken);

    public async Task<int> MergeRemoteDeviceDisplayNamesAsync(
        string userId, IReadOnlyCollection<DeviceDisplayNameChange> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var changed = 0;
        await ExecuteWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name.DeviceId) || string.IsNullOrWhiteSpace(name.DisplayName) ||
                    name.DisplayName.Length > 64 || string.IsNullOrWhiteSpace(name.Revision))
                {
                    continue;
                }
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO device_aliases(user_id, device_id, display_name, revision, is_pending, is_legacy)
                    VALUES ($user, $device, $name, $revision, 0, 0)
                    ON CONFLICT(user_id, device_id) DO UPDATE SET
                        display_name = excluded.display_name, revision = excluded.revision,
                        is_pending = 0, is_legacy = 0
                    WHERE (device_aliases.is_pending = 0 OR device_aliases.is_legacy = 1)
                      AND device_aliases.revision <> excluded.revision;
                    """;
                command.Parameters.AddWithValue("$user", userId);
                command.Parameters.AddWithValue("$device", name.DeviceId);
                command.Parameters.AddWithValue("$name", name.DisplayName.Trim());
                command.Parameters.AddWithValue("$revision", name.Revision);
                if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    command.CommandText = """
                        INSERT INTO settings(key, value) VALUES ('device_display_name:' || $device, $name)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                        """;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    changed++;
                }
            }
            transaction.Commit();
        }, cancellationToken);
        return changed;
    }
}
