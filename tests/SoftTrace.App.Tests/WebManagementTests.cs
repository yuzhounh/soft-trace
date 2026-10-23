using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class WebManagementTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, $"web-management-{Guid.NewGuid():N}.db");
    private ActivityStore _store = null!;
    private ActivityCaptureService _capture = null!;
    private LocalDashboardServer _server = null!;
    private HttpClient _http = null!;
    private bool _signedIn, _startup;
    private int _loginCalls, _startupWrites;
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddDays(-1);
    private CloudSyncStatus Status => new(_signedIn, false, _signedIn ? "test@example.com" : null,
        _signedIn ? "云端已是最新" : "尚未登录", null, false);

    public async Task InitializeAsync()
    {
        _store = new ActivityStore(_path); await _store.InitializeAsync();
        await _store.AddCompletedSegmentAsync("pc", new(new("chrome", "Chrome", null, "Original title"), _start, _start.AddMinutes(5)));
        _capture = new ActivityCaptureService(_store, "pc", "Test", 3);
        var actions = new DashboardActions(() => { _loginCalls++; _signedIn = true; return Task.CompletedTask; },
            () => { _signedIn = false; return Task.CompletedTask; }, () => _signedIn ? "account" : null,
            () => _startup, value => { _startupWrites++; _startup = value; });
        _server = new LocalDashboardServer(_store, _capture, () => Status, () => Task.CompletedTask, _ => { }, actions);
        await _server.StartAsync();
        _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(_server.Address) };
    }
    public async Task DisposeAsync()
    {
        _http.Dispose(); await _server.DisposeAsync(); await _capture.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
    private void Authorize() => _http.DefaultRequestHeaders.Add("X-SoftTrace-Token", new Uri(_server.BrowserUrl).Fragment[1..]);

    [Fact]
    public async Task OverviewRequiresAuthorizationAndReportsSameUsageWithoutExposingCredentials()
    {
        Assert.Equal(HttpStatusCode.Forbidden,(await _http.GetAsync("/api/overview?device=pc&zone=UTC")).StatusCode);
        Authorize();
        using var response=JsonDocument.Parse(await _http.GetStringAsync("/api/overview?device=pc&zone=UTC"));
        Assert.Equal(300,response.RootElement.GetProperty("activeSeconds").GetDouble());
        Assert.Equal(1,response.RootElement.GetProperty("activeDays").GetInt32());
        Assert.Equal("Chrome",response.RootElement.GetProperty("apps")[0].GetProperty("appName").GetString());
        Assert.False(response.RootElement.TryGetProperty("windowTitle",out _));
        var patterns = response.RootElement.GetProperty("patterns");
        Assert.Equal(24, patterns.GetProperty("hours").GetArrayLength());
        Assert.Equal(7, patterns.GetProperty("weekdays").GetArrayLength());
        Assert.Equal(300, patterns.GetProperty("hours").EnumerateArray().Sum(hour => hour.GetProperty("activeSeconds").GetDouble()));
        Assert.Contains("按固定月份分组", patterns.GetProperty("calendarBasis").GetString());
        Assert.Contains("softTracePatterns", await _http.GetStringAsync("/patterns.js"));
        Assert.Equal(HttpStatusCode.BadRequest,(await _http.GetAsync("/api/overview?device=pc&zone=invalid-zone")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await _http.GetAsync("/api/account/avatar")).StatusCode);
    }

    [Fact]
    public async Task WebAccountDeviceAndPreferencesKeepExistingRecordsAndOnlyChangeStartupExplicitly()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsync("/api/account/login", null)).StatusCode);
        Assert.Equal(0, _loginCalls);
        Authorize();
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync("/api/account/login", null)).StatusCode);
        Assert.True(_signedIn);
        Assert.Equal(1, _loginCalls);
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/preferences", new { idleMinutes = 4, autoStart = false })).StatusCode);
        Assert.Equal(0, _startupWrites);
        Assert.Equal(4, _capture.IdleThresholdMinutes);
        Assert.Equal(4, await _store.GetIdleThresholdMinutesAsync());
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/preferences", new { idleMinutes = 4, autoStart = true })).StatusCode);
        Assert.True(_startup); Assert.Equal(1, _startupWrites);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/preferences", new { idleMinutes = 0, autoStart = false })).StatusCode);
        Assert.True(_startup);
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/device/name", new { deviceId = "pc", name = "Renamed PC" })).StatusCode);
        Assert.Equal("Renamed PC", Assert.Single(await _store.GetDevicesAsync()).Name);
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync("/api/account/logout", null)).StatusCode);
        Assert.False(_signedIn);
        var day = await _store.GetDailyTimelineAsync("pc", _start, _start.AddDays(1));
        Assert.Equal("Original title", Assert.Single(day.Entries).WindowTitle);
        Assert.Equal(300, day.ActiveSeconds);
        var icon = await _http.GetAsync("/brand.ico");
        Assert.Equal("image/x-icon", icon.Content.Headers.ContentType!.MediaType);
        Assert.True((await icon.Content.ReadAsByteArrayAsync()).Length > 100);
    }

    [Fact]
    public async Task UsageExportAndBrowserFileImportSupportTheOldDataManagementWorkflow()
    {
        Authorize();
        using var usage = JsonDocument.Parse(await _http.GetStringAsync("/api/usage?device=pc"));
        Assert.InRange(usage.RootElement.GetProperty("totalSeconds").GetDouble(), 299.99, 300.01);
        var export = await _http.GetAsync("/api/export?device=pc");
        Assert.Contains("SoftTrace_Export_", export.Content.Headers.ContentDisposition!.FileNameStar);
        var package = JsonNode.Parse(await export.Content.ReadAsStringAsync())!;
        var item = package["activities"]![0]!;
        Assert.Equal("Original title", item["windowTitle"]!.GetValue<string>());
        item["syncId"] = "new-web-import";
        item["startUtc"] = _start.AddHours(1).ToString("O");
        item["endUtc"] = _start.AddHours(2).ToString("O");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(package.ToJsonString(), Encoding.UTF8, "application/json"), "file", "records.json");
        var result = await _http.PostAsync("/api/import", form);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using var imported = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal(1, imported.RootElement.GetProperty("imported").GetInt32());
        var day = await _store.GetDailyTimelineAsync("pc", _start, _start.AddDays(1));
        Assert.Equal(2, day.Entries.Count);
        Assert.Equal(3900, day.ActiveSeconds);
        using var repeated = new MultipartFormDataContent();
        repeated.Add(new StringContent(package.ToJsonString(), Encoding.UTF8, "application/json"), "file", "records.json");
        using var duplicate = JsonDocument.Parse(await (await _http.PostAsync("/api/import", repeated)).Content.ReadAsStringAsync());
        Assert.Equal(0, duplicate.RootElement.GetProperty("imported").GetInt32());
    }
}
