using System.Globalization;
using System.IO;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SoftTrace.App;

public sealed partial class LocalDashboardServer
{
    private readonly HttpClient _avatarHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _avatarGate = new(1, 1);
    private string? _avatarUrl, _avatarData;

    private void MapOverview(WebApplication server)
    {
        server.MapGet("/api/overview", async (HttpContext context) =>
        {
            var (start, end, device) = ReadExportFilter(context);
            if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("请选择设备。");
            var zoneId = context.Request.Query["zone"].ToString();
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
            catch (TimeZoneNotFoundException) { return Results.BadRequest(new { error = "无法识别当前时区。" }); }
            var until = end ?? DateTimeOffset.UtcNow;
            until = until > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : until;
            return Results.Json(await store.GetPeriodOverviewAsync(device, start, until, zone, context.RequestAborted));
        });
        server.MapGet("/api/account/avatar", async (HttpContext context) =>
        {
            var status = syncStatus();
            if (!status.IsSignedIn || !Uri.TryCreate(status.PhotoUrl, UriKind.Absolute, out var photo) ||
                photo.Scheme != "https" || !(photo.Host.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                    photo.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase))) return Results.NotFound();
            await _avatarGate.WaitAsync(context.RequestAborted);
            try
            {
                if (_avatarUrl == photo.AbsoluteUri && _avatarData is not null) return Results.Json(new { dataUrl = _avatarData });
                using var response = await _avatarHttp.GetAsync(photo, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                var type = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode || type is not ("image/png" or "image/jpeg" or "image/webp") ||
                    response.Content.Headers.ContentLength > 512 * 1024) return Results.NotFound();
                await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                using var output = new MemoryStream(); var buffer = new byte[8192];
                int count;
                while ((count = await input.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (output.Length + count > 512 * 1024) return Results.NotFound();
                    output.Write(buffer, 0, count);
                }
                _avatarUrl = photo.AbsoluteUri; _avatarData = $"data:{type};base64,{Convert.ToBase64String(output.ToArray())}";
                return Results.Json(new { dataUrl = _avatarData });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            { return Results.NotFound(); }
            finally { _avatarGate.Release(); }
        });
    }
}
