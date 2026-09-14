using SoftTrace.Core;

namespace SoftTrace.App;

public sealed record CloudSyncStatus(
    bool IsSignedIn,
    bool IsSyncing,
    string? Email,
    string Message,
    DateTimeOffset? LastSyncUtc,
    bool HasError,
    string? DisplayName = null,
    string? PhotoUrl = null);

public sealed class FirebaseSyncCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);
    private readonly ActivityStore _store;
    private readonly FirebaseSyncSettingsStore _settingsStore;
    private readonly FirebaseAuthClient _authClient;
    private readonly FirestoreSyncClient _firestoreClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();

    private FirebaseSyncSettings _settings;
    private FirebaseAuthSession? _session;
    private Task? _loopTask;
    private CloudSyncStatus _currentStatus;

    public FirebaseSyncCoordinator(
        ActivityStore store,
        FirebaseSyncSettingsStore settingsStore,
        FirebaseAuthClient authClient,
        FirestoreSyncClient firestoreClient)
    {
        _store = store;
        _settingsStore = settingsStore;
        _authClient = authClient;
        _firestoreClient = firestoreClient;
        _settings = settingsStore.Load();
        _currentStatus = _settings.IsConfigured
            ? new CloudSyncStatus(
                true,
                false,
                _settings.Email,
                "等待自动同步",
                null,
                false,
                _settings.DisplayName,
                _settings.PhotoUrl)
            : new CloudSyncStatus(false, false, null, "尚未登录", null, false);
    }

    public event EventHandler<CloudSyncStatus>? StatusChanged;

    public CloudSyncStatus CurrentStatus => _currentStatus;

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = Task.Run(() => RunLoopAsync(_cancellation.Token));
        if (_settings.IsConfigured)
        {
            _ = Task.Run(() => SyncNowAsync(_cancellation.Token));
        }
    }

    public async Task SignInWithGoogleAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            SetStatus(new CloudSyncStatus(false, true, null, "正在打开 Google 登录…", null, false));
            _session = await _authClient.SignInWithGoogleAsync(
                FirebaseConfiguration.ApiKey,
                FirebaseConfiguration.GoogleDesktopClientId,
                FirebaseConfiguration.GetGoogleDesktopClientSecret(),
                cancellationToken);
            _settings = new FirebaseSyncSettings(
                true,
                _session.Email,
                _session.DisplayName,
                _session.PhotoUrl,
                _session.UserId,
                FirebaseSyncSettingsStore.ProtectRefreshToken(_session.RefreshToken));
            _settingsStore.Save(_settings);
            SetStatus(new CloudSyncStatus(
                true,
                false,
                _settings.Email,
                "登录成功，等待同步",
                null,
                false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus(new CloudSyncStatus(
                false,
                false,
                null,
                exception.Message,
                null,
                true));
            throw;
        }
        finally
        {
            _operationLock.Release();
        }

        await SyncNowAsync(cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _session = null;
            _settings = FirebaseSyncSettings.Empty;
            _settingsStore.Save(_settings);
            SetStatus(new CloudSyncStatus(false, false, null, "尚未登录", null, false));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            SetStatus(new CloudSyncStatus(true, true, _settings.Email, "正在同步…", null, false));
            var session = await EnsureSessionAsync(cancellationToken);
            var pushed = await PushPendingAsync(session, cancellationToken);
            var pulled = await PullRemoteAsync(session, cancellationToken);
            var completedUtc = DateTimeOffset.UtcNow;
            var detail = pushed == 0 && pulled == 0
                ? "云端已是最新"
                : $"已同步：上传 {pushed}，更新 {pulled}";
            SetStatus(new CloudSyncStatus(
                true,
                false,
                _settings.Email,
                detail,
                completedUtc,
                false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus(new CloudSyncStatus(
                true,
                false,
                _settings.Email,
                exception.Message,
                null,
                true));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<FirebaseAuthSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is not null && _session.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _session;
        }

        if (!_settings.IsConfigured)
        {
            throw new InvalidOperationException("请先 Google 登录。");
        }

        var refreshToken = FirebaseSyncSettingsStore.UnprotectRefreshToken(
            _settings.ProtectedRefreshToken!);
        var refreshed = await _authClient.RefreshAsync(
            FirebaseConfiguration.ApiKey,
            refreshToken,
            cancellationToken);
        _session = refreshed with
        {
            Email = refreshed.Email ?? _settings.Email,
            DisplayName = refreshed.DisplayName ?? _settings.DisplayName,
            PhotoUrl = refreshed.PhotoUrl ?? _settings.PhotoUrl
        };
        _settings = _settings with
        {
            Email = _session.Email,
            DisplayName = _session.DisplayName,
            PhotoUrl = _session.PhotoUrl,
            UserId = refreshed.UserId,
            ProtectedRefreshToken = FirebaseSyncSettingsStore.ProtectRefreshToken(refreshed.RefreshToken)
        };
        _settingsStore.Save(_settings);
        return _session;
    }

    private async Task<int> PushPendingAsync(
        FirebaseAuthSession session,
        CancellationToken cancellationToken)
    {
        var pushed = 0;
        for (var batchNumber = 0; batchNumber < 20; batchNumber++)
        {
            var activities = await _store.GetPendingSyncActivitiesAsync(200, cancellationToken);
            if (activities.Count == 0)
            {
                break;
            }

            await _firestoreClient.PushActivitiesAsync(
                FirebaseConfiguration.ProjectId,
                session.UserId,
                session.IdToken,
                activities,
                cancellationToken);
            await _store.MarkActivitiesSyncedAsync(
                activities.Select(activity => new SyncMarker(activity.SyncId, activity.UpdatedUtc)).ToArray(),
                cancellationToken);
            pushed += activities.Count;
            if (activities.Count < 200)
            {
                break;
            }
        }
        return pushed;
    }

    private async Task<int> PullRemoteAsync(
        FirebaseAuthSession session,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        var cursor = await _store.GetLastSyncPullCursorAsync(cancellationToken);
        for (var pageNumber = 0; pageNumber < 20; pageNumber++)
        {
            var page = await _firestoreClient.PullActivitiesAsync(
                FirebaseConfiguration.ProjectId,
                session.UserId,
                session.IdToken,
                cursor,
                cancellationToken: cancellationToken);
            changed += await _store.MergeRemoteActivitiesAsync(page.Activities, cancellationToken);
            if (page.NextCursor is not null)
            {
                cursor = page.NextCursor;
                await _store.SetLastSyncPullCursorAsync(cursor, cancellationToken);
            }
            if (!page.HasMore || page.NextCursor is null)
            {
                break;
            }
        }
        return changed;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SyncInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SyncNowAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void SetStatus(CloudSyncStatus status)
    {
        if (status.IsSignedIn)
        {
            status = status with
            {
                Email = status.Email ?? _settings.Email,
                DisplayName = status.DisplayName ?? _settings.DisplayName,
                PhotoUrl = status.PhotoUrl ?? _settings.PhotoUrl
            };
        }
        _currentStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_loopTask is not null)
        {
            await _loopTask;
        }
        _operationLock.Dispose();
        _cancellation.Dispose();
    }
}
