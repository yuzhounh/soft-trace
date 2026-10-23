using SoftTrace.Core;
using System.Net.NetworkInformation;

namespace SoftTrace.App;

public sealed record CloudSyncStatus(
    bool IsSignedIn,
    bool IsSyncing,
    string? Email,
    string Message,
    DateTimeOffset? LastSyncUtc,
    bool HasError,
    string? DisplayName = null,
    string? PhotoUrl = null,
    bool RequiresReauthentication = false);

public sealed class FirebaseSyncCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);
    private readonly ActivityStore _store;
    private readonly FirebaseSyncSettingsStore _settingsStore;
    private readonly FirebaseAuthClient _authClient;
    private readonly FirestoreSyncClient _firestoreClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CloudSyncRetrySchedule _retrySchedule;
    private readonly CancellationTokenSource _cancellation = new();

    private FirebaseSyncSettings _settings;
    private FirebaseAuthSession? _session;
    private Task? _loopTask;
    private CloudSyncStatus _currentStatus;

    public FirebaseSyncCoordinator(
        ActivityStore store,
        FirebaseSyncSettingsStore settingsStore,
        FirebaseAuthClient authClient,
        FirestoreSyncClient firestoreClient,
        CloudSyncRetrySchedule? retrySchedule = null)
    {
        _store = store;
        _settingsStore = settingsStore;
        _authClient = authClient;
        _firestoreClient = firestoreClient;
        _retrySchedule = retrySchedule ?? new CloudSyncRetrySchedule();
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

    public string? CurrentUserId => _settings.IsConfigured ? _settings.UserId : null;

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _loopTask = Task.Run(() => RunLoopAsync(_cancellation.Token));
    }

    public async Task SignInWithGoogleAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var requiresReauthentication = _currentStatus.RequiresReauthentication;
        try
        {
            SetStatus(new CloudSyncStatus(_settings.IsConfigured, true, _settings.Email, "正在打开 Google 登录…", null, false));
            var session = await _authClient.SignInWithGoogleAsync(
                FirebaseConfiguration.ApiKey,
                FirebaseConfiguration.GoogleDesktopClientId,
                FirebaseConfiguration.GetGoogleDesktopClientSecret(),
                cancellationToken);
            if (_settings.IsConfigured && !string.Equals(_settings.UserId, session.UserId, StringComparison.Ordinal))
                throw new InvalidOperationException($"请选择原 Google 账号（{_settings.Email}）。原账号、记录和设置均已保留。");
            var settings = new FirebaseSyncSettings(
                true,
                session.Email,
                session.DisplayName,
                session.PhotoUrl,
                session.UserId,
                FirebaseSyncSettingsStore.ProtectRefreshToken(session.RefreshToken));
            _settingsStore.Save(settings);
            _settings = settings;
            _session = session;
            _retrySchedule.RecordSuccess();
            SetStatus(new CloudSyncStatus(
                true,
                false,
                _settings.Email,
                "登录成功，等待同步",
                null,
                false));
        }
        catch (Exception exception)
        {
            SetStatus(new CloudSyncStatus(
                _settings.IsConfigured,
                false,
                _settings.Email,
                exception is OperationCanceledException ? "已取消 Google 授权，原账号与本地数据保留。" : exception.Message,
                null,
                true,
                RequiresReauthentication: requiresReauthentication));
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
            _retrySchedule.RecordSuccess();
            _settingsStore.Save(_settings);
            SetStatus(new CloudSyncStatus(false, false, null, "尚未登录", null, false));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default) =>
        SyncCoreAsync(false, cancellationToken);

    private async Task SyncCoreAsync(bool automatic, CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured)
        {
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            // Recheck after waiting: a manual sync or sign-out may have completed first.
            if (!_settings.IsConfigured || (automatic &&
                (_currentStatus.RequiresReauthentication || _retrySchedule.Delay > TimeSpan.Zero))) return;
            SetStatus(new CloudSyncStatus(true, true, _settings.Email, "正在同步…", null, false));
            var session = await EnsureSessionAsync(cancellationToken);
            var pushed = await PushPendingAsync(session, cancellationToken);
            var pulled = await PullRemoteAsync(session, cancellationToken);
            var deviceNames = await SyncDeviceDisplayNamesAsync(session, cancellationToken);
            var completedUtc = DateTimeOffset.UtcNow;
            var detail = pushed == 0 && pulled == 0
                ? "云端已是最新"
                : $"已同步：上传 {pushed}，更新 {pulled}";
            if (deviceNames > 0) detail += $"；设备名称 {deviceNames}";
            _retrySchedule.RecordSuccess();
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
        catch (FirebaseReauthorizationRequiredException exception)
        {
            SetStatus(new CloudSyncStatus(true, false, _settings.Email, exception.Message, null, true,
                RequiresReauthentication: true));
        }
        catch (Exception exception)
        {
            _retrySchedule.RecordFailure();
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

    private async Task<int> SyncDeviceDisplayNamesAsync(
        FirebaseAuthSession session, CancellationToken cancellationToken)
    {
        // Claim signed-out edits before merging cloud names. Explicit pending edits stay local until uploaded.
        await _store.GetPendingDeviceDisplayNamesAsync(session.UserId, cancellationToken);
        var remote = await _firestoreClient.PullDeviceDisplayNamesAsync(
            FirebaseConfiguration.ProjectId, session.UserId, session.IdToken, cancellationToken);
        var changed = await _store.MergeRemoteDeviceDisplayNamesAsync(session.UserId, remote, cancellationToken);
        var pending = await _store.GetPendingDeviceDisplayNamesAsync(session.UserId, cancellationToken);
        foreach (var batch in pending.Chunk(200))
        {
            await _firestoreClient.PushDeviceDisplayNamesAsync(
                FirebaseConfiguration.ProjectId, session.UserId, session.IdToken, batch, cancellationToken);
            await _store.MarkDeviceDisplayNamesSyncedAsync(session.UserId, batch, cancellationToken);
            changed += batch.Length;
        }
        if (pending.Count > 0)
        {
            remote = await _firestoreClient.PullDeviceDisplayNamesAsync(
                FirebaseConfiguration.ProjectId, session.UserId, session.IdToken, cancellationToken);
            changed += await _store.MergeRemoteDeviceDisplayNamesAsync(session.UserId, remote, cancellationToken);
        }
        return changed;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = _settings.IsConfigured && !_currentStatus.RequiresReauthentication
                    ? _retrySchedule.Delay : SyncInterval;
                await _wakeSignal.WaitAsync(delay, cancellationToken);
                if (_currentStatus.RequiresReauthentication) continue;
                await SyncCoreAsync(true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        if (!args.IsAvailable || _cancellation.IsCancellationRequested || !_settings.IsConfigured ||
            _currentStatus.RequiresReauthentication) return;
        _retrySchedule.RequestNow();
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
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
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _cancellation.Cancel();
        if (_loopTask is not null)
        {
            await _loopTask;
        }
        _operationLock.Dispose();
        _wakeSignal.Dispose();
        _cancellation.Dispose();
    }
}
