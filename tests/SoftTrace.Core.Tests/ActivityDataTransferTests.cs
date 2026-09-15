using SoftTrace.Core;

namespace SoftTrace.Core.Tests;

public sealed class ActivityDataTransferTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"softtrace-transfer-tests-{Guid.NewGuid():N}.db");

    private ActivityStore Store => new(_databasePath);

    public async Task InitializeAsync() => await Store.InitializeAsync();

    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExportAndImportPreservesActivitiesAndDeduplicatesOverlapOnSameDevice()
    {
        var app = new AppIdentity("testapp", "Test App", "C:\\testapp.exe");
        var origin = DateTimeOffset.Parse("2026-09-15T08:00:00Z");

        // Existing local data: 08:00 - 08:30 on device-1
        await Store.AddCompletedSegmentAsync("device-1", new(app, origin, origin.AddMinutes(30)));

        // Export data
        var exportedJson = await Store.ExportActivitiesToJsonAsync("device-1");
        Assert.Contains("Test App", exportedJson);
        Assert.Contains("device-1", exportedJson);

        // Re-import the same export into the same store -> should fully deduplicate!
        var reimportResult = await Store.ImportActivitiesFromJsonAsync(exportedJson);
        Assert.Equal(1, reimportResult.TotalItemsInPackage);
        Assert.Equal(0, reimportResult.ImportedCount);
        Assert.Equal(TimeSpan.Zero, reimportResult.ImportedDuration);
        Assert.Equal(TimeSpan.FromMinutes(30), reimportResult.OverlapDuration);

        // Create an export with partial overlap (08:15 - 08:45) on device-1
        var partialPackage = new ActivityExportPackage(
            1,
            DateTimeOffset.UtcNow,
            "device-1",
            [
                new ActivityExportItem(
                    Guid.NewGuid().ToString("N"),
                    "device-1",
                    "Device One",
                    origin.AddMinutes(15),
                    origin.AddMinutes(45),
                    "testapp",
                    "Test App",
                    "C:\\testapp.exe",
                    "local")
            ]);
        var partialJson = System.Text.Json.JsonSerializer.Serialize(partialPackage);
        var partialResult = await Store.ImportActivitiesFromJsonAsync(partialJson);

        // Only 08:30 - 08:45 (15 mins) should be imported
        Assert.Equal(1, partialResult.ImportedCount);
        Assert.Equal(TimeSpan.FromMinutes(15), partialResult.ImportedDuration);
        Assert.Equal(TimeSpan.FromMinutes(15), partialResult.OverlapDuration);

        // Now import for device-2 during the same time period 08:00 - 08:30 -> different device should NOT be subtracted!
        var device2Package = new ActivityExportPackage(
            1,
            DateTimeOffset.UtcNow,
            "device-2",
            [
                new ActivityExportItem(
                    Guid.NewGuid().ToString("N"),
                    "device-2",
                    "Device Two",
                    origin,
                    origin.AddMinutes(30),
                    "testapp",
                    "Test App",
                    "C:\\testapp.exe",
                    "local")
            ]);
        var device2Json = System.Text.Json.JsonSerializer.Serialize(device2Package);
        var device2Result = await Store.ImportActivitiesFromJsonAsync(device2Json);
        Assert.Equal(1, device2Result.ImportedCount);
        Assert.Equal(TimeSpan.FromMinutes(30), device2Result.ImportedDuration);
        Assert.Equal(TimeSpan.Zero, device2Result.OverlapDuration);

        // Verify device list shows both devices
        var devices = await Store.GetDevicesAsync();
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Id == "device-1");
        Assert.Contains(devices, d => d.Id == "device-2");
    }
}
