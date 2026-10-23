using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SoftTrace.App;

public sealed partial class LocalDashboardServer
{
    private sealed record Preferences(int IdleMinutes, bool AutoStart);
    private sealed record DeviceName(string DeviceId, string Name);
    private readonly SemaphoreSlim _importGate = new(1, 1);

    private void MapManagement(WebApplication server)
    {
        server.MapPost("/api/account/login", async () =>
        {
            if (actions is null) return Results.StatusCode(503);
            await actions.SignIn();
            return Results.Json(syncStatus());
        });
        server.MapPost("/api/account/logout", async () =>
        {
            if (actions is null) return Results.StatusCode(503);
            await actions.SignOut();
            return Results.Json(syncStatus());
        });
        server.MapPost("/api/preferences", async (HttpContext context) =>
        {
            if (actions is null) return Results.StatusCode(503);
            var preferences = await context.Request.ReadFromJsonAsync<Preferences>(context.RequestAborted);
            if (preferences is null || preferences.IdleMinutes is < 1 or > 60)
                return Results.BadRequest(new { error = "空闲阈值需为 1–60 分钟。" });
            // Only an explicit checkbox change touches the startup registration.
            if (actions.GetAutoStart() != preferences.AutoStart) actions.SetAutoStart(preferences.AutoStart);
            await store.SetIdleThresholdMinutesAsync(preferences.IdleMinutes, context.RequestAborted);
            capture.IdleThresholdMinutes = preferences.IdleMinutes;
            return Results.Ok(new { message = "设置已保存。" });
        });
        server.MapPost("/api/device/name", async (HttpContext context) =>
        {
            var name = await context.Request.ReadFromJsonAsync<DeviceName>(context.RequestAborted);
            if (name is null || string.IsNullOrWhiteSpace(name.Name) || name.Name.Trim().Length > 80 ||
                !(name.DeviceId == capture.DeviceId || (await store.GetDevicesAsync()).Any(d => d.Id == name.DeviceId)))
                return Results.BadRequest(new { error = "请选择已有设备，并填写 1–80 个字符的名称。" });
            await store.SetDeviceDisplayNameAsync(name.DeviceId, name.Name.Trim(), actions?.CurrentUserId());
            await syncNow();
            return Results.Ok(new { message = "设备名称已保存。", sync = syncStatus() });
        });
        server.MapGet("/api/usage", async (HttpContext context) =>
        {
            var (start, end, device) = ReadExportFilter(context);
            var from = start ?? await store.GetEarliestActivityUtcAsync(device) ?? DateTimeOffset.UtcNow.AddDays(-1);
            var until = end ?? DateTimeOffset.UtcNow;
            var usage = await store.GetUsageAsync(from, until, device, context.RequestAborted);
            return Results.Json(new { rows = usage.Select(row => new { row.AppName, row.ProcessName,
                seconds = row.Duration.TotalSeconds, row.SegmentCount }),
                totalSeconds = usage.Sum(row => row.Duration.TotalSeconds) });
        });
        server.MapGet("/api/export", async (HttpContext context) =>
        {
            var (start, end, device) = ReadExportFilter(context);
            var json = await store.ExportActivitiesToJsonAsync(device, start, end, context.RequestAborted);
            return Results.File(Encoding.UTF8.GetBytes(json), "application/json",
                $"SoftTrace_Export_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        });
        server.MapPost("/api/import", async (HttpContext context) =>
        {
            if (!await _importGate.WaitAsync(0)) return Results.Conflict(new { error = "正在导入，请等待完成。" });
            string? scratch = null;
            try
            {
                if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "请选择导入文件。" });
                const int maxBytes = 256 * 1024 * 1024;
                var form = await context.Request.ReadFormAsync(new FormOptions
                    { MultipartBodyLengthLimit = maxBytes, MemoryBufferThreshold = maxBytes }, context.RequestAborted);
                if (form.Files.Count != 1 || form.Files[0].Length is <= 0 or > maxBytes)
                    return Results.BadRequest(new { error = "请选择一个不超过 256 MB 的文件。" });
                var file = form.Files[0];
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (extension is not (".json" or ".zip" or ".db"))
                    return Results.BadRequest(new { error = "支持 SoftTrace JSON 或 ManicTime ZIP/DB。" });
                scratch = Path.Combine(AppContext.BaseDirectory, "tmp", "imports", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                var path = Path.Combine(scratch, "import" + extension);
                await using (var output = File.Create(path)) await file.CopyToAsync(output, context.RequestAborted);
                if (extension == ".json")
                {
                    var result = await store.ImportActivitiesFromFileAsync(path, context.RequestAborted);
                    return Results.Json(new { imported = result.ImportedCount, read = result.TotalItemsInPackage,
                        seconds = result.ImportedDuration.TotalSeconds, overlapSeconds = result.OverlapDuration.TotalSeconds });
                }
                var manic = await store.ImportManicTimeAsync(path, capture.DeviceId, capture.DeviceName, context.RequestAborted);
                return Results.Json(new { imported = manic.ImportedSegmentCount, read = manic.ReadSegmentCount,
                    seconds = manic.ImportedDuration.TotalSeconds, overlapSeconds = manic.OverlapDuration.TotalSeconds });
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "文件不是有效的活动数据包。" });
            }
            finally
            {
                try
                {
                    if (scratch is not null && Directory.Exists(scratch))
                    {
                        foreach (var file in Directory.GetFiles(scratch)) File.Delete(file);
                        Directory.Delete(scratch);
                    }
                }
                finally { _importGate.Release(); }
            }
        });
    }

    private static (DateTimeOffset? Start, DateTimeOffset? End, string? Device) ReadExportFilter(HttpContext context)
    {
        DateTimeOffset? Read(string key)
        {
            var value = context.Request.Query[key].ToString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
                throw new ArgumentException("Invalid date");
            return date.ToUniversalTime();
        }
        var start = Read("start"); var end = Read("end");
        if (start.HasValue && end.HasValue && end <= start) throw new ArgumentException("Invalid date range");
        var device = context.Request.Query["device"].ToString();
        return (start, end, string.IsNullOrWhiteSpace(device) ? null : device);
    }
}
