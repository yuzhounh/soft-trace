using SoftTrace.Core;

namespace SoftTrace.Core.Tests;

public sealed class ActivityStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"softtrace-tests-{Guid.NewGuid():N}.db");

    private ActivityStore Store => new(_databasePath);

    public async Task InitializeAsync() => await Store.InitializeAsync();

    public Task DisposeAsync()
    {
        SqliteCleanup.DeleteDatabaseFiles(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UsageGroupsSegmentsByApplication()
    {
        var chrome = new AppIdentity("chrome", "Google Chrome", "C:\\Chrome.exe");
        var code = new AppIdentity("Code", "Visual Studio Code", "C:\\Code.exe");
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");

        await Store.AddCompletedSegmentAsync("test-pc", new(chrome, origin, origin.AddMinutes(10)));
        await Store.AddCompletedSegmentAsync("test-pc", new(chrome, origin.AddMinutes(15), origin.AddMinutes(35)));
        await Store.AddCompletedSegmentAsync("test-pc", new(code, origin.AddMinutes(35), origin.AddMinutes(40)));

        var usage = await Store.GetUsageAsync(origin, origin.AddHours(1));

        Assert.Equal(2, usage.Count);
        Assert.Equal("Google Chrome", usage[0].AppName);
        Assert.Equal("C:\\Chrome.exe", usage[0].ExecutablePath);
        AssertClose(TimeSpan.FromMinutes(30), usage[0].Duration);
        Assert.Equal(2, usage[0].SegmentCount);
        AssertClose(TimeSpan.FromMinutes(5), usage[1].Duration);
    }

    [Fact]
    public async Task UsageClipsSegmentsAtDateRangeBoundaries()
    {
        var app = new AppIdentity("app", "App", null);
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(app, origin.AddMinutes(-10), origin.AddMinutes(10)));

        var usage = await Store.GetUsageAsync(origin, origin.AddMinutes(5));

        var row = Assert.Single(usage);
        AssertClose(TimeSpan.FromMinutes(5), row.Duration);
    }

    [Fact]
    public async Task RecoveryClosesOpenSegmentsAtTheirLastCheckpoint()
    {
        var app = new AppIdentity("app", "App", null);
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        var id = await Store.StartSegmentAsync("test-pc", app, origin);
        await Store.CheckpointSegmentAsync(id, origin.AddSeconds(15), close: false);

        await Store.RecoverInterruptedSegmentsAsync();
        var usage = await Store.GetUsageAsync(origin, origin.AddMinutes(1));

        AssertClose(TimeSpan.FromSeconds(15), Assert.Single(usage).Duration);
    }

    [Fact]
    public async Task RemoteActivitiesAreIdempotentAndNewerUpdatesWin()
    {
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        var initial = new SyncActivity(
            "shared-segment",
            "remote-pc",
            "Remote PC",
            "chrome",
            "Google Chrome",
            "C:\\Chrome.exe",
            origin,
            origin.AddMinutes(5),
            origin.AddMinutes(5),
            false,
            "local");

        Assert.Equal(1, await Store.MergeRemoteActivitiesAsync([initial]));
        Assert.Equal(0, await Store.MergeRemoteActivitiesAsync([initial]));

        var newer = initial with
        {
            EndUtc = origin.AddMinutes(8),
            UpdatedUtc = origin.AddMinutes(8)
        };
        Assert.Equal(1, await Store.MergeRemoteActivitiesAsync([newer]));

        var usage = await Store.GetUsageAsync(origin, origin.AddHours(1));
        AssertClose(TimeSpan.FromMinutes(8), Assert.Single(usage).Duration);
        Assert.Empty(await Store.GetPendingSyncActivitiesAsync());
    }

    [Fact]
    public async Task SyncPullCursorRoundTripsTimestampAndDocumentName()
    {
        var cursor = new SyncPullCursor(
            DateTimeOffset.Parse("2026-09-14T12:34:56.789Z"),
            "projects/soft-trace/databases/(default)/documents/users/u/activities/a");

        await Store.SetLastSyncPullCursorAsync(cursor);
        var restored = await Store.GetLastSyncPullCursorAsync();

        Assert.Equal(cursor, restored);
    }

    private static void AssertClose(TimeSpan expected, TimeSpan actual) =>
        Assert.InRange(Math.Abs((expected - actual).TotalMilliseconds), 0, 20);

    private static class SqliteCleanup
    {
        public static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
