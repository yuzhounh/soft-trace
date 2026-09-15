using System.IO.Compression;
using Microsoft.Data.Sqlite;
using SoftTrace.Core;

namespace SoftTrace.Core.Tests;

public sealed class ActivityStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"softtrace-tests-{Guid.NewGuid():N}.db");

    private ActivityStore Store => new(_databasePath);

    public async Task InitializeAsync() => await Store.InitializeAsync();

    public Task DisposeAsync()
    {
        SqliteCleanup.DeleteDatabaseFiles(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UsageGroupsSegmentsByApplication()
    {
        var chrome = new AppIdentity("chrome", "Google Chrome", "C:\\Chrome.exe");
        var code = new AppIdentity("Code", "Visual Studio Code", "C:\\Code.exe");
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");

        await Store.AddCompletedSegmentAsync("test-pc", new(chrome, origin, origin.AddMinutes(10)));
        await Store.AddCompletedSegmentAsync("test-pc", new(chrome, origin.AddMinutes(15), origin.AddMinutes(35)));
        await Store.AddCompletedSegmentAsync("test-pc", new(code, origin.AddMinutes(35), origin.AddMinutes(40)));

        var usage = await Store.GetUsageAsync(origin, origin.AddHours(1));

        Assert.Equal(2, usage.Count);
        Assert.Equal("Google Chrome", usage[0].AppName);
        Assert.Equal("C:\\Chrome.exe", usage[0].ExecutablePath);
        AssertClose(TimeSpan.FromMinutes(30), usage[0].Duration);
        Assert.Equal(2, usage[0].SegmentCount);
        AssertClose(TimeSpan.FromMinutes(5), usage[1].Duration);
    }

    [Fact]
    public async Task UsageConsolidatesDisplayNameVariantsWithoutMergingGenericProcesses()
    {
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("chrome", "Chrome", "C:\\Qoom\\Chrome\\chrome.exe"),
                origin,
                origin.AddMinutes(1)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("chrome", "Google Chrome", "C:\\Google\\Chrome\\chrome.exe"),
                origin.AddMinutes(1),
                origin.AddMinutes(11)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("clash-verge", "clash-verge", null),
                origin.AddMinutes(11),
                origin.AddMinutes(12)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("clash-verge", "Clash Verge", "C:\\Clash Verge\\clash-verge.exe"),
                origin.AddMinutes(12),
                origin.AddMinutes(17)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("setup", "Product A", "C:\\A\\setup.exe"),
                origin.AddMinutes(17),
                origin.AddMinutes(18)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("setup", "Product B", "C:\\B\\setup.exe"),
                origin.AddMinutes(18),
                origin.AddMinutes(19)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("ChatGPT", "Codex", "C:\\OpenAI.Codex\\ChatGPT.exe"),
                origin.AddMinutes(19),
                origin.AddMinutes(21)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("HipsMain", "Huorong Internet Security", "C:\\Huorong\\HipsMain.exe"),
                origin.AddMinutes(21),
                origin.AddMinutes(22)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("HipsMain", "火绒安全软件", "C:\\Huorong\\HipsMain.exe"),
                origin.AddMinutes(22),
                origin.AddMinutes(24)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("mmc", "Event Viewer", "C:\\Windows\\System32\\mmc.exe"),
                origin.AddMinutes(24),
                origin.AddMinutes(25)));
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(
                new AppIdentity("mmc", "Computer Management", "C:\\Windows\\System32\\mmc.exe"),
                origin.AddMinutes(25),
                origin.AddMinutes(26)));

        var usage = await Store.GetUsageAsync(origin, origin.AddHours(1));

        Assert.Equal(8, usage.Count);
        var chrome = Assert.Single(usage, row => row.ProcessName == "chrome");
        Assert.Equal("Google Chrome", chrome.AppName);
        AssertClose(TimeSpan.FromMinutes(11), chrome.Duration);
        Assert.Equal("C:\\Google\\Chrome\\chrome.exe", chrome.ExecutablePath);
        var clash = Assert.Single(usage, row => row.ProcessName == "clash-verge");
        Assert.Equal("Clash Verge", clash.AppName);
        AssertClose(TimeSpan.FromMinutes(6), clash.Duration);
        Assert.Equal(2, usage.Count(row => row.ProcessName == "setup"));
        Assert.Equal("Codex", Assert.Single(usage, row => row.ProcessName == "ChatGPT").AppName);
        var huorong = Assert.Single(usage, row => row.ProcessName == "HipsMain");
        Assert.Equal("火绒安全软件", huorong.AppName);
        AssertClose(TimeSpan.FromMinutes(3), huorong.Duration);
        Assert.Equal(2, usage.Count(row => row.ProcessName == "mmc"));
    }

    [Fact]
    public async Task UsageClipsSegmentsAtDateRangeBoundaries()
    {
        var app = new AppIdentity("app", "App", null);
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        await Store.AddCompletedSegmentAsync(
            "test-pc",
            new(app, origin.AddMinutes(-10), origin.AddMinutes(10)));

        var usage = await Store.GetUsageAsync(origin, origin.AddMinutes(5));

        var row = Assert.Single(usage);
        AssertClose(TimeSpan.FromMinutes(5), row.Duration);
    }

    [Fact]
    public async Task RecoveryClosesOpenSegmentsAtTheirLastCheckpoint()
    {
        var app = new AppIdentity("app", "App", null);
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        var id = await Store.StartSegmentAsync("test-pc", app, origin);
        await Store.CheckpointSegmentAsync(id, origin.AddSeconds(15), close: false);

        await Store.RecoverInterruptedSegmentsAsync();
        var usage = await Store.GetUsageAsync(origin, origin.AddMinutes(1));

        AssertClose(TimeSpan.FromSeconds(15), Assert.Single(usage).Duration);
    }

    [Fact]
    public async Task RemoteActivitiesAreIdempotentAndNewerUpdatesWin()
    {
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        var initial = new SyncActivity(
            "shared-segment",
            "remote-pc",
            "Remote PC",
            "chrome",
            "Google Chrome",
            "C:\\Chrome.exe",
            origin,
            origin.AddMinutes(5),
            origin.AddMinutes(5),
            false,
            "local");

        Assert.Equal(1, await Store.MergeRemoteActivitiesAsync([initial]));
        Assert.Equal(0, await Store.MergeRemoteActivitiesAsync([initial]));

        var newer = initial with
        {
            EndUtc = origin.AddMinutes(8),
            UpdatedUtc = origin.AddMinutes(8)
        };
        Assert.Equal(1, await Store.MergeRemoteActivitiesAsync([newer]));

        var usage = await Store.GetUsageAsync(origin, origin.AddHours(1));
        AssertClose(TimeSpan.FromMinutes(8), Assert.Single(usage).Duration);
        Assert.Empty(await Store.GetPendingSyncActivitiesAsync());
    }

    [Fact]
    public async Task SyncPullCursorRoundTripsTimestampAndDocumentName()
    {
        var cursor = new SyncPullCursor(
            DateTimeOffset.Parse("2026-09-14T12:34:56.789Z"),
            "projects/soft-trace/databases/(default)/documents/users/u/activities/a");

        await Store.SetLastSyncPullCursorAsync(cursor);
        var restored = await Store.GetLastSyncPullCursorAsync();

        Assert.Equal(cursor, restored);
    }

    [Fact]
    public async Task UiSettingPersistsAcrossStoreInstances()
    {
        const string widths = "420;260;130;95";

        await Store.SetSettingAsync("usage_column_widths", widths);
        var restored = await new ActivityStore(_databasePath)
            .GetSettingAsync("usage_column_widths");

        Assert.Equal(widths, restored);
    }

    [Fact]
    public async Task DeviceDisplayNameIsAnAliasAndDoesNotRewriteActivities()
    {
        var identity = await Store.GetOrCreateDeviceIdentityAsync("Original PC");
        var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        var app = new AppIdentity("app", "App", "C:\\App.exe");
        var segmentId = await Store.StartSegmentAsync(
            identity.Id,
            identity.Name,
            app,
            origin);
        await Store.CheckpointSegmentAsync(segmentId, origin.AddMinutes(1), close: true);
        var before = Assert.Single(await Store.GetPendingSyncActivitiesAsync());

        await Store.SetDeviceDisplayNameAsync(identity.Id, "Studio PC");

        var device = Assert.Single(await Store.GetDevicesAsync());
        var after = Assert.Single(await Store.GetPendingSyncActivitiesAsync());
        Assert.Equal(identity.Id, device.Id);
        Assert.Equal("Studio PC", device.Name);
        Assert.Equal("Original PC", after.DeviceName);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
    }

    [Fact]
    public async Task ManicTimeImportConsolidatesTitlesAndOnlyFillsUncoveredTime()
    {
        var sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"manictime-reports-{Guid.NewGuid():N}.db");
        var archivePath = sourcePath + ".zip";
        try
        {
            await CreateManicTimeReportsDatabaseAsync(sourcePath);
            var origin = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
            await Store.AddCompletedSegmentAsync(
                "test-pc",
                new(
                    new AppIdentity("softtrace", "SoftTrace", "C:\\SoftTrace.exe"),
                    origin.AddMinutes(5),
                    origin.AddMinutes(15)));

            var result = await Store.ImportManicTimeAsync(
                sourcePath,
                "test-pc",
                "Test PC");

            Assert.Equal(2, result.ReadSegmentCount);
            Assert.Equal(1, result.ConsolidatedSegmentCount);
            Assert.Equal(2, result.ImportedSegmentCount);
            AssertClose(TimeSpan.FromMinutes(20), result.SourceDuration);
            AssertClose(TimeSpan.FromMinutes(10), result.ImportedDuration);
            AssertClose(TimeSpan.FromMinutes(10), result.OverlapDuration);

            var usage = await Store.GetUsageAsync(origin, origin.AddMinutes(20));
            Assert.Equal(2, usage.Count);
            Assert.All(usage, row => AssertClose(TimeSpan.FromMinutes(10), row.Duration));
            Assert.Contains(usage, row =>
                row.AppName == "Example App" &&
                row.ProcessName == "example" &&
                row.ExecutablePath == "C:\\Example\\example.exe");

            var repeated = await Store.ImportManicTimeAsync(
                sourcePath,
                "test-pc",
                "Test PC");
            Assert.Equal(0, repeated.ImportedSegmentCount);
            AssertClose(TimeSpan.FromMinutes(20), repeated.OverlapDuration);

            var repeatedUsage = await Store.GetUsageAsync(origin, origin.AddMinutes(20));
            Assert.Equal(usage.Count, repeatedUsage.Count);
            Assert.Equal(
                usage.Sum(row => row.Duration.TotalSeconds),
                repeatedUsage.Sum(row => row.Duration.TotalSeconds));

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(sourcePath, "backup/ManicTimeReports.db");
            }
            var zipped = await Store.ImportManicTimeAsync(
                archivePath,
                "test-pc",
                "Test PC");
            Assert.Equal(2, zipped.ReadSegmentCount);
            Assert.Equal(0, zipped.ImportedSegmentCount);
        }
        finally
        {
            SqliteCleanup.DeleteDatabaseFiles(sourcePath);
            File.Delete(archivePath);
        }
    }

    private static async Task CreateManicTimeReportsDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Ar_Timeline (
                ReportId INTEGER NOT NULL,
                TimelineKey TEXT NOT NULL,
                SchemaName TEXT NOT NULL
            );
            CREATE TABLE Ar_Group (
                ReportId INTEGER NOT NULL,
                GroupId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                [Key] TEXT NOT NULL,
                Other TEXT NOT NULL
            );
            CREATE TABLE Ar_Activity (
                ReportId INTEGER NOT NULL,
                ActivityId INTEGER NOT NULL,
                GroupId INTEGER NOT NULL,
                StartUtcTime TEXT NOT NULL,
                EndUtcTime TEXT NOT NULL
            );
            INSERT INTO Ar_Timeline VALUES (
                3,
                'example-timeline',
                'ManicTime/Applications'
            );
            INSERT INTO Ar_Group VALUES (
                3,
                7,
                'Example App',
                'example.exe;example app',
                '{"fileName":"example.exe","fullPath":"C:\\Example\\example.exe","name":"Example App"}'
            );
            INSERT INTO Ar_Activity VALUES (
                3, 100, 7, '2026-09-14 00:00:00', '2026-09-14 00:10:00'
            );
            INSERT INTO Ar_Activity VALUES (
                3, 101, 7, '2026-09-14 00:10:00', '2026-09-14 00:20:00'
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertClose(TimeSpan expected, TimeSpan actual) =>
        Assert.InRange(Math.Abs((expected - actual).TotalMilliseconds), 0, 20);

    private static class SqliteCleanup
    {
        public static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
