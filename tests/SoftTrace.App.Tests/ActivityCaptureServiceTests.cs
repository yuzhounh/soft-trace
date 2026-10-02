using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class ActivityCaptureServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"softtrace-capture-{Guid.NewGuid():N}.db");
    private ActivityStore Store => new(_databasePath);
    private const string DeviceId = "capture-test-device";
    private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow.AddMinutes(-10);

    public Task InitializeAsync() => Store.InitializeAsync();

    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StorageFailurePausesLiveLoopAndResumeDoesNotFillTheGap()
    {
        await using var capture = new ActivityCaptureService(Store, DeviceId, "Test", 3);
        var segmentId = await SeedSegmentAsync(capture);
        await ExecuteSqlAsync("""
            CREATE TRIGGER fail_activity_update BEFORE UPDATE ON activities
            BEGIN SELECT RAISE(FAIL, 'test storage failure'); END;
            """);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureCount = 0;
        capture.CaptureFailed += (_, exception) =>
        {
            Interlocked.Increment(ref failureCount);
            failed.TrySetResult(exception);
        };

        using var cancellation = new CancellationTokenSource();
        var poll = RunPollAsync(capture, cancellation.Token);
        try
        {
            var first = await Task.WhenAny(failed.Task, poll).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(failed.Task, first);
            Assert.IsType<SqliteException>(await failed.Task);
            Assert.True(capture.CurrentStatus.IsPaused);
            Assert.Contains("test storage failure", capture.CurrentStatus.Error);
            Assert.Null(capture.CurrentStatus.CurrentApp);
            Assert.False(poll.IsCompleted);

            await Task.Delay(1100); // Allow another real polling tick while paused.
            Assert.Equal(1, failureCount);
            Assert.False(poll.IsCompleted);

            // A premature resume must remain paused, without throwing into the UI event handler.
            await capture.SetPausedAsync(false);
            Assert.True(capture.CurrentStatus.IsPaused);
            Assert.NotNull(capture.CurrentStatus.Error);
        }
        finally
        {
            cancellation.Cancel();
            await poll.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await ExecuteSqlAsync("DROP TRIGGER fail_activity_update;");
        await capture.SetPausedAsync(false);
        Assert.False(capture.CurrentStatus.IsPaused);
        Assert.Null(capture.CurrentStatus.Error);
        Assert.Null(capture.CurrentStatus.CurrentApp);

        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT end_utc, is_open FROM activities WHERE id = $id";
        command.Parameters.AddWithValue("$id", segmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(_origin.AddSeconds(15), DateTimeOffset.Parse(reader.GetString(0)));
        Assert.Equal(0, reader.GetInt32(1));
    }

    [Fact]
    public async Task PauseWriteFailureIsReportedAndDisposalDoesNotExtendTheSegment()
    {
        var capture = new ActivityCaptureService(Store, DeviceId, "Test", 3);
        await SeedSegmentAsync(capture);
        await ExecuteSqlAsync("""
            CREATE TRIGGER fail_activity_update BEFORE UPDATE ON activities
            BEGIN SELECT RAISE(FAIL, 'test storage failure'); END;
            """);
        await capture.SetPausedAsync(true);
        Assert.True(capture.CurrentStatus.IsPaused);
        Assert.NotNull(capture.CurrentStatus.Error);
        Assert.Null(capture.CurrentStatus.CurrentApp);
        await capture.DisposeAsync();
    }

    [Fact]
    public async Task NormalCancellationDoesNotReportCaptureFailure()
    {
        await using var capture = new ActivityCaptureService(Store, DeviceId, "Test", 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RunPollAsync(capture, cancellation.Token);
        Assert.Null(capture.CurrentStatus.Error);
        Assert.False(capture.CurrentStatus.IsPaused);
    }

    private async Task<long> SeedSegmentAsync(ActivityCaptureService capture)
    {
        var app = new AppIdentity("capture-test-unobservable-process", "Test", null);
        var id = await Store.StartSegmentAsync(DeviceId, app, _origin);
        await Store.CheckpointSegmentAsync(id, _origin.AddSeconds(15), close: false);
        // Seed the saved segment without observing or writing the user's real foreground activity.
        SetField(capture, "_currentSegmentId", id);
        SetField(capture, "_currentApp", app);
        SetField(capture, "_currentSegmentStartUtc", _origin);
        SetField(capture, "_lastCheckpointUtc", _origin);
        return id;
    }

    private static void SetField(ActivityCaptureService capture, string name, object value) =>
        typeof(ActivityCaptureService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(capture, value);

    private static Task RunPollAsync(ActivityCaptureService capture, CancellationToken cancellationToken) =>
        (Task)typeof(ActivityCaptureService)
            .GetMethod("PollAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(capture, [cancellationToken])!;

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
