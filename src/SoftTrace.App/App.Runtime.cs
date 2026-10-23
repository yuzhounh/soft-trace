using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using SoftTrace.Core;
using Forms = System.Windows.Forms;

namespace SoftTrace.App;

public partial class App
{
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowHandle;
    private RegisteredWaitHandle? _registeredWaitHandle;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;
    private ActivityCaptureService? _capture;
    private FirebaseSyncCoordinator? _syncCoordinator;
    private HttpClient? _httpClient;
    private LocalDashboardServer? _dashboard;
    private string? _logPath;
    private bool _trayMenuOpening;

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log($"Unhandled AppDomain exception: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
            Log($"Unhandled Dispatcher exception: {args.Exception}");

        var startInBackground = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);

        _singleInstanceMutex = new Mutex(true, "SoftTrace.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting("SoftTrace.ShowWindowEvent");
                showEvent.Set();
            }
            catch
            {
            }
            Shutdown();
            return;
        }

        try
        {
            _showWindowHandle = new EventWaitHandle(false, EventResetMode.AutoReset, "SoftTrace.ShowWindowEvent");
            _registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                _showWindowHandle,
                (_, _) => Dispatcher.BeginInvoke(ShowMainWindow),
                null,
                -1,
                false);

            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftTrace");
            Directory.CreateDirectory(dataDirectory);
            MigrateLegacyData(dataDirectory);
            _logPath = Path.Combine(dataDirectory, "softtrace.log");
            Log($"Starting SoftTrace. Version={typeof(App).Assembly.GetName().Version}; Executable={Environment.ProcessPath}; PID={Environment.ProcessId}");
            var store = new ActivityStore(Path.Combine(dataDirectory, "softtrace.db"));
            await store.InitializeAsync();
            var device = await store.GetOrCreateDeviceIdentityAsync(Environment.MachineName);
            await store.RecoverInterruptedSegmentsAsync(device.Id);
            Log("Database initialized.");

            var idleMinutes = await store.GetIdleThresholdMinutesAsync();
            _capture = new ActivityCaptureService(store, device.Id, device.Name, idleMinutes);
            _capture.CaptureFailed += (_, exception) => Log($"Capture failed: {exception}");
            _capture.StatusChanged += (_, status) =>
                Dispatcher.BeginInvoke(() => UpdateTrayCaptureStatus(status));
            _capture.Start();
            Log("Capture service started.");

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var syncSettings = new FirebaseSyncSettingsStore(
                Path.Combine(dataDirectory, "firebase-sync.json"));
            _syncCoordinator = new FirebaseSyncCoordinator(
                store,
                syncSettings,
                new FirebaseAuthClient(_httpClient),
                new FirestoreSyncClient(_httpClient));
            _syncCoordinator.StatusChanged += (_, status) =>
                Log($"Cloud sync: {status.Message}");
            _syncCoordinator.Start();

            _dashboard = new LocalDashboardServer(store, _capture,
                () => _syncCoordinator.CurrentStatus, () => _syncCoordinator.SyncNowAsync(),
                exception => Log($"Local dashboard: {exception}"),
                new DashboardActions(() => _syncCoordinator.SignInWithGoogleAsync(),
                    () => _syncCoordinator.SignOutAsync(), () => _syncCoordinator.CurrentUserId,
                    () => _startupService.IsEnabled(), enabled => _startupService.SetEnabled(enabled)));
            await _dashboard.StartAsync();
            CreateTrayIcon();
            if (startInBackground)
            {
                Log("Started in background mode.");
            }
            else
            {
                ShowMainWindow();
            }
        }
        catch (Exception exception)
        {
            Log(exception.ToString());
            MessageBox.Show(
                $"SoftTrace 启动失败：\n\n{exception.Message}",
                "SoftTrace",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void ShowMainWindow() => OpenDashboard();

    private void OpenDashboard(string fragment = "")
    {
        if (IsExiting || _dashboard is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_dashboard.BrowserUrl + fragment) { UseShellExecute = true });
        }
        catch (Exception exception) { Log($"Opening dashboard failed: {exception}"); }
    }

    public void RequestExit()
    {
        if (IsExiting) return;
        IsExiting = true;
        if (_trayContextMenu is not null) _trayContextMenu.IsOpen = false;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        Log("Stopping SoftTrace.");
        _registeredWaitHandle?.Unregister(null);
        _showWindowHandle?.Dispose();
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();
        // Do not capture the dispatcher while it is shutting down. Complete each cleanup
        // independently so a failed service cannot skip the final local-record checkpoint.
        Task.Run(async () =>
        {
            async Task StopService(string name, Func<ValueTask> stop)
            {
                try { await stop().ConfigureAwait(false); }
                catch (Exception exception) { Log($"Shutdown cleanup failed ({name}): {exception}"); }
            }
            if (_dashboard is not null) await StopService("dashboard", _dashboard.DisposeAsync);
            if (_syncCoordinator is not null) await StopService("sync", _syncCoordinator.DisposeAsync);
            if (_capture is not null) await StopService("capture", _capture.DisposeAsync);
        }).GetAwaiter().GetResult();
        _httpClient?.Dispose();
        _singleInstanceMutex?.Dispose();
        Log("SoftTrace stopped.");
        base.OnExit(e);
        // WPF Shutdown ends the application dispatcher; it does not guarantee process
        // termination if a native/COM component still owns a foreground thread. The user
        // explicitly exited and all services have been stopped and records flushed above.
        // End this process normally rather than leaving the single-instance owner alive.
        Environment.Exit(e.ApplicationExitCode);
    }

    private readonly StartupService _startupService = new();
    private System.Windows.Controls.ContextMenu? _trayContextMenu;
    private System.Windows.Controls.MenuItem? _autoStartTrayMenuItem;
    private System.Windows.Controls.MenuItem? _pauseCaptureTrayMenuItem;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private void CreateTrayIcon()
    {
        _trayContextMenu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            HorizontalOffset = -200,
            StaysOpen = false,
            HasDropShadow = false
        };

        _trayContextMenu.Resources = new ResourceDictionary
        {
            Source = new Uri("/SoftTrace;component/Styles/TrayMenu.xaml", UriKind.Relative)
        };
        _trayContextMenu.Style = (Style)_trayContextMenu.Resources[typeof(System.Windows.Controls.ContextMenu)];

        var openItem = new System.Windows.Controls.MenuItem
        {
            Header = "打开 SoftTrace"
        };
        openItem.Click += (_, _) => QueueTrayAction("Open main window from menu", ShowMainWindow);
        _trayContextMenu.Items.Add(openItem);

        _trayContextMenu.Items.Add(new System.Windows.Controls.Separator());

        _pauseCaptureTrayMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = "暂停记录"
        };
        _pauseCaptureTrayMenuItem.Click += async (_, _) =>
        {
            if (_capture is not null)
            {
                await _capture.SetPausedAsync(!_capture.IsPaused);
            }
        };
        _trayContextMenu.Items.Add(_pauseCaptureTrayMenuItem);

        _autoStartTrayMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = "开机自启动",
            IsCheckable = true
        };
        _autoStartTrayMenuItem.Click += (_, _) =>
        {
            var isEnabled = _startupService.IsEnabled();
            try
            {
                _startupService.SetEnabled(!isEnabled);
                _autoStartTrayMenuItem.IsChecked = !isEnabled;
            }
            catch (Exception ex)
            {
                _autoStartTrayMenuItem.IsChecked = isEnabled;
                MessageBox.Show($"设置开机自启动失败：{ex.Message}", "开机自启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        _trayContextMenu.Items.Add(_autoStartTrayMenuItem);

        _trayContextMenu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem
        {
            Header = "退出"
        };
        exitItem.Click += (_, _) => QueueTrayAction("Exit from menu", RequestExit);
        _trayContextMenu.Items.Add(exitItem);

        _trayContextMenu.Opened += (_, _) =>
        {
            Log("Tray menu opened.");
            if (_autoStartTrayMenuItem is not null)
            {
                _autoStartTrayMenuItem.IsChecked = _startupService.IsEnabled();
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (!IsExiting && _trayContextMenu.IsOpen &&
                    System.Windows.PresentationSource.FromVisual(_trayContextMenu) is System.Windows.Interop.HwndSource source &&
                    !source.IsDisposed)
                {
                    SetForegroundWindow(source.Handle);
                    _trayContextMenu.Focus();
                }
            });
        };
        _trayContextMenu.Closed += (_, _) => Log("Tray menu closed.");

        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var iconPath = Path.Combine(appDir, "Assets", "SoftTrace.ico");
            if (File.Exists(iconPath))
            {
                _trayIconImage = new Icon(iconPath);
            }
        }
        catch
        {
        }

        if (_trayIconImage is null && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            try
            {
                _trayIconImage = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            }
            catch
            {
            }
        }
        _trayIconImage ??= (Icon)SystemIcons.Application.Clone();

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "SoftTrace 正在记录软件使用时间",
            Icon = _trayIconImage,
            Visible = true
        };
        if (_capture is not null)
        {
            UpdateTrayCaptureStatus(_capture.CurrentStatus);
        }

        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                QueueTrayAction("Left click", ShowMainWindow);
            }
            else if (e.Button == Forms.MouseButtons.Right)
            {
                QueueTrayAction("Right click", OpenTrayMenu);
            }
        };
    }

    private void QueueTrayAction(string operation, Action action)
    {
        if (IsExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Leave the WinForms native callback before creating WPF popup/window hosts.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
        {
            if (IsExiting)
            {
                return;
            }
            Log($"Tray action: {operation}.");
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Log($"Tray action failed ({operation}): {exception}");
                throw;
            }
        });
    }

    private void OpenTrayMenu()
    {
        if (_trayContextMenu is null || _trayContextMenu.IsOpen || _trayMenuOpening)
        {
            return;
        }

        _trayMenuOpening = true;
        try
        {
            if (_autoStartTrayMenuItem is not null)
            {
                _autoStartTrayMenuItem.IsChecked = _startupService.IsEnabled();
            }
            _trayContextMenu.IsOpen = true;
        }
        finally
        {
            _trayMenuOpening = false;
        }
    }

    private void UpdateTrayCaptureStatus(CaptureStatus status)
    {
        if (_pauseCaptureTrayMenuItem is not null)
        {
            _pauseCaptureTrayMenuItem.Header = status.IsPaused ? "继续记录" : "暂停记录";
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Text = status.Error is not null
                ? "SoftTrace 记录失败，已暂停；请打开窗口查看"
                : status.IsPaused ? "SoftTrace 已暂停记录"
                : status.IsIdle ? "SoftTrace Idle，不计时"
                : "SoftTrace 正在记录软件使用时间";
        }
    }

    private void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                _logPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void MigrateLegacyData(string targetDirectory)
    {
        try
        {
            // The current database and its settings are authoritative. Never replace them with
            // a larger but older MSIX cache, or copy legacy WAL/configuration files over them.
            if (File.Exists(Path.Combine(targetDirectory, "softtrace.db"))) return;
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                "OpenAI.Codex_2p2nqsd0c76g0",
                "LocalCache",
                "Local",
                "SoftTrace");
            if (!Directory.Exists(legacyDir))
            {
                return;
            }

            foreach (var filePath in Directory.GetFiles(legacyDir))
            {
                var fileName = Path.GetFileName(filePath);
                var destPath = Path.Combine(targetDirectory, fileName);
                if (!File.Exists(destPath))
                {
                    File.Copy(filePath, destPath, overwrite: false);
                }
            }
        }
        catch
        {
        }
    }
}
