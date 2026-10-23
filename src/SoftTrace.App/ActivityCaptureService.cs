using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SoftTrace.Core;

namespace SoftTrace.App;

public sealed record CaptureStatus(bool IsPaused, bool IsIdle, AppIdentity? CurrentApp, string? Error = null);

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
    private Process? _foregroundProcess;
    private uint _foregroundProcessId;
    private string? _foregroundProcessName;
    private string? _foregroundExecutablePath;
    private string? _foregroundAppName;
    private readonly Func<TimeSpan> _readIdleDuration;
    private readonly Func<AppIdentity?> _readForegroundApp;

    private Task? _pollTask;
    private long? _currentSegmentId;
    private AppIdentity? _currentApp;
    private DateTimeOffset _currentSegmentStartUtc;
    private DateTimeOffset _lastCheckpointUtc;
    private DateTimeOffset? _activeRunStartUtc;
    private bool _isSystemUnavailable;
    private bool _isIdle;
    private string? _captureError;
    private long? _stateSegmentId;
    private string? _stateKind;
    private DateTimeOffset _stateCheckpointUtc;
    private DateTimeOffset? _faultStartedUtc;
    private string _systemState = "locked";
    private bool _sessionUnavailable;
    private bool _suspended;
    private string _sessionState = "locked";

    public ActivityCaptureService(
        ActivityStore store,
        string deviceId,
        string deviceName,
        int idleThresholdMinutes,
        Func<TimeSpan>? readIdleDuration = null,
        Func<AppIdentity?>? readForegroundApp = null)
    {
        _store = store;
        _deviceId = deviceId;
        _deviceName = deviceName;
        IdleThresholdMinutes = Math.Clamp(idleThresholdMinutes, 1, 60);
        _readIdleDuration = readIdleDuration ?? NativeMethods.GetIdleDuration;
        _readForegroundApp = readForegroundApp ?? TryGetForegroundApp;
    }

    public event EventHandler<CaptureStatus>? StatusChanged;
    public event EventHandler<Exception>? CaptureFailed;

    public bool IsPaused { get; private set; }

    public string DeviceId => _deviceId;

    public string DeviceName => _deviceName;

    public int IdleThresholdMinutes { get; set; }

    public CaptureStatus CurrentStatus =>
        new(IsPaused, _isIdle || _isSystemUnavailable, _currentApp, _captureError);

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
            if (!paused && _captureError is not null)
            {
                // Close at the last persisted checkpoint, without extending through the unobserved gap.
                await _store.RecoverInterruptedSegmentsAsync(_deviceId);
                if (_faultStartedUtc is { } faultStart)
                {
                    var id = await _store.StartSegmentAsync(_deviceId, _deviceName,
                        new AppIdentity("__softtrace_state__", "采集故障", null, Kind: "fault"), faultStart);
                    await _store.CheckpointSegmentAsync(id, DateTimeOffset.UtcNow, close: true);
                }
                _faultStartedUtc = null;
                _captureError = null;
            }
            IsPaused = paused;
            if (paused)
            {
                await CloseCurrentSegmentAsync(DateTimeOffset.UtcNow, CancellationToken.None);
                await SetStateAsync(_isSystemUnavailable ? _systemState : "paused", DateTimeOffset.UtcNow, CancellationToken.None);
            }
            else await SetStateAsync(_isSystemUnavailable ? _systemState : null, DateTimeOffset.UtcNow, CancellationToken.None);
            RaiseStatus();
        }
        catch (Exception exception)
        {
            SetCaptureFault(exception);
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
            await ObserveSafelyAsync(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ObserveSafelyAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ObserveAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                SetCaptureFault(exception);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void SetCaptureFault(Exception exception)
    {
        IsPaused = true;
        _captureError = exception.Message;
        _faultStartedUtc ??= DateTimeOffset.UtcNow;
        // Leave the stored segment at its last successful checkpoint for recovery.
        _currentSegmentId = null;
        _currentApp = null;
        _stateSegmentId = null;
        _stateKind = null;
        _activeRunStartUtc = null;
        RaiseStatus();
        CaptureFailed?.Invoke(this, exception);
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
                if (_captureError is null)
                    await SetStateAsync(_isSystemUnavailable ? _systemState : "paused", now, cancellationToken);
                return;
            }

            var idleDuration = _readIdleDuration();
            var isNowIdle = idleDuration >= TimeSpan.FromMinutes(IdleThresholdMinutes);
            if (isNowIdle)
            {
                var idleStartedUtc = now - idleDuration;
                var activeRunStart = _activeRunStartUtc ?? _currentSegmentStartUtc;
                if (_currentSegmentId is not null && idleStartedUtc < activeRunStart)
                    idleStartedUtc = activeRunStart;
                if (_stateKind != "idle" && _currentSegmentId is null) idleStartedUtc = now;
                if (_currentSegmentId is not null)
                {
                    await CloseCurrentSegmentAsync(
                        idleStartedUtc < _currentSegmentStartUtc ? _currentSegmentStartUtc : idleStartedUtc,
                        cancellationToken);
                    await _store.TrimIdleTailAsync(_deviceId, activeRunStart, idleStartedUtc, cancellationToken);
                }
                await SetStateAsync("idle", _stateKind == "idle" ? now : idleStartedUtc, cancellationToken);
                await SetStateAsync("idle", now, cancellationToken);
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

            var observedApp = _readForegroundApp();
            if (observedApp is null)
            {
                await CloseCurrentSegmentAsync(now, cancellationToken);
                await SetStateAsync("unavailable", now, cancellationToken);
                return;
            }

            await SetStateAsync(null, now, cancellationToken);
            if (_currentApp?.StableKey != observedApp.StableKey ||
                _currentApp?.WindowTitle != observedApp.WindowTitle)
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
        _activeRunStartUtc ??= startUtc;
        _currentSegmentStartUtc = startUtc;
        _lastCheckpointUtc = startUtc;
        RaiseStatus();
    }

    private async Task SetStateAsync(string? kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_stateKind != kind)
        {
            if (_stateSegmentId is { } previous)
                await _store.CheckpointSegmentAsync(previous, now, close: true, cancellationToken);
            _stateSegmentId = null;
            _stateKind = null;
            if (kind is not null)
            {
                _activeRunStartUtc = null;
                var label = kind switch
                {
                    "idle" => "空闲", "locked" => "已锁屏", "sleep" => "睡眠",
                    "disconnected" => "会话已断开", "paused" => "暂停记录", _ => "无法识别前台窗口"
                };
                _stateSegmentId = await _store.StartSegmentAsync(_deviceId, _deviceName,
                    new AppIdentity("__softtrace_state__", label, null, Kind: kind), now, cancellationToken);
                _stateKind = kind;
                _stateCheckpointUtc = now;
            }
        }
        if (_stateSegmentId is { } id && now - _stateCheckpointUtc >= CheckpointInterval)
        {
            await _store.CheckpointSegmentAsync(id, now, close: false, cancellationToken);
            _stateCheckpointUtc = now;
        }
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

            // The process name and image path are stable for a live process, and reading them
            // (MainModule enumerates modules) is the costly part of the one-second poll.
            // Keep the handle of the last foreground process: HasExited is handle-based, so a
            // reused PID cannot be mistaken for the cached process.
            if (_foregroundProcess is null || _foregroundProcessId != processId || _foregroundProcess.HasExited)
            {
                _foregroundProcess?.Dispose();
                _foregroundProcess = null;
                _foregroundProcessId = 0;
                var process = Process.GetProcessById((int)processId);
                try
                {
                    var name = process.ProcessName;
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Protected and elevated processes may not expose their executable path.
                    }

                    var key = path ?? name;
                    if (!_appNameCache.TryGetValue(key, out var resolvedName))
                    {
                        resolvedName = ResolveAppName(name, path);
                        _appNameCache[key] = resolvedName;
                    }

                    _foregroundProcess = process;
                    _foregroundProcessId = processId;
                    _foregroundProcessName = name;
                    _foregroundExecutablePath = path;
                    _foregroundAppName = resolvedName;
                }
                catch
                {
                    process.Dispose();
                    throw;
                }
            }

            var title = new StringBuilder(2048);
            NativeMethods.GetWindowText(window, title, title.Capacity);
            return new AppIdentity(_foregroundProcessName!, _foregroundAppName!, _foregroundExecutablePath, title.ToString());
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
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
        {
            _ = SetSystemUnavailableAsync(true, e.Reason == SessionSwitchReason.SessionLock ? "locked" : "disconnected");
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
        {
            _ = SetSystemUnavailableAsync(false);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _ = SetSystemUnavailableAsync(true, "sleep");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _ = SetSystemUnavailableAsync(false, "sleep");
        }
    }

    private async Task SetSystemUnavailableAsync(bool unavailable, string kind = "locked")
    {
        await _gate.WaitAsync();
        try
        {
            if (kind == "sleep") _suspended = unavailable;
            else { _sessionUnavailable = unavailable; _sessionState = kind; }
            _isSystemUnavailable = _sessionUnavailable || _suspended;
            _systemState = _suspended ? "sleep" : _sessionState;
            if (_captureError is not null) { RaiseStatus(); return; }
            var now = DateTimeOffset.UtcNow;
            if (_isSystemUnavailable)
            {
                await CloseCurrentSegmentAsync(now, CancellationToken.None);
                await SetStateAsync(_systemState, now, CancellationToken.None);
            }
            else await SetStateAsync(IsPaused ? "paused" : null, now, CancellationToken.None);
            RaiseStatus();
        }
        catch (Exception exception)
        {
            SetCaptureFault(exception);
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
            await SetStateAsync(null, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetCaptureFault(exception);
        }
        finally
        {
            _foregroundProcess?.Dispose();
            _foregroundProcess = null;
            _gate.Release();
            _gate.Dispose();
            _cancellation.Dispose();
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

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
