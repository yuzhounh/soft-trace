using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoftTrace.App;
using SoftTrace.Core;

// Runs in its own process: never uses the daily database, account or instance mutex.
internal sealed class ProbeApp(string root, string webDirectory, bool retainedThread) : App
{
    private void Set(string name, object value) => typeof(App)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);

    protected override async void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(root);
        Set("_logPath", Path.Combine(root, "exit.log"));
        var store = new ActivityStore(Path.Combine(root, "probe.db"));
        await store.InitializeAsync();
        var capture = new ActivityCaptureService(store, "probe", "Probe", 3,
            () => TimeSpan.Zero, () => new AppIdentity("probe", "Probe", null));
        Set("_capture", capture);
        capture.Start();
        var dashboard = new LocalDashboardServer(store, capture,
            () => new(false, false, null, "offline", null, false), () => Task.CompletedTask,
            _ => { }, webDirectory: webDirectory);
        Set("_dashboard", dashboard);
        await dashboard.StartAsync();
        File.WriteAllText(Path.Combine(root, "address.txt"), dashboard.Address);
        typeof(App).GetMethod("CreateTrayIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);
        if (retainedThread)
            new Thread(() => { using var wait = new ManualResetEvent(false); wait.WaitOne(); })
                { IsBackground = false, Name = "ExitRegression-RetainedForegroundThread" }.Start();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        var phase = 0;
        timer.Tick += (_, _) =>
        {
            if (phase++ == 0)
                typeof(App).GetMethod("OpenTrayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);
            else
            {
                timer.Stop();
                var menu = (ContextMenu)typeof(App).GetField("_trayContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
                var exit = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "退出");
                exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
        };
        timer.Start();
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A failing regression must end itself rather than leave a test process running.
        new Thread(() => { Thread.Sleep(10000); Environment.Exit(91); }) { IsBackground = true }.Start();
        var app = new ProbeApp(args[0], args[1], args.Contains("--retained-thread"))
            { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }
}
