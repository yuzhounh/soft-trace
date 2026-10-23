using System.IO;
using System.Net;
using System.Net.Http;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class WebResourceReloadTests
{
    [Fact]
    public async Task RequestsReadChangedAndNewAssetsWithoutRestartingServerOrExposingOtherFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"web-reload-{Guid.NewGuid():N}");
        var web = Path.Combine(root, "Web");
        Directory.CreateDirectory(web);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<h1>First interface</h1>");
            await File.WriteAllTextAsync(Path.Combine(web, "dashboard.css"), "body{color:red}");
            await File.WriteAllTextAsync(Path.Combine(root, "private.js"), "must not be served");
            var store = new ActivityStore(Path.Combine(root, "test.db"));
            await store.InitializeAsync();
            await using var capture = new ActivityCaptureService(store, "test", "Test", 3);
            await using var server = new LocalDashboardServer(store, capture,
                () => new(false, false, null, "Offline", null, false),
                () => Task.CompletedTask, _ => { }, webDirectory: web);
            await server.StartAsync();
            var originalAddress = server.Address;
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(originalAddress) };
            Assert.Contains("First interface", await http.GetStringAsync("/"));
            Assert.Contains("red", await http.GetStringAsync("/dashboard.css"));
            await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<h1>Updated interface</h1>");
            await File.WriteAllTextAsync(Path.Combine(web, "dashboard.css"), "body{color:green}");
            var refreshed = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            Assert.True(refreshed.Headers.CacheControl!.NoStore);
            Assert.Contains("Updated interface", await refreshed.Content.ReadAsStringAsync());
            Assert.Contains("green", await http.GetStringAsync("/dashboard.css"));
            Directory.CreateDirectory(Path.Combine(web, "components"));
            await File.WriteAllTextAsync(Path.Combine(web, "components", "new.js"), "const newlyAdded = true;");
            Assert.Contains("newlyAdded", await http.GetStringAsync("/components/new.js"));
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/components/../../private.js")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/test.db")).StatusCode);
            Assert.Equal(originalAddress, server.Address);
        }
        finally
        {
            // This test creates the entire directory; no user files are involved.
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
