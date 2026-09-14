using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Windows;
using SoftTrace.Core;
using Forms = System.Windows.Forms;

namespace SoftTrace.App;

public partial class App
{
    private Mutex? _singleInstanceMutex;
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

        _singleInstanceMutex = new Mutex(true, "SoftTrace.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("SoftTrace 已经在运行。", "SoftTrace", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftTrace");
            Directory.CreateDirectory(dataDirectory);
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
            _mainWindow.Show();
            Log("Main window shown.");
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

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
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

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 SoftTrace", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(RequestExit));

        _trayIconImage = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
        _trayIconImage ??= (Icon)SystemIcons.Application.Clone();

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "SoftTrace 正在记录软件使用时间",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
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
}
