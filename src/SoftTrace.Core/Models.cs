namespace SoftTrace.Core;

public sealed record AppIdentity(string ProcessName, string AppName, string? ExecutablePath)
{
    public string StableKey => string.IsNullOrWhiteSpace(ExecutablePath)
        ? ProcessName.ToUpperInvariant()
        : ExecutablePath.ToUpperInvariant();
}

public sealed record UsageSummary(
    string AppName,
    string ProcessName,
    string? ExecutablePath,
    TimeSpan Duration,
    int SegmentCount);

public sealed record ActivitySegment(
    AppIdentity App,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

public sealed record DeviceIdentity(string Id, string Name);

public sealed record DeviceSummary(string Id, string Name);

public sealed record SyncActivity(
    string SyncId,
    string DeviceId,
    string DeviceName,
    string ProcessName,
    string AppName,
    string? ExecutablePath,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset UpdatedUtc,
    bool IsOpen,
    string Source,
    DateTimeOffset? ServerUpdatedUtc = null);

public sealed record SyncMarker(string SyncId, DateTimeOffset UpdatedUtc);

public sealed record SyncPullCursor(DateTimeOffset ServerUpdatedUtc, string DocumentName);
