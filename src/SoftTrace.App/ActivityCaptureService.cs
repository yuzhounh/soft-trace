using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SoftTrace.Core;

namespace SoftTrace.App;

public sealed record CaptureStatus(bool IsPaused, bool IsIdle, AppIdentity? CurrentApp);

public sealed class ActivityCaptureService : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(15);

    private readonly ActivityStore _store;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, string> _appNameCache = new(StringComparer.OrdinalIgnoreCase);

    private Task? _pollTask;
    private long? _currentSegmentId;
    private AppIdentity? _currentApp;
    private DateTimeOffset _currentSegmentStartUtc;
    private DateTimeOffset _lastCheckpointUtc;
    private bool _isSystemUnavailable;
    private bool _isIdle;

    public ActivityCaptureService(
        ActivityStore store,
        string deviceId,
        string deviceName,
        int idleThresholdMinutes)
    {
        _store = store;
        _deviceId = deviceId;
        _deviceName = deviceName;
        IdleThresholdMinutes = Math.Clamp(idleThresholdMinutes, 1, 60);
    }

    public event EventHandler<CaptureStatus>? StatusChanged;

    public bool IsPaused { get; private set; }

    public int IdleThresholdMinutes { get; set; }

    public CaptureStatus CurrentStatus =>
        new(IsPaused, _isIdle || _isSystemUnavailable, _currentApp);

    public void Start()
    {
        if (_pollTask is not null)
        {
            return;
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _pollTask = Task.Run(() => PollAsync(_cancellation.Token));
    }

    public async Task SetPausedAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            IsPaused = paused;
            if (paused)
            {
                await CloseCurrentSegmentAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            }
            RaiseStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            await ObserveAsync(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ObserveAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (IsPaused || _isSystemUnavailable)
            {
                await CloseCurrentSegmentAsync(now, cancellationToken);
                return;
            }

            var idleDuration = NativeMethods.GetIdleDuration();
            var isNowIdle = idleDuration >= TimeSpan.FromMinutes(IdleThresholdMinutes);
            if (isNowIdle)
            {
                var idleStartedUtc = now - idleDuration;
                if (_currentSegmentId is not null)
                {
                    await CloseCurrentSegmentAsync(
                        idleStartedUtc < _currentSegmentStartUtc ? _currentSegmentStartUtc : idleStartedUtc,
                        cancellationToken);
                }
                if (!_isIdle)
                {
                    _isIdle = true;
                    RaiseStatus();
                }
                return;
            }

            if (_isIdle)
            {
                _isIdle = false;
                RaiseStatus();
            }

            var observedApp = TryGetForegroundApp();
            if (observedApp is null)
            {
                await CloseCurrentSegmentAsync(now, cancellationToken);
                return;
            }

            if (_currentApp?.StableKey != observedApp.StableKey)
            {
                await CloseCurrentSegmentAsync(now, cancellationToken);
                await StartSegmentAsync(observedApp, now, cancellationToken);
                return;
            }

            if (_currentSegmentId is not null && now - _lastCheckpointUtc >= CheckpointInterval)
            {
                var safeCheckpointUtc = idleDuration > TimeSpan.Zero ? now - idleDuration : now;
                if (safeCheckpointUtc > _currentSegmentStartUtc)
                {
                    await _store.CheckpointSegmentAsync(
                        _currentSegmentId.Value,
                        safeCheckpointUtc,
                        close: false,
                        cancellationToken);
                    _lastCheckpointUtc = now;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartSegmentAsync(
        AppIdentity app,
        DateTimeOffset startUtc,
        CancellationToken cancellationToken)
    {
        _currentSegmentId = await _store.StartSegmentAsync(
            _deviceId,
            _deviceName,
            app,
            startUtc,
            cancellationToken);
        _currentApp = app;
        _currentSegmentStartUtc = startUtc;
        _lastCheckpointUtc = startUtc;
        RaiseStatus();
    }

    private async Task CloseCurrentSegmentAsync(
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        if (_currentSegmentId is null)
        {
            return;
        }

        if (endUtc < _currentSegmentStartUtc)
        {
            endUtc = _currentSegmentStartUtc;
        }

        await _store.CheckpointSegmentAsync(
            _currentSegmentId.Value,
            endUtc,
            close: true,
            cancellationToken);
        _currentSegmentId = null;
        _currentApp = null;
        RaiseStatus();
    }

    private AppIdentity? TryGetForegroundApp()
    {
        try
        {
            var window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            string? executablePath = null;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch
            {
                // Protected and elevated processes may not expose their executable path.
            }

            var cacheKey = executablePath ?? processName;
            if (!_appNameCache.TryGetValue(cacheKey, out var appName))
            {
                appName = ResolveAppName(processName, executablePath);
                _appNameCache[cacheKey] = appName;
            }

            return new AppIdentity(processName, appName, executablePath);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveAppName(string processName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(executablePath);
                var candidate = version.FileDescription ?? version.ProductName;
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate.Trim();
                }
            }
            catch
            {
            }
        }

        return processName;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
        {
            _ = SetSystemUnavailableAsync(true);
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            _ = SetSystemUnavailableAsync(false);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _ = SetSystemUnavailableAsync(true);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _ = SetSystemUnavailableAsync(false);
        }
    }

    private async Task SetSystemUnavailableAsync(bool unavailable)
    {
        await _gate.WaitAsync();
        try
        {
            _isSystemUnavailable = unavailable;
            if (unavailable)
            {
                await CloseCurrentSegmentAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            }
            RaiseStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, CurrentStatus);

    public async ValueTask DisposeAsync()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cancellation.Cancel();
        if (_pollTask is not null)
        {
            await _pollTask;
        }

        await _gate.WaitAsync();
        try
        {
            await CloseCurrentSegmentAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _cancellation.Dispose();
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

        internal static TimeSpan GetIdleDuration()
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var elapsedMilliseconds = unchecked((uint)Environment.TickCount - info.Time);
            return TimeSpan.FromMilliseconds(elapsedMilliseconds);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }
    }
}
