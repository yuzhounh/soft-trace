using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoftTrace.Core;

namespace SoftTrace.App;

public partial class MainWindow
{
    private ActivityStore? _store;
    private ActivityCaptureService? _capture;
    private FirebaseSyncCoordinator? _syncCoordinator;
    private bool _initialized;
    private bool _applyingPeriod;
    private bool _updatingDeviceFilters;
    private string? _cloudPhotoUrl;
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(
        ActivityStore store,
        ActivityCaptureService capture,
        FirebaseSyncCoordinator syncCoordinator)
    {
        _store = store;
        _capture = capture;
        _syncCoordinator = syncCoordinator;
        InitializeComponent();
        DataContext = this;

        var today = DateTime.Today;
        FromDatePicker.SelectedDate = today;
        ToDatePicker.SelectedDate = today;
        _capture.StatusChanged += CaptureOnStatusChanged;
        _syncCoordinator.StatusChanged += SyncCoordinatorOnStatusChanged;
        _refreshTimer.Tick += RefreshTimer_OnTick;
        Loaded += OnLoaded;
        Closing += OnClosing;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public ObservableCollection<UsageDisplayRow> UsageRows { get; } = [];
    public ObservableCollection<DeviceFilterItem> DeviceFilters { get; } = [];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initialized = true;
        await RefreshDeviceFiltersAsync();
        await RefreshUsageAsync();
        _refreshTimer.Start();
        UpdateCaptureStatus(_capture?.CurrentStatus ?? new CaptureStatus(false, false, null));
        UpdateCloudStatus(_syncCoordinator?.CurrentStatus ??
                          new CloudSyncStatus(false, false, null, "尚未登录", null, false));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _refreshTimer.Stop();
            if (_syncCoordinator is not null)
            {
                _syncCoordinator.StatusChanged -= SyncCoordinatorOnStatusChanged;
            }
        }
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (IsVisible)
        {
            _refreshTimer.Start();
            await RefreshUsageAsync();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private async void RefreshTimer_OnTick(object? sender, EventArgs e) => await RefreshUsageAsync();

    private void CaptureOnStatusChanged(object? sender, CaptureStatus status) =>
        Dispatcher.BeginInvoke(() => UpdateCaptureStatus(status));

    private void SyncCoordinatorOnStatusChanged(object? sender, CloudSyncStatus status) =>
        Dispatcher.BeginInvoke(async () =>
        {
            UpdateCloudStatus(status);
            if (status.IsSignedIn && !status.IsSyncing && !status.HasError)
            {
                await RefreshDeviceFiltersAsync();
                await RefreshUsageAsync();
            }
        });

    private void UpdateCloudStatus(CloudSyncStatus status)
    {
        GoogleSignInButton.Visibility = status.IsSignedIn ? Visibility.Collapsed : Visibility.Visible;
        CloudAccountButton.Visibility = status.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!status.IsSignedIn)
        {
            CloudAccountButton.IsChecked = false;
        }

        CloudAccountText.Text = status.Email ?? "Google 账号";
        CloudStatusText.Text = status.Message;
        CloudAccountButton.ToolTip = $"{status.Email ?? "Google 账号"}\n{status.Message}";
        CloudStatusText.Foreground = status.HasError
            ? System.Windows.Media.Brushes.IndianRed
            : (Brush)FindResource("MutedTextBrush");
        UpdateCloudAvatar(status.PhotoUrl, status.DisplayName, status.Email);
        GoogleSignInButton.IsEnabled = !status.IsSyncing;
        SyncNowButton.IsEnabled = !status.IsSyncing;
        GoogleSignOutButton.IsEnabled = !status.IsSyncing;
    }

