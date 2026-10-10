using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class DeviceDisplayNameSyncTests
{
    [Fact]
    public async Task TwoDevicesExchangeAliasesAndRetryOfflineChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"softtrace-alias-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var storeA = new ActivityStore(Path.Combine(root, "a.db"));
            var storeB = new ActivityStore(Path.Combine(root, "b.db"));
            await storeA.InitializeAsync();
            await storeB.InitializeAsync();
            using var cloud = new FakeCloud();
            using var http = new HttpClient(cloud);
            await using var coordinatorA = CreateCoordinator(storeA, Path.Combine(root, "a.json"), http);
            await using var coordinatorB = CreateCoordinator(storeB, Path.Combine(root, "b.json"), http);
            await storeA.SetDeviceDisplayNameAsync("stable-device-id", "工作电脑", coordinatorA.CurrentUserId);
            await coordinatorA.SyncNowAsync();
            Assert.False(coordinatorA.CurrentStatus.HasError, coordinatorA.CurrentStatus.Message);
            await coordinatorB.SyncNowAsync();
            Assert.False(coordinatorB.CurrentStatus.HasError, coordinatorB.CurrentStatus.Message);
            Assert.Equal("工作电脑", await storeB.GetSettingAsync("device_display_name:stable-device-id"));
            Assert.Empty(await storeB.GetPendingDeviceDisplayNamesAsync("test-account"));

            cloud.FailDeviceWrites = true;
            await storeB.SetDeviceDisplayNameAsync("stable-device-id", "离线新名称", coordinatorB.CurrentUserId);
            await coordinatorB.SyncNowAsync();
            Assert.True(coordinatorB.CurrentStatus.HasError);
            Assert.Equal("离线新名称", Assert.Single(await storeB.GetPendingDeviceDisplayNamesAsync("test-account")).DisplayName);
            cloud.FailDeviceWrites = false;
            await coordinatorB.SyncNowAsync();
            await coordinatorA.SyncNowAsync();
            Assert.False(coordinatorA.CurrentStatus.HasError, coordinatorA.CurrentStatus.Message);
            Assert.False(coordinatorB.CurrentStatus.HasError, coordinatorB.CurrentStatus.Message);
            Assert.Equal("离线新名称", await storeA.GetSettingAsync("device_display_name:stable-device-id"));
            Assert.Empty(await storeB.GetPendingDeviceDisplayNamesAsync("test-account"));
            Assert.All(cloud.Names.Keys, path => Assert.Contains("/users/test-account/devices/", path));
            Assert.All(cloud.Names.Values, fields => Assert.False(fields.TryGetProperty("deviceName", out _)));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task MigratedAliasUsesCreateOnlyAndNameQueriesPageThroughDevices()
    {
        using var cloud = new FakeCloud();
        using var http = new HttpClient(cloud);
        var client = new FirestoreSyncClient(http);
        var names = Enumerable.Range(0, 251)
            .Select(i => new DeviceDisplayNameChange($"device-{i:D3}", $"Name {i}", $"revision-{i}")).ToArray();
        await client.PushDeviceDisplayNamesAsync("project", "test-account", "test-token", names);
        Assert.Equal(251, (await client.PullDeviceDisplayNamesAsync("project", "test-account", "test-token")).Count);
        await client.PushDeviceDisplayNamesAsync("project", "test-account", "test-token",
            new[] { new DeviceDisplayNameChange("legacy-device", "Legacy alias", "legacy-revision", IsLegacy: true) });
        Assert.True(cloud.SawCreateOnlyWrite);
    }

    private static FirebaseSyncCoordinator CreateCoordinator(ActivityStore store, string path, HttpClient http)
    {
        var settings = new FirebaseSyncSettingsStore(path);
        settings.Save(new(true, "test@example.com", null, null, "test-account",
            FirebaseSyncSettingsStore.ProtectRefreshToken("synthetic-refresh-token")));
        return new(store, settings, new FirebaseAuthClient(http), new FirestoreSyncClient(http));
    }

    private sealed class FakeCloud : HttpMessageHandler
    {
        public Dictionary<string, JsonElement> Names { get; } = new(StringComparer.Ordinal);
        public bool FailDeviceWrites { get; set; }
        public bool SawCreateOnlyWrite { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "securetoken.googleapis.com")
                return Reply("""{"id_token":"test-token","refresh_token":"synthetic-refresh-token","user_id":"test-account","expires_in":"3600"}""");
            if (uri.Host == "identitytoolkit.googleapis.com") return Reply("""{"users":[]}""");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (uri.AbsolutePath.EndsWith(":commit", StringComparison.Ordinal))
            {
                if (FailDeviceWrites) throw new HttpRequestException("Synthetic offline failure");
                foreach (var write in body.RootElement.GetProperty("writes").EnumerateArray())
                {
                    var update = write.GetProperty("update");
                    var name = update.GetProperty("name").GetString()!;
                    if (write.TryGetProperty("currentDocument", out var condition))
                    {
                        Assert.False(condition.GetProperty("exists").GetBoolean());
                        SawCreateOnlyWrite = true;
                        if (Names.ContainsKey(name)) return new(HttpStatusCode.PreconditionFailed)
                        {
                            Content = new StringContent("""{"error":{"message":"Already exists"}}""")
                        };
                    }
                    Names[name] = update.GetProperty("fields").Clone();
                }
                return Reply("{}");
            }
            var query = body.RootElement.GetProperty("structuredQuery");
            if (query.GetProperty("from")[0].GetProperty("collectionId").GetString() == "activities") return Reply("[]");
            var prefix = uri.AbsolutePath["/v1/".Length..^":runQuery".Length] + "/devices/";
            string? cursor = query.TryGetProperty("startAt", out var start)
                ? start.GetProperty("values")[0].GetProperty("referenceValue").GetString() : null;
            var results = new JsonArray();
            foreach (var pair in Names.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                         (cursor is null || string.CompareOrdinal(p.Key, cursor) > 0))
                         .OrderBy(p => p.Key, StringComparer.Ordinal).Take(query.GetProperty("limit").GetInt32()))
            {
                results.Add(new JsonObject { ["document"] = new JsonObject
                {
                    ["name"] = pair.Key, ["fields"] = JsonNode.Parse(pair.Value.GetRawText())
                } });
            }
            return Reply(results.ToJsonString());
        }
        private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
