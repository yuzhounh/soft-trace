using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private MainWindow? _mainWindow;
    private string? _logPath;

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
            Log("Starting SoftTrace.");
            var store = new ActivityStore(Path.Combine(dataDirectory, "softtrace.db"));
            await store.InitializeAsync();
            var device = await store.GetOrCreateDeviceIdentityAsync(Environment.MachineName);
            await store.RecoverInterruptedSegmentsAsync(device.Id);
            Log("Database initialized.");

            const int idleMinutes = 3;
            await store.SetIdleThresholdMinutesAsync(idleMinutes);
            _capture = new ActivityCaptureService(store, device.Id, device.Name, idleMinutes);
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

            _mainWindow = new MainWindow(store, _capture, _syncCoordinator);
            MainWindow = _mainWindow;
            CreateTrayIcon();
            if (startInBackground)
            {
                Log("Started in background mode.");
            }
            else
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
                _mainWindow.Topmost = true;
                _mainWindow.Topmost = false;
                _mainWindow.Focus();
                Log("Main window shown.");
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
        _mainWindow.NotifyShown();

        try
        {
            var hwnd = new WindowInteropHelper(_mainWindow).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 9); // SW_RESTORE
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
        }
    }

    public void RequestExit()
    {
        IsExiting = true;
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("Stopping SoftTrace.");
        _registeredWaitHandle?.Unregister(null);
        _showWindowHandle?.Dispose();
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();
        if (_syncCoordinator is not null)
        {
            _syncCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        if (_capture is not null)
        {
            _capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _httpClient?.Dispose();
        _singleInstanceMutex?.Dispose();
        Log("SoftTrace stopped.");
        base.OnExit(e);
    }

    private readonly StartupService _startupService = new();
    private System.Windows.Controls.ContextMenu? _trayContextMenu;
    private System.Windows.Controls.MenuItem? _autoStartTrayMenuItem;

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

        var openItem = new System.Windows.Controls.MenuItem
        {
            Header = "打开 SoftTrace"
        };
        openItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayContextMenu.Items.Add(openItem);

        _trayContextMenu.Items.Add(new System.Windows.Controls.Separator());

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
        exitItem.Click += (_, _) => Dispatcher.Invoke(RequestExit);
        _trayContextMenu.Items.Add(exitItem);

        _trayContextMenu.Opened += (_, _) =>
        {
            if (_autoStartTrayMenuItem is not null)
            {
                _autoStartTrayMenuItem.IsChecked = _startupService.IsEnabled();
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (System.Windows.PresentationSource.FromVisual(_trayContextMenu) is System.Windows.Interop.HwndSource source)
                {
                    SetForegroundWindow(source.Handle);
                    _trayContextMenu.Focus();
                }
            });
        };

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

        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_trayContextMenu is not null)
                    {
                        _trayContextMenu.IsOpen = false;
                    }
                    ShowMainWindow();
                });
            }
            else if (e.Button == Forms.MouseButtons.Right)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_trayContextMenu is null)
                    {
                        return;
                    }

                    if (_autoStartTrayMenuItem is not null)
                    {
                        _autoStartTrayMenuItem.IsChecked = _startupService.IsEnabled();
                    }

                    _trayContextMenu.IsOpen = true;
                });
            }
        };
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
                if (!File.Exists(destPath) || new FileInfo(destPath).Length < new FileInfo(filePath).Length)
                {
                    File.Copy(filePath, destPath, overwrite: true);
                }
            }
        }
        catch
        {
        }
    }
}
