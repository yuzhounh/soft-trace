using Microsoft.Data.Sqlite;
using SoftTrace.Core;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine(
        "Usage: SoftTrace.ManicTimeImport <ManicTime ZIP|directory|ManicTimeReports.db> [softtrace.db]");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var databasePath = args.Length == 2
    ? Path.GetFullPath(args[1])
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftTrace",
        "softtrace.db");

try
{
    if (!File.Exists(databasePath))
    {
        throw new FileNotFoundException("找不到 Soft Trace 数据库。", databasePath);
    }

    var backupPath = Path.Combine(
        Path.GetDirectoryName(databasePath)!,
        $"{Path.GetFileNameWithoutExtension(databasePath)}.pre-manictime-{DateTime.Now:yyyyMMdd-HHmmss}.db");
    await BackupDatabaseAsync(databasePath, backupPath);

    var store = new ActivityStore(databasePath);
    await store.InitializeAsync();
    var device = await store.GetOrCreateDeviceIdentityAsync(Environment.MachineName);
    var result = await store.ImportManicTimeAsync(
        sourcePath,
        device.Id,
        device.Name);

    Console.WriteLine($"Backup: {backupPath}");
    Console.WriteLine($"Read segments: {result.ReadSegmentCount:N0}");
    Console.WriteLine($"Consolidated segments: {result.ConsolidatedSegmentCount:N0}");
    Console.WriteLine($"Imported segments: {result.ImportedSegmentCount:N0}");
    Console.WriteLine($"Imported duration: {result.ImportedDuration}");
    Console.WriteLine($"Covered duration: {result.OverlapDuration}");
    if (result.EarliestUtc is { } earliest && result.LatestUtc is { } latest)
    {
        Console.WriteLine($"Range (local): {earliest.ToLocalTime():O} - {latest.ToLocalTime():O}");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static async Task BackupDatabaseAsync(string databasePath, string backupPath)
{
    var sourceConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString();
    var backupConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = backupPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString();

    await using var source = new SqliteConnection(sourceConnectionString);
    await using var backup = new SqliteConnection(backupConnectionString);
    await source.OpenAsync();
    await backup.OpenAsync();
    source.BackupDatabase(backup);
}
