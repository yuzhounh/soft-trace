using System.IO;
using System.Net;
using System.Net.Http;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class FirebaseSyncRetryTests
{
    [Fact]
    public async Task ExplicitSyncRetriesImmediatelyDuringAutomaticBackoff()
    {
        using var handler = new RetryHandler();
        using var http = new HttpClient(handler);
        var schedule = new CloudSyncRetrySchedule(jitter: () => 0.5);
        await WithCoordinator(http, schedule, async coordinator =>
        {
            await coordinator.SyncNowAsync();
            Assert.True(coordinator.CurrentStatus.HasError);
            Assert.InRange(schedule.Delay.TotalSeconds, 115, 120);
            await coordinator.SyncNowAsync();
            Assert.Equal(2, handler.Calls);
            Assert.InRange(schedule.Delay.TotalSeconds, 235, 240);
        });
    }

    [Fact]
    public async Task DisposalCancelsAndAwaitsInitialAutomaticSync()
    {
        using var handler = new RetryHandler { Block = true };
        using var http = new HttpClient(handler);
        await WithCoordinator(http, new CloudSyncRetrySchedule(), async coordinator =>
        {
            coordinator.Start();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        Assert.True(handler.Ended.Task.IsCompleted);
    }

    private static async Task WithCoordinator(HttpClient http, CloudSyncRetrySchedule schedule,
        Func<FirebaseSyncCoordinator, Task> action)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FirebaseSyncCoordinator? coordinator = null;
        try
        {
            var store = new ActivityStore(Path.Combine(root, "data.db"));
            await store.InitializeAsync();
            var settings = new FirebaseSyncSettingsStore(Path.Combine(root, "sync.json"));
            settings.Save(new(true, "test@example.com", null, null, "test-user",
                FirebaseSyncSettingsStore.ProtectRefreshToken("synthetic-refresh-token")));
            coordinator = new(store, settings, new FirebaseAuthClient(http), new FirestoreSyncClient(http), schedule);
            await action(coordinator);
        }
        finally
        {
            if (coordinator is not null) await coordinator.DisposeAsync();
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test directory is outside the test output.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public bool Block { get; init; }
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            try
            {
                if (Block) await Task.Delay(Timeout.Infinite, token);
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            }
            finally { Ended.TrySetResult(); }
        }
    }
}
