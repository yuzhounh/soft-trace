using Microsoft.Data.Sqlite;
using SoftTrace.Core;

namespace SoftTrace.Core.Tests;

public sealed class DailyTimelineTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, $"timeline-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _start = DateTimeOffset.Parse("2026-10-17T00:00:00+08:00");
    private ActivityStore Store => new(_path);
    public Task InitializeAsync() => Store.InitializeAsync();
    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ClipsMidnightSeparatesStatesAndLeavesUnknownGaps()
    {
        await Store.AddCompletedSegmentAsync("pc", new(new("chrome", "Chrome", null, "Tab A"),
            _start.AddMinutes(-5), _start.AddMinutes(10)));
        await Store.AddCompletedSegmentAsync("pc", new(new("__softtrace_state__", "空闲", null, Kind: "idle"),
            _start.AddMinutes(10), _start.AddMinutes(20)));
        await Store.AddCompletedSegmentAsync("other", new(new("other", "Other", null), _start, _start.AddHours(1)));
        var day = await Store.GetDailyTimelineAsync("pc", _start, _start.AddDays(1));
        Assert.Equal(600, day.ActiveSeconds);
        Assert.Equal(600, day.IdleSeconds);
        Assert.Equal(_start, day.FirstActivityUtc);
        Assert.Equal(_start.AddMinutes(10), day.LastActivityUtc);
        Assert.Equal(new[] { "active", "idle", "unknown" }, day.States.Select(e => e.Kind));
        Assert.Equal(_start.AddDays(1), day.States[^1].EndUtc);
        Assert.Equal("Tab A", day.Entries[0].WindowTitle);
        Assert.Single(await Store.GetUsageAsync(_start, _start.AddDays(1), "pc"));
    }

    [Fact]
    public async Task SyncMergeAndExportRoundTripPreserveTitlesAndStates()
    {
        await Store.AddCompletedSegmentAsync("pc", new(new("chrome", "Chrome", null, "Title <script>"), _start, _start.AddMinutes(5)));
        await Store.AddCompletedSegmentAsync("pc", new(new("state", "已锁屏", null, Kind: "locked"), _start.AddMinutes(5), _start.AddMinutes(8)));
        var pending = await Store.GetPendingSyncActivitiesAsync();
        Assert.Equal("Title <script>", pending.Single(e => e.Kind == "application").WindowTitle);
        Assert.Contains(pending, e => e.Kind == "locked");
        var targetPath = _path + ".target.db";
        try
        {
            var target = new ActivityStore(targetPath);
            await target.InitializeAsync();
            Assert.Equal(2, await target.MergeRemoteActivitiesAsync(pending));
            Assert.Equal(0, await target.MergeRemoteActivitiesAsync(pending));
            var day = await target.GetDailyTimelineAsync("pc", _start, _start.AddDays(1));
            Assert.Equal(180, day.States.Where(e => e.Kind == "locked").Sum(e => (e.EndUtc-e.StartUtc).TotalSeconds));
            var json = await target.ExportActivitiesToJsonAsync();
            Assert.Contains("Title <script>", json);
            Assert.Contains("\"kind\": \"locked\"", json);
            Assert.Equal(0, (await Store.ImportActivitiesFromJsonAsync(json)).ImportedCount);
            // Fresh import also preserves state records and titles.
            await using var connection = new SqliteConnection($"Data Source={targetPath};Pooling=False");
            await connection.OpenAsync();
            using (var command = connection.CreateCommand()) { command.CommandText = "DELETE FROM activities"; await command.ExecuteNonQueryAsync(); }
            Assert.Equal(2, (await target.ImportActivitiesFromJsonAsync(json)).ImportedCount);
            var imported = await target.GetDailyTimelineAsync("pc", _start, _start.AddDays(1));
            Assert.Equal("Title <script>", imported.Entries[0].WindowTitle);
            Assert.Equal("locked", imported.Entries[1].Kind);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(targetPath + suffix); }
    }

    [Fact]
    public async Task ExistingSchemaUpgradesWithoutChangingHistoricalRows()
    {
        await using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE activities DROP COLUMN window_title;
                ALTER TABLE activities DROP COLUMN kind;
                INSERT INTO activities(sync_id,device_id,device_name,start_utc,end_utc,process_name,app_name,is_open,created_utc,updated_utc,source)
                VALUES('old-id','pc','Old PC','2026-10-16T16:00:00.0000000+00:00','2026-10-16T16:10:00.0000000+00:00','chrome','Chrome',0,'2026-10-16T16:00:00Z','2026-10-16T16:10:00Z','local');
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Store.InitializeAsync();
        await Store.InitializeAsync();
        var day = await Store.GetDailyTimelineAsync("pc", _start, _start.AddDays(1));
        var old = Assert.Single(day.Entries);
        Assert.Equal("old-id", old.Id); Assert.Equal("application", old.Kind);
        Assert.Null(old.WindowTitle); Assert.Equal(600, day.ActiveSeconds);
    }

    [Fact]
    public async Task IdleBoundaryCanRetractAProvisionalCheckpoint()
    {
        var id = await Store.StartSegmentAsync("pc", new("chrome", "Chrome", null), _start);
        await Store.CheckpointSegmentAsync(id, _start.AddMinutes(2), false);
        await Store.CheckpointSegmentAsync(id, _start.AddMinutes(1), true);
        var day = await Store.GetDailyTimelineAsync("pc", _start, _start.AddDays(1));
        Assert.Equal(60, day.ActiveSeconds);
    }

    [Fact]
    public async Task IdleRetractionRemovesTitleChangesAfterLastInputWithoutTouchingOtherDevices()
    {
        await Store.AddCompletedSegmentAsync("pc", new(new("chrome", "Chrome", null, "A"), _start, _start.AddMinutes(2)));
        await Store.AddCompletedSegmentAsync("pc", new(new("chrome", "Chrome", null, "B"), _start.AddMinutes(2), _start.AddMinutes(3)));
        await Store.AddCompletedSegmentAsync("other", new(new("chrome", "Chrome", null), _start, _start.AddMinutes(3)));
        await Store.TrimIdleTailAsync("pc", _start, _start.AddMinutes(1));
        Assert.Equal(60, (await Store.GetDailyTimelineAsync("pc", _start, _start.AddDays(1))).ActiveSeconds);
        Assert.Equal(180, (await Store.GetDailyTimelineAsync("other", _start, _start.AddDays(1))).ActiveSeconds);
        Assert.All(await Store.GetPendingSyncActivitiesAsync(), e => Assert.True(e.EndUtc >= e.StartUtc));
    }

    [Fact]
    public void OverlappingImportedAppsCountOnlyOnceForActiveTime()
    {
        var a = new TimelineEntry("a", "application", "Chrome", "chrome", null, _start, _start.AddMinutes(10), false, "import");
        var b = a with { Id = "b", StartUtc = _start.AddMinutes(5), EndUtc = _start.AddMinutes(15) };
        var day = DailyTimeline.Create(_start, _start.AddDays(1), [a,b]);
        Assert.Equal(900, day.ActiveSeconds);
        Assert.Equal(2, day.Entries.Count);
    }
}
