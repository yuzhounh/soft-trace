using Microsoft.Data.Sqlite;
using SoftTrace.Core;

namespace SoftTrace.Core.Tests;

public sealed class DeviceDisplayNameSyncTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"softtrace-device-names-{Guid.NewGuid():N}.db");
    private ActivityStore Store => new(_path);
    public Task InitializeAsync() => Store.InitializeAsync();
    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SyncedAliasChangesDisplayWithoutChangingDeviceOrActivity()
    {
        var store = Store;
        var device = await store.GetOrCreateDeviceIdentityAsync("Actual Windows PC");
        var origin = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
        var segment = await store.StartSegmentAsync(device.Id, device.Name, new("app", "App", null), origin);
        await store.CheckpointSegmentAsync(segment, origin.AddMinutes(1), close: true);
        var before = Assert.Single(await store.GetPendingSyncActivitiesAsync());
        var remote = new DeviceDisplayNameChange(device.Id, "工作电脑", "remote-revision");

        Assert.Equal(1, await store.MergeRemoteDeviceDisplayNamesAsync("account", new[] { remote }));
        Assert.Equal("工作电脑", Assert.Single(await store.GetDevicesAsync()).Name);
        Assert.Equal(before, Assert.Single(await store.GetPendingSyncActivitiesAsync()));
        Assert.Equal(device.Id, (await store.GetOrCreateDeviceIdentityAsync("Actual Windows PC")).Id);
        Assert.Empty(await store.GetPendingDeviceDisplayNamesAsync("account"));
        Assert.Equal(0, await store.MergeRemoteDeviceDisplayNamesAsync("account", new[] { remote }));
    }

    [Fact]
    public async Task NewerLocalEditSurvivesUploadAcknowledgmentAndRemotePull()
    {
        var store = Store;
        await store.SetDeviceDisplayNameAsync("device", "First name", "account-a");
        var uploaded = Assert.Single(await store.GetPendingDeviceDisplayNamesAsync("account-a"));
        await store.SetDeviceDisplayNameAsync("device", "Newer name", "account-a");
        await store.MarkDeviceDisplayNamesSyncedAsync("account-a", new[] { uploaded });
        await store.MergeRemoteDeviceDisplayNamesAsync("account-a", new[] { uploaded });
        var pending = Assert.Single(await store.GetPendingDeviceDisplayNamesAsync("account-a"));
        Assert.Equal("Newer name", pending.DisplayName);
        Assert.NotEqual(uploaded.Revision, pending.Revision);
        Assert.Equal("Newer name", await store.GetSettingAsync("device_display_name:device"));
        Assert.Empty(await store.GetPendingDeviceDisplayNamesAsync("account-b"));
    }

    [Fact]
    public async Task SignedOutRenameIsAssignedToNextAccountOnce()
    {
        var store = Store;
        await store.SetDeviceDisplayNameAsync("device", "离线改名");
        Assert.Equal("离线改名", Assert.Single(await store.GetPendingDeviceDisplayNamesAsync("account-a")).DisplayName);
        Assert.Empty(await store.GetPendingDeviceDisplayNamesAsync("account-b"));
    }

    [Fact]
    public async Task LegacyAliasMigratesOnceAndAnExistingCloudNameWins()
    {
        var store = Store;
        await store.SetSettingAsync("device_display_name:device", "Legacy local name");
        await using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM settings WHERE key = 'device_alias_migration_done';";
            await command.ExecuteNonQueryAsync();
        }
        await store.InitializeAsync();
        Assert.True(Assert.Single(await store.GetPendingDeviceDisplayNamesAsync("account")).IsLegacy);
        await store.MergeRemoteDeviceDisplayNamesAsync("account", new[]
        {
            new DeviceDisplayNameChange("device", "Existing cloud name", "cloud-revision")
        });
        Assert.Equal("Existing cloud name", await store.GetSettingAsync("device_display_name:device"));
        Assert.Empty(await store.GetPendingDeviceDisplayNamesAsync("account"));
        await store.InitializeAsync();
        Assert.Empty(await store.GetPendingDeviceDisplayNamesAsync("account"));
    }
}