    private void UpdateCloudAvatar(string? photoUrl, string? displayName, string? email)
    {
        var fallbackName = !string.IsNullOrWhiteSpace(displayName) ? displayName : email;
        CloudAvatarFallbackText.Text = string.IsNullOrWhiteSpace(fallbackName)
            ? "G"
            : fallbackName.Trim()[0].ToString().ToUpperInvariant();

        if (string.Equals(_cloudPhotoUrl, photoUrl, StringComparison.Ordinal))
        {
            return;
        }

        _cloudPhotoUrl = photoUrl;
        CloudAvatarEllipse.Fill = new SolidColorBrush(Color.FromRgb(0xE7, 0xEC, 0xFB));
        CloudAvatarFallbackText.Visibility = Visibility.Visible;
        if (!Uri.TryCreate(photoUrl, UriKind.Absolute, out var photoUri))
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = photoUri;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            var imageBrush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            CloudAvatarEllipse.Fill = imageBrush;
            bitmap.DownloadCompleted += (_, _) =>
            {
                if (string.Equals(_cloudPhotoUrl, photoUrl, StringComparison.Ordinal))
                {
                    CloudAvatarFallbackText.Visibility = Visibility.Collapsed;
                }
            };
            bitmap.DownloadFailed += (_, _) =>
            {
                if (string.Equals(_cloudPhotoUrl, photoUrl, StringComparison.Ordinal))
                {
                    CloudAvatarEllipse.Fill = new SolidColorBrush(Color.FromRgb(0xE7, 0xEC, 0xFB));
                    CloudAvatarFallbackText.Visibility = Visibility.Visible;
                }
            };
        }
        catch
        {
            CloudAvatarEllipse.Fill = new SolidColorBrush(Color.FromRgb(0xE7, 0xEC, 0xFB));
            CloudAvatarFallbackText.Visibility = Visibility.Visible;
        }
    }

    private async void GoogleSignInButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_syncCoordinator is null)
        {
            return;
        }

        try
        {
            await _syncCoordinator.SignInWithGoogleAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Google 登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SyncNowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_syncCoordinator is not null)
        {
            await _syncCoordinator.SyncNowAsync();
        }
    }

    private async void GoogleSignOutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_syncCoordinator is not null)
        {
            await _syncCoordinator.SignOutAsync();
        }
    }

    private void UpdateCaptureStatus(CaptureStatus status)
    {
        if (status.IsPaused)
        {
            CaptureStateText.Text = "已暂停";
        }
        else if (status.IsIdle)
        {
            CaptureStateText.Text = "Idle，不计时";
        }
        else
        {
            CaptureStateText.Text = "正在记录";
        }

        if (status.CurrentApp is not { } currentApp)
        {
            CurrentAppBadge.Visibility = Visibility.Collapsed;
            CurrentAppIcon.Source = null;
            CurrentAppNameText.Text = string.Empty;
            return;
        }

        CurrentAppIcon.Source = GetApplicationIcon(currentApp.ProcessName, currentApp.ExecutablePath);
        CurrentAppNameText.Text = currentApp.AppName;
        CurrentAppBadge.Visibility = Visibility.Visible;
    }

    private async Task RefreshUsageAsync()
    {
        if (_store is null ||
            FromDatePicker.SelectedDate is not DateTime fromDate ||
            ToDatePicker.SelectedDate is not DateTime toDate)
        {
            return;
        }

        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
            FromDatePicker.SelectedDate = fromDate;
            ToDatePicker.SelectedDate = toDate;
        }

        var rangeStartUtc = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Unspecified)));
        var rangeEndUtc = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(toDate.Date.AddDays(1), DateTimeKind.Unspecified)));
        var selectedDeviceId = DeviceFilterComboBox.SelectedValue as string;
        var rows = await _store.GetUsageAsync(
            rangeStartUtc,
            rangeEndUtc,
            string.IsNullOrWhiteSpace(selectedDeviceId) ? null : selectedDeviceId);
        var total = TimeSpan.FromSeconds(rows.Sum(row => row.Duration.TotalSeconds));

        UsageRows.Clear();
        foreach (var row in rows)
        {
            var durationSeconds = row.Duration.TotalSeconds;
            var shareRatio = total > TimeSpan.Zero ? durationSeconds / total.TotalSeconds : 0;
            UsageRows.Add(new UsageDisplayRow(
                row.AppName,
                row.ProcessName,
                GetApplicationIcon(row.ProcessName, row.ExecutablePath),
                FormatDuration(row.Duration),
                durationSeconds,
                $"{shareRatio:P1}",
                shareRatio));
        }

        TotalDurationText.Text = FormatDuration(total);
        RangeSummaryText.Text = fromDate.Date == toDate.Date
            ? fromDate.ToString("yyyy 年 M 月 d 日")
            : $"{fromDate:yyyy-MM-dd} 至 {toDate:yyyy-MM-dd}";
    }

    private async void PeriodComboBox_OnSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized || _applyingPeriod || _store is null ||
            PeriodComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem ||
            selectedItem.Tag is not string period)
        {
            return;
        }

        var today = DateTime.Today;
        DateTime fromDate;
        switch (period)
        {
            case "ThisWeek":
                var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
                fromDate = today.AddDays(-daysSinceMonday);
                break;
            case "ThisMonth":
                fromDate = new DateTime(today.Year, today.Month, 1);
                break;
            case "ThisQuarter":
                var quarterStartMonth = ((today.Month - 1) / 3 * 3) + 1;
                fromDate = new DateTime(today.Year, quarterStartMonth, 1);
                break;
            case "ThisYear":
                fromDate = new DateTime(today.Year, 1, 1);
                break;
            case "Last7Days":
                fromDate = today.AddDays(-6);
                break;
            case "Last30Days":
                fromDate = today.AddDays(-29);
                break;
            case "LastQuarter":
                fromDate = today.AddMonths(-3).AddDays(1);
                break;
            case "LastYear":
                fromDate = today.AddYears(-1).AddDays(1);
                break;
            case "All":
                var selectedDeviceId = DeviceFilterComboBox.SelectedValue as string;
                var earliestUtc = await _store.GetEarliestActivityUtcAsync(
                    string.IsNullOrWhiteSpace(selectedDeviceId) ? null : selectedDeviceId);
                fromDate = earliestUtc?.ToLocalTime().Date ?? today;
                break;
            default:
                fromDate = today;
                break;
        }

        _applyingPeriod = true;
        try
        {
            FromDatePicker.SelectedDate = fromDate;
            ToDatePicker.SelectedDate = today;
        }
        finally
        {
            _applyingPeriod = false;
        }
        await RefreshUsageAsync();
    }

    private async void DatePicker_OnSelectedDateChanged(
        object? sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initialized && !_applyingPeriod)
        {
            _applyingPeriod = true;
            try
            {
                PeriodComboBox.SelectedIndex = PeriodComboBox.Items.Count - 1;
            }
            finally
            {
                _applyingPeriod = false;
            }
            await RefreshUsageAsync();
        }
    }

    private async void DeviceFilterComboBox_OnSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initialized && !_updatingDeviceFilters)
        {
            await RefreshUsageAsync();
        }
    }

    private async Task RefreshDeviceFiltersAsync()
    {
        if (_store is null)
        {
            return;
        }

        var selectedId = DeviceFilterComboBox.SelectedValue as string ?? string.Empty;
        var devices = await _store.GetDevicesAsync();
        _updatingDeviceFilters = true;
        try
        {
            DeviceFilters.Clear();
            DeviceFilters.Add(new DeviceFilterItem(string.Empty, "全部设备"));
            foreach (var device in devices)
            {
                DeviceFilters.Add(new DeviceFilterItem(device.Id, device.Name));
            }

            DeviceFilterComboBox.SelectedValue = DeviceFilters.Any(item => item.Id == selectedId)
                ? selectedId
                : string.Empty;
        }
        finally
        {
            _updatingDeviceFilters = false;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟";
        }
        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes} 分钟";
        }
        return $"{Math.Max(0, (int)duration.TotalSeconds)} 秒";
    }

    private ImageSource GetApplicationIcon(string processName, string? executablePath)
    {
        var cacheKey = executablePath ?? processName;
        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
        {
            return cachedIcon;
        }

        System.Drawing.Icon icon;
        try
        {
            icon = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
                ? System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
                    ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone()
                : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }
        catch
        {
            icon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        using (icon)
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            image.Freeze();
            _iconCache[cacheKey] = image;
            return image;
        }
    }

    public sealed record UsageDisplayRow(
        string AppName,
        string ProcessName,
        ImageSource Icon,
        string DurationText,
        double DurationSeconds,
        string ShareText,
        double ShareRatio);

    public sealed record DeviceFilterItem(string Id, string Name);
}
