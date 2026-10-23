using System.Net;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class DashboardTests
{
    private static readonly CloudSyncStatus Offline = new(false, false, null, "尚未登录", null, false);

    [Fact]
    public async Task LoopbackApiRequiresTokenServesEmbeddedAssetsAndDoesNotNeedTheBrowserForCapture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"dashboard-{Guid.NewGuid():N}.db");
        try
        {
            var store = new ActivityStore(path); await store.InitializeAsync();
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            await store.AddCompletedSegmentAsync("pc", new(new("chrome", "Chrome", null, "Test tab"), start, start.AddMinutes(5)));
            await using var capture = new ActivityCaptureService(store, "pc", "Test PC", 3);
            await using var server = new LocalDashboardServer(store, capture, () => Offline,
                () => Task.CompletedTask, _ => { });
            await server.StartAsync();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(server.Address) };
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/status")).StatusCode);
            Assert.Contains("每日记录", await http.GetStringAsync("/"));
            Assert.Contains("renderTimeline", await http.GetStringAsync("/dashboard.js"));
            http.DefaultRequestHeaders.Add("X-SoftTrace-Token", new Uri(server.BrowserUrl).Fragment[1..]);
            Assert.Contains("\"localDeviceId\":\"pc\"", await http.GetStringAsync("/api/status"));
            var url = $"/api/day?device=pc&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(start.AddDays(1).ToString("O"))}";
            using var day = JsonDocument.Parse(await http.GetStringAsync(url));
            Assert.Equal(300, day.RootElement.GetProperty("activeSeconds").GetDouble());
            Assert.Equal("Test tab", day.RootElement.GetProperty("entries")[0].GetProperty("windowTitle").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/day?device=pc&start=no&end=no")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/pause", null)).StatusCode);
            Assert.True(capture.IsPaused);
            Assert.Equal("paused", (await store.GetPendingSyncActivitiesAsync()).Single(e => e.Kind != "application").Kind);
            using var hostileHost = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            hostileHost.Headers.Host = "attacker.example";
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(hostileHost)).StatusCode);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task TitleChangesIdleLockAndSleepProduceDistinctNonOverlappingIntervals()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"capture-states-{Guid.NewGuid():N}.db");
        try
        {
            var store = new ActivityStore(path); await store.InitializeAsync();
            var idle = TimeSpan.Zero;
            var app = new AppIdentity("chrome", "Chrome", null, "Tab A");
            await using var capture = new ActivityCaptureService(store, "pc", "Test", 3, () => idle, () => app);
            await Observe(capture);
            await Task.Delay(20);
            app = app with { WindowTitle = "Tab B" };
            await Observe(capture);
            await Task.Delay(20);
            idle = TimeSpan.FromMinutes(4); await Observe(capture);
            Assert.True(capture.CurrentStatus.IsIdle);
            await Task.Delay(20);
            idle = TimeSpan.Zero; await Observe(capture);
            await SystemState(capture, true, "locked");
            await Task.Delay(20);
            await SystemState(capture, true, "sleep");
            await Task.Delay(20);
            await SystemState(capture, false, "sleep");
            Assert.True(capture.CurrentStatus.IsIdle); // Waking does not unlock a locked session.
            await Task.Delay(20);
            await SystemState(capture, false, "locked");
            await Observe(capture);
            await capture.SetPausedAsync(true);
            await Task.Delay(20);
            await capture.SetPausedAsync(false);
            var rows = (await store.GetPendingSyncActivitiesAsync()).OrderBy(e => e.StartUtc).ToList();
            Assert.Contains(rows, e => e.WindowTitle == "Tab A");
            Assert.Contains(rows, e => e.WindowTitle == "Tab B");
            foreach (var kind in new[] { "idle", "locked", "sleep", "paused" }) Assert.Contains(rows, e => e.Kind == kind);
            var intervals = rows.Where(e => e.EndUtc > e.StartUtc).ToList();
            for (var i = 1; i < intervals.Count; i++) Assert.True(intervals[i-1].EndUtc <= intervals[i].StartUtc);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private static Task Observe(ActivityCaptureService capture) => (Task)typeof(ActivityCaptureService)
        .GetMethod("ObserveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture, [CancellationToken.None])!;
    private static Task SystemState(ActivityCaptureService capture, bool unavailable, string kind) => (Task)typeof(ActivityCaptureService)
        .GetMethod("SetSystemUnavailableAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture, [unavailable, kind])!;

    [Fact]
    public async Task FirestoreRoundTripKeepsStateDurationOutOfOlderClientsSoftwareTotals()
    {
        using var cloud = new ActivityCloud(); using var http = new HttpClient(cloud);
        var client = new FirestoreSyncClient(http);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var state = new SyncActivity("state", "pc", "Test", "__softtrace_state__", "空闲", null,
            start, start.AddMinutes(10), start.AddMinutes(10), false, "local", Kind: "idle");
        var app = state with { SyncId = "app", Kind = "application", ProcessName = "chrome", AppName = "Chrome", WindowTitle = "A tab" };
        await client.PushActivitiesAsync("project", "account", "synthetic-token", [state, app]);
        var legacy = cloud.Documents[0]!["fields"]!;
        Assert.Equal(legacy["startUtc"]!.ToJsonString(), legacy["endUtc"]!.ToJsonString());
        var pulled = await client.PullActivitiesAsync("project", "account", "synthetic-token", null);
        Assert.Equal(state.EndUtc, pulled.Activities.Single(e => e.Kind == "idle").EndUtc);
        Assert.Equal("A tab", pulled.Activities.Single(e => e.Kind == "application").WindowTitle);
        // Documents written by the previous version have neither optional field.
        cloud.Documents[1]!["fields"]!.AsObject().Remove("kind");
        cloud.Documents[1]!["fields"]!.AsObject().Remove("windowTitle");
        pulled = await client.PullActivitiesAsync("project", "account", "synthetic-token", null);
        Assert.Null(pulled.Activities.Single(e => e.SyncId == "app").WindowTitle);
        Assert.Equal("application", pulled.Activities.Single(e => e.SyncId == "app").Kind);
    }

    private sealed class ActivityCloud : HttpMessageHandler
    {
        public JsonArray Documents { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(":commit"))
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                foreach (var write in body["writes"]!.AsArray())
                {
                    var document = write!["update"]!.DeepClone();
                    document["fields"]!["serverUpdatedUtc"] = new JsonObject { ["timestampValue"] = DateTimeOffset.UtcNow.ToString("O") };
                    Documents.Add(document);
                }
                return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            var results = new JsonArray(Documents.Select(d => (JsonNode)new JsonObject { ["document"] = d!.DeepClone() }).ToArray());
            return new(HttpStatusCode.OK) { Content = new StringContent(results.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
