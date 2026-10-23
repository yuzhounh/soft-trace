using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftTrace.Core;

namespace SoftTrace.App;

/// <summary>The tray process owns this loopback-only server; no external runtime or CDN is needed.</summary>
public sealed record DashboardActions(Func<Task> SignIn, Func<Task> SignOut,
    Func<string?> CurrentUserId, Func<bool> GetAutoStart, Action<bool> SetAutoStart);

public sealed partial class LocalDashboardServer(ActivityStore store, ActivityCaptureService capture,
    Func<CloudSyncStatus> syncStatus, Func<Task> syncNow,
    Action<Exception> logError, DashboardActions? actions = null, string? webDirectory = null) : IAsyncDisposable
{
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _webDirectory = Path.GetFullPath(webDirectory ?? FindWebDirectory());
    private WebApplication? _server;
    public string Address { get; private set; } = string.Empty;
    public string BrowserUrl => Address + "/#" + _token;

    public async Task StartAsync()
    {
        if (!File.Exists(Path.Combine(_webDirectory, "index.html")))
            throw new FileNotFoundException("网页资源缺失，请完整解压或安装 SoftTrace 程序包（包含 Web 目录）。");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(LocalDashboardServer).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders(); // Never log the browser access token or window titles.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256L * 1024 * 1024);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        _server = server;
        server.Use(async (context, next) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote) ||
                context.Request.Host.Host != "127.0.0.1")
            {
                context.Response.StatusCode = 403;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            if (context.Request.Path.StartsWithSegments("/api") &&
                context.Request.Headers["X-SoftTrace-Token"].ToString() != _token)
            {
                context.Response.StatusCode = 403;
                return;
            }
            try { await next(context); }
            catch (ArgumentException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "日期或查询参数无效。" });
            }
            catch (Exception exception)
            {
                logError(exception);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "暂时无法完成操作，请重试或查看本地日志。" });
            }
        });
        server.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        server.MapGet("/dashboard.css", () => Asset("dashboard.css", "text/css; charset=utf-8"));
        server.MapGet("/dashboard.js", () => Asset("dashboard.js", "text/javascript; charset=utf-8"));
        server.MapGet("/management.js", () => Asset("management.js", "text/javascript; charset=utf-8"));
        server.MapGet("/calendar.js", () => Asset("calendar.js", "text/javascript; charset=utf-8"));
        server.MapGet("/overview.js", () => Asset("overview.js", "text/javascript; charset=utf-8"));
        server.MapGet("/patterns.js", () => Asset("patterns.js", "text/javascript; charset=utf-8"));
        server.MapGet("/brand.ico", () => Asset("brand.ico", "image/x-icon"));
        server.MapGet("/api/status", async () =>
        {
            var devices = (await store.GetDevicesAsync()).ToList();
            if (devices.All(d => d.Id != capture.DeviceId))
                devices.Insert(0, new DeviceSummary(capture.DeviceId, capture.DeviceName));
            return Results.Json(new { capture = capture.CurrentStatus, sync = syncStatus(), devices,
                localDeviceId = capture.DeviceId, idleMinutes = capture.IdleThresholdMinutes,
                autoStart = actions?.GetAutoStart() ?? false, features = new { overview = true, avatar = true }, nowUtc = DateTimeOffset.UtcNow });
        });
        server.MapGet("/api/day", async (HttpContext context) =>
        {
            var query = context.Request.Query;
            if (!DateTimeOffset.TryParse(query["start"], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var start) ||
                !DateTimeOffset.TryParse(query["end"], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var end) || string.IsNullOrWhiteSpace(query["device"]))
                throw new ArgumentException("Invalid day");
            if (end - start > TimeSpan.FromHours(26) || end <= start || start > DateTimeOffset.UtcNow)
                throw new ArgumentException("Invalid range");
            end = end > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : end;
            return Results.Json(await store.GetDailyTimelineAsync(query["device"].ToString(), start, end,
                context.RequestAborted));
        });
        server.MapPost("/api/pause", async () =>
        {
            await capture.SetPausedAsync(!capture.IsPaused);
            return Results.Json(capture.CurrentStatus);
        });
        server.MapPost("/api/sync", async () => { await syncNow(); return Results.Json(syncStatus()); });
        MapManagement(server);
        MapOverview(server);
        server.MapGet("/{**assetPath}", (string? assetPath) => StaticAsset(assetPath));
        await server.StartAsync();
        Address = server.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
    }

    private static string FindWebDirectory()
    {
        // In the active original workspace, both development and daily builds read the same
        // source assets. A standalone installation instead uses its adjacent Web directory.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "src", "SoftTrace.App", "Web");
            if (File.Exists(Path.Combine(directory.FullName, "SoftTrace.sln")) && Directory.Exists(source)) return source;
        }
        return Path.Combine(AppContext.BaseDirectory, "Web");
    }

    private IResult Asset(string name, string contentType)
    {
        var path = Path.Combine(_webDirectory, name);
        if (name == "brand.ico" && !File.Exists(path))
            path = Path.GetFullPath(Path.Combine(_webDirectory, "..", "Assets", "SoftTrace.ico"));
        // Read on every request instead of caching an assembly resource or file metadata.
        return File.Exists(path) ? Results.Bytes(File.ReadAllBytes(path), contentType) : Results.NotFound();
    }

    private IResult StaticAsset(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("api/", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part.StartsWith('.')))
            return Results.NotFound();
        var path = Path.GetFullPath(Path.Combine(_webDirectory, relative));
        if (!path.StartsWith(_webDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8", ".json" => "application/json",
            ".ico" => "image/x-icon", ".png" => "image/png", ".svg" => "image/svg+xml",
            ".webp" => "image/webp", ".woff2" => "font/woff2", _ => null
        };
        return contentType is null ? Results.NotFound() : Asset(relative, contentType);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _server.StopAsync(timeout.Token).ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
        _avatarHttp.Dispose();
    }
}
