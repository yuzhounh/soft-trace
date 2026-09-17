using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SoftTrace.Core;

namespace SoftTrace.App;

public partial class MainWindow
{
    private const string MainWindowSizeSettingKey = "main_window_size";
    private const string UsageColumnWidthsSettingKey = "usage_column_widths";
    private readonly StartupService _startupService = new();
    private ActivityStore? _store;
    private ActivityCaptureService? _capture;
    private FirebaseSyncCoordinator? _syncCoordinator;
    private bool _initialized;
    private bool _applyingPeriod;
    private bool _deviceFilterInitialized;
    private string _selectedDeviceId = string.Empty;
    private string? _cloudPhotoUrl;
    private readonly List<UsageDisplayRow> _allUsageRows = [];
    private int _currentPage = 1;
    private int _pageSize = 50;
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private readonly DispatcherTimer _windowSizeSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };
    private readonly ConcurrentDictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _resolvedOrFailedKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _iconResolutionCts;

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
        _windowSizeSaveTimer.Tick += WindowSizeSaveTimer_OnTick;
        Loaded += OnLoaded;
        Closing += OnClosing;
        SizeChanged += MainWindow_OnSizeChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public ObservableCollection<UsageDisplayRow> UsageRows { get; } = [];
    public ObservableCollection<DeviceFilterItem> DeviceFilters { get; } = [];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RestoreMainWindowSizeAsync();
        await RestoreUsageColumnWidthsAsync();
        _initialized = true;
        await RefreshDeviceFiltersAsync();
        await RefreshUsageAsync(resetPage: true);
        _refreshTimer.Start();
        UpdateCaptureStatus(_capture?.CurrentStatus ?? new CaptureStatus(false, false, null));
        UpdateCloudStatus(_syncCoordinator?.CurrentStatus ??
                          new CloudSyncStatus(false, false, null, "尚未登录", null, false));
        AutoStartCheckBox.IsChecked = _startupService.IsEnabled();
    }

    private async Task RestoreMainWindowSizeAsync()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var savedSize = await _store.GetSettingAsync(MainWindowSizeSettingKey);
            var sizeValues = savedSize?.Split(';');
            if (sizeValues?.Length != 2 ||
                !double.TryParse(
                    sizeValues[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var savedWidth) ||
                !double.TryParse(
                    sizeValues[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var savedHeight) ||
                !double.IsFinite(savedWidth) ||
                !double.IsFinite(savedHeight))
            {
                return;
            }

            var workArea = SystemParameters.WorkArea;
            Width = Math.Clamp(savedWidth, MinWidth, Math.Max(MinWidth, workArea.Width));
            Height = Math.Clamp(savedHeight, MinHeight, Math.Max(MinHeight, workArea.Height));
        }
        catch
        {
            // Invalid or unavailable UI preferences should not prevent startup.
        }
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(UpdateUsageTableHeight, DispatcherPriority.Loaded);
        }

        if (!_initialized || WindowState != WindowState.Normal)
        {
            return;
        }

        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
    }

    private async void WindowSizeSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _windowSizeSaveTimer.Stop();
        if (_store is null || WindowState != WindowState.Normal)
        {
            return;
        }

        try
        {
            var savedSize = string.Join(
                ";",
                ActualWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ActualHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            await _store.SetSettingAsync(MainWindowSizeSettingKey, savedSize);
        }
        catch
        {
            // Window resizing should remain usable even if preferences cannot be saved.
        }
    }

    private async Task RestoreUsageColumnWidthsAsync()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var savedWidths = await _store.GetSettingAsync(UsageColumnWidthsSettingKey);
            var widthValues = savedWidths?.Split(';');
            if (widthValues?.Length != UsageDataGrid.Columns.Count)
            {
                return;
            }

            // Keep the final column flexible so it always absorbs the table's remaining width.
            for (var index = 0; index < widthValues.Length - 1; index++)
            {
                if (double.TryParse(
                        widthValues[index],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var width) &&
                    double.IsFinite(width) &&
                    width > 0)
                {
                    var column = UsageDataGrid.Columns[index];
                    column.Width = new System.Windows.Controls.DataGridLength(
                        Math.Clamp(width, column.MinWidth, 1200));
                }
            }
        }
        catch
        {
            // Invalid or unavailable UI preferences should not prevent startup.
        }
    }

    private async void UsageDataGrid_OnPreviewMouseLeftButtonUp(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_store is null ||
            e.OriginalSource is not DependencyObject source ||
            FindVisualAncestor<System.Windows.Controls.Primitives.Thumb>(source) is null)
        {
            return;
        }

        try
        {
            var widths = string.Join(
                ";",
                UsageDataGrid.Columns.Select(column =>
                    column.ActualWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            await _store.SetSettingAsync(UsageColumnWidthsSettingKey, widths);
        }
        catch
        {
            // Column resizing should remain usable even if preferences cannot be saved.
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static System.Windows.Controls.ScrollViewer? GetScrollViewer(DependencyObject dep)
    {
        if (dep is System.Windows.Controls.ScrollViewer sv)
        {
            return sv;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(dep); i++)
        {
            var child = VisualTreeHelper.GetChild(dep, i);
            var result = GetScrollViewer(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
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
            _windowSizeSaveTimer.Stop();
            _iconResolutionCts?.Cancel();
            _iconResolutionCts?.Dispose();
            _iconResolutionCts = null;
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
            _iconResolutionCts?.Cancel();
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

        var displayName = string.IsNullOrWhiteSpace(status.DisplayName)
            ? "Google 用户"
            : status.DisplayName;
        CloudDisplayNameText.Text = displayName;
        CloudAccountText.Text = status.Email ?? "Google 账号";
        CloudStatusText.Text = status.LastSyncUtc is { } lastSyncUtc
            ? $"{status.Message}\n上次同步：{lastSyncUtc.ToLocalTime():HH:mm:ss}"
            : status.Message;
        CloudAccountButton.ToolTip = $"{displayName}\n{status.Email ?? "Google 账号"}\n{status.Message}";
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

    private void AutoStartCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        var isChecked = AutoStartCheckBox.IsChecked == true;
        try
        {
            _startupService.SetEnabled(isChecked);
        }
        catch (Exception ex)
        {
            AutoStartCheckBox.IsChecked = !isChecked;
            MessageBox.Show($"设置开机自启动失败：{ex.Message}", "开机自启动", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出活动记录数据",
            Filter = "SoftTrace 数据包 (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"SoftTrace_Export_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ExportButton.IsEnabled = false;
        var prevContent = ExportButton.Content;
        ExportButton.Content = "正在导出…";
        try
        {
            DateTimeOffset? rangeStartUtc = null;
            DateTimeOffset? rangeEndUtc = null;
            if (FromDatePicker.SelectedDate is { } fromDate && ToDatePicker.SelectedDate is { } toDate)
            {
                if (toDate < fromDate)
                {
                    (fromDate, toDate) = (toDate, fromDate);
                }
                // If "All" is not selected, we export current date range; if "All" is selected, export all
                if (PeriodComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && (string?)item.Tag != "All")
                {
                    rangeStartUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Unspecified)));
                    rangeEndUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(toDate.Date.AddDays(1), DateTimeKind.Unspecified)));
                }
            }

            var deviceId = string.IsNullOrWhiteSpace(_selectedDeviceId) ? null : _selectedDeviceId;
            await _store.ExportActivitiesToFileAsync(dialog.FileName, deviceId, rangeStartUtc, rangeEndUtc);
            MessageBox.Show("数据导出完成！", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出数据失败：{ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ExportButton.Content = prevContent;
            ExportButton.IsEnabled = true;
        }
    }

    private async void ImportButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_store is null || _capture is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择导入文件 (SoftTrace JSON 或 ManicTime 备份)",
            Filter = "所有支持格式 (*.json;*.zip;ManicTimeReports.db)|*.json;*.zip;ManicTimeReports.db|SoftTrace 数据包 (*.json)|*.json|ManicTime 备份 (*.zip;*.db)|*.zip;*.db;ManicTimeReports.db|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImportButton.IsEnabled = false;
        var prevContent = ImportButton.Content;
        ImportButton.Content = "正在导入…";
        try
        {
            var ext = Path.GetExtension(dialog.FileName);
            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            {
                var result = await _store.ImportActivitiesFromFileAsync(dialog.FileName);
                await RefreshDeviceFiltersAsync();
                await RefreshUsageAsync(resetPage: true);

                var msg = $"导入完成！\n共读取 {result.TotalItemsInPackage:N0} 条记录\n成功导入：{result.ImportedCount:N0} 条（总时长：{FormatDuration(result.ImportedDuration)}）\n重复或重叠跳过：{FormatDuration(result.OverlapDuration)}\n涉及设备数：{result.AffectedDeviceCount}";
                MessageBox.Show(msg, "SoftTrace 导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // ManicTime import
                var result = await _store.ImportManicTimeAsync(
                    dialog.FileName,
                    _capture.DeviceId,
                    _capture.DeviceName);
                await RefreshDeviceFiltersAsync();
                await RefreshUsageAsync(resetPage: true);

                var dateRange = result.EarliestUtc is { } earliest && result.LatestUtc is { } latest
                    ? $"\n时间范围：{earliest.ToLocalTime():yyyy-MM-dd} 至 {latest.ToLocalTime():yyyy-MM-dd}"
                    : string.Empty;
                var message = result.ImportedSegmentCount == 0
                    ? $"没有需要导入的新记录。已有数据覆盖了所选备份中的 {FormatDuration(result.OverlapDuration)}。{dateRange}"
                    : $"已导入 {result.ImportedSegmentCount:N0} 条记录，共 {FormatDuration(result.ImportedDuration)}。\n" +
                      $"已由 Soft Trace 或此前导入覆盖：{FormatDuration(result.OverlapDuration)}。{dateRange}";
                MessageBox.Show(message, "ManicTime 导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ImportButton.Content = prevContent;
            ImportButton.IsEnabled = true;
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

        CurrentAppIcon.Source = GetApplicationIcon(currentApp.ProcessName, currentApp.ExecutablePath, currentApp.AppName);
        CurrentAppNameText.Text = currentApp.AppName;
        CurrentAppBadge.Visibility = Visibility.Visible;
    }

    private async Task RefreshUsageAsync(bool resetPage = false)
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
        var rows = await _store.GetUsageAsync(
            rangeStartUtc,
            rangeEndUtc,
            string.IsNullOrWhiteSpace(_selectedDeviceId) ? null : _selectedDeviceId);
        var total = TimeSpan.FromSeconds(rows.Sum(row => row.Duration.TotalSeconds));

        _allUsageRows.Clear();
        foreach (var row in rows)
        {
            var durationSeconds = row.Duration.TotalSeconds;
            var shareRatio = total > TimeSpan.Zero ? durationSeconds / total.TotalSeconds : 0;
            var cacheKey = GetCacheKey(row.ProcessName, row.ExecutablePath);
            if (!_iconCache.TryGetValue(cacheKey, out var icon))
            {
                icon = GenericApplicationIcon;
            }

            _allUsageRows.Add(new UsageDisplayRow(
                row.AppName ?? row.ProcessName,
                row.ProcessName,
                row.ExecutablePath,
                icon,
                FormatDuration(row.Duration),
                durationSeconds,
                $"{shareRatio:P1}",
                shareRatio));
        }

        TotalDurationText.Text = FormatDuration(total);
        RangeSummaryText.Text = fromDate.Date == toDate.Date
            ? fromDate.ToString("yyyy 年 M 月 d 日")
            : $"{fromDate:yyyy-MM-dd} 至 {toDate:yyyy-MM-dd}";

        if (resetPage)
        {
            _currentPage = 1;
        }
        UpdatePagedView(preserveScroll: !resetPage);
    }

    private void UpdatePagedView(bool preserveScroll = false)
    {
        var totalCount = _allUsageRows.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / _pageSize));
        if (_currentPage > totalPages)
        {
            _currentPage = totalPages;
        }
        if (_currentPage < 1)
        {
            _currentPage = 1;
        }

        var pagedItems = _allUsageRows
            .Skip((_currentPage - 1) * _pageSize)
            .Take(_pageSize)
            .ToList();

        var scrollViewer = GetScrollViewer(UsageDataGrid);
        var verticalOffset = scrollViewer?.VerticalOffset ?? 0;
        var horizontalOffset = scrollViewer?.HorizontalOffset ?? 0;
        var selectedItem = UsageDataGrid.SelectedItem as UsageDisplayRow;

        // In-place update to preserve scroll position and avoid visual jumping
        for (var i = 0; i < pagedItems.Count; i++)
        {
            if (i < UsageRows.Count)
            {
                if (!UsageRows[i].Equals(pagedItems[i]))
                {
                    UsageRows[i] = pagedItems[i];
                }
                else if (!ReferenceEquals(UsageRows[i].Icon, pagedItems[i].Icon))
                {
                    UsageRows[i].Icon = pagedItems[i].Icon;
                }
            }
            else
            {
                UsageRows.Add(pagedItems[i]);
            }
        }
        while (UsageRows.Count > pagedItems.Count)
        {
            UsageRows.RemoveAt(UsageRows.Count - 1);
        }

        PaginationSummaryText.Text = $"共 {totalCount:N0} 条记录";
        PageNumberText.Text = $"第 {_currentPage} / {totalPages} 页";

        FirstPageButton.IsEnabled = _currentPage > 1;
        PreviousPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < totalPages;
        LastPageButton.IsEnabled = _currentPage < totalPages;

        if (preserveScroll && scrollViewer is not null)
        {
            if (verticalOffset > 0)
            {
                scrollViewer.ScrollToVerticalOffset(verticalOffset);
            }
            if (horizontalOffset > 0)
            {
                scrollViewer.ScrollToHorizontalOffset(horizontalOffset);
            }
        }
        else if (!preserveScroll && scrollViewer is not null)
        {
            scrollViewer.ScrollToTop();
        }

        if (selectedItem is not null)
        {
            var match = UsageRows.FirstOrDefault(r =>
                string.Equals(r.AppName, selectedItem.AppName, StringComparison.Ordinal) &&
                string.Equals(r.ProcessName, selectedItem.ProcessName, StringComparison.Ordinal));
            if (match is not null)
            {
                UsageDataGrid.SelectedItem = match;
            }
        }

        TriggerLazyIconResolution(pagedItems);
    }

    private void FirstPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage != 1)
        {
            _currentPage = 1;
            UpdatePagedView(preserveScroll: false);
        }
    }

    private void PreviousPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            UpdatePagedView(preserveScroll: false);
        }
    }

    private void NextPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling((double)_allUsageRows.Count / _pageSize));
        if (_currentPage < totalPages)
        {
            _currentPage++;
            UpdatePagedView(preserveScroll: false);
        }
    }

    private void LastPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling((double)_allUsageRows.Count / _pageSize));
        if (_currentPage != totalPages)
        {
            _currentPage = totalPages;
            UpdatePagedView(preserveScroll: false);
        }
    }

    private void PageSizeComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized || PageSizeComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
        {
            return;
        }

        if (int.TryParse(item.Content?.ToString(), out var newSize) && newSize > 0)
        {
            _pageSize = newSize;
            _currentPage = 1;
            UpdatePagedView(preserveScroll: false);
        }
    }

    private void UpdateUsageTableHeight()
    {
        // Allow table to naturally stretch with window height
        UsageTableBorder.ClearValue(FrameworkElement.HeightProperty);
    }

    private void UsageDataGrid_OnSorting(
        object sender,
        System.Windows.Controls.DataGridSortingEventArgs e)
    {
        if (e.Column.SortDirection != System.ComponentModel.ListSortDirection.Descending)
        {
            return;
        }

        e.Handled = true;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(UsageDataGrid.ItemsSource);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (var column in UsageDataGrid.Columns)
            {
                column.SortDirection = null;
            }
        }
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
        var toDate = today;
        DateTime fromDate;
        switch (period)
        {
            case "Yesterday":
                fromDate = today.AddDays(-1);
                toDate = today.AddDays(-1);
                break;
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
                var earliestUtc = await _store.GetEarliestActivityUtcAsync(
                    string.IsNullOrWhiteSpace(_selectedDeviceId) ? null : _selectedDeviceId);
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
            ToDatePicker.SelectedDate = toDate;
        }
        finally
        {
            _applyingPeriod = false;
        }
        await RefreshUsageAsync(resetPage: true);
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
            await RefreshUsageAsync(resetPage: true);
        }
    }

    private void DatePicker_OnCalendarOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DatePicker datePicker)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (datePicker.Template.FindName("PART_Popup", datePicker) is not
                    System.Windows.Controls.Primitives.Popup popup ||
                datePicker.Template.FindName("PART_Button", datePicker) is not FrameworkElement calendarButton)
            {
                return;
            }

            popup.PlacementTarget = calendarButton;
            popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 6;
            if (datePicker.Template.FindName("PART_Calendar", datePicker) is FrameworkElement calendarHost)
            {
                calendarHost.LayoutTransform = new ScaleTransform(1.4, 1.4);
            }
        }, DispatcherPriority.Loaded);
    }

    private async void DeviceFilterItem_OnMouseLeftButtonUp(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border row ||
            row.DataContext is not DeviceFilterItem device)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            (FindVisualAncestor<System.Windows.Controls.TextBox>(source) is not null ||
             FindVisualAncestor<System.Windows.Controls.Button>(source) is not null))
        {
            return;
        }

        e.Handled = true;
        SetSelectedDevice(device.Id);
        DeviceFilterPopup.IsOpen = false;
        DeviceFilterToggleButton.IsChecked = false;
        if (_initialized)
        {
            await RefreshUsageAsync(resetPage: true);
        }
    }

    private void DeviceFilterPopup_OnClosed(object? sender, EventArgs e) =>
        DeviceFilterToggleButton.IsChecked = false;

    private void SetSelectedDevice(string deviceId)
    {
        _selectedDeviceId = deviceId;
        foreach (var device in DeviceFilters)
        {
            device.IsSelected = string.Equals(device.Id, deviceId, StringComparison.Ordinal);
        }

        DeviceFilterToggleButton.Content = DeviceFilters
            .FirstOrDefault(device => string.Equals(device.Id, deviceId, StringComparison.Ordinal))?.Name
            ?? "全部设备";
    }

    private void DeviceEditButton_OnPreviewMouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button button ||
            button.DataContext is not DeviceFilterItem device ||
            string.IsNullOrWhiteSpace(device.Id) ||
            button.Parent is not System.Windows.Controls.Grid row)
        {
            return;
        }

        var editor = row.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
        var nameText = row.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();
        if (editor is null || nameText is null)
        {
            return;
        }

        editor.Text = device.Name;
        nameText.Visibility = Visibility.Collapsed;
        button.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            editor.Focus();
            System.Windows.Input.Keyboard.Focus(editor);
            editor.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void DeviceNameEditor_OnPreviewMouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox editor && !editor.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            editor.Focus();
            editor.SelectAll();
        }
    }

    private async void DeviceNameEditor_OnLostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox editor)
        {
            await SaveInlineDeviceNameAsync(editor);
        }
    }

    private async void DeviceNameEditor_OnKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox editor)
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await SaveInlineDeviceNameAsync(editor);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            EndInlineDeviceNameEdit(editor);
        }
    }

    private async Task SaveInlineDeviceNameAsync(System.Windows.Controls.TextBox editor)
    {
        if (_store is null || editor.Visibility != Visibility.Visible ||
            editor.DataContext is not DeviceFilterItem device ||
            string.IsNullOrWhiteSpace(device.Id))
        {
            return;
        }

        var newName = editor.Text.Trim();
        EndInlineDeviceNameEdit(editor);
        if (string.IsNullOrWhiteSpace(newName) ||
            string.Equals(device.Name, newName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _store.SetDeviceDisplayNameAsync(device.Id, newName);
            await RefreshDeviceFiltersAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "重命名设备失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void EndInlineDeviceNameEdit(System.Windows.Controls.TextBox editor)
    {
        editor.Visibility = Visibility.Collapsed;
        if (editor.Parent is not System.Windows.Controls.Grid row)
        {
            return;
        }

        var nameText = row.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();
        var editButton = row.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault();
        if (nameText is not null)
        {
            nameText.Visibility = Visibility.Visible;
        }
        if (editButton is not null)
        {
            editButton.ClearValue(UIElement.VisibilityProperty);
        }
    }

    private async Task RefreshDeviceFiltersAsync()
    {
        if (_store is null)
        {
            return;
        }

        var currentDeviceId = _capture?.DeviceId ?? string.Empty;
        var selectedId = _deviceFilterInitialized
            ? _selectedDeviceId
            : currentDeviceId;
        var devices = await _store.GetDevicesAsync();
        var currentDeviceAlias = string.IsNullOrWhiteSpace(currentDeviceId)
            ? null
            : await _store.GetSettingAsync($"device_display_name:{currentDeviceId}");
        DeviceFilters.Clear();
        DeviceFilters.Add(new DeviceFilterItem(string.Empty, "全部设备"));
        foreach (var device in devices)
        {
            DeviceFilters.Add(new DeviceFilterItem(device.Id, device.Name));
        }

        if (!string.IsNullOrWhiteSpace(currentDeviceId) &&
            DeviceFilters.All(item => item.Id != currentDeviceId) &&
            _capture is not null)
        {
            DeviceFilters.Insert(1, new DeviceFilterItem(
                currentDeviceId,
                string.IsNullOrWhiteSpace(currentDeviceAlias) ? _capture.DeviceName : currentDeviceAlias));
        }

        var resolvedDeviceId = DeviceFilters.Any(item => item.Id == selectedId)
            ? selectedId
            : DeviceFilters.Any(item => item.Id == currentDeviceId)
                ? currentDeviceId
                : string.Empty;
        SetSelectedDevice(resolvedDeviceId);
        _deviceFilterInitialized = true;
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

    private readonly string _iconCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftTrace", "Icons");

    private static string GetCacheKey(string processName, string? executablePath) =>
        string.IsNullOrWhiteSpace(processName) ? (executablePath ?? "unknown") : processName;

    private void TriggerLazyIconResolution(IReadOnlyList<UsageDisplayRow> pagedItems)
    {
        _iconResolutionCts?.Cancel();
        _iconResolutionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _iconResolutionCts = cts;
        var token = cts.Token;

        var itemsToResolve = pagedItems
            .Where(r =>
            {
                var key = GetCacheKey(r.ProcessName, r.ExecutablePath);
                return !_resolvedOrFailedKeys.ContainsKey(key);
            })
            .ToList();

        if (itemsToResolve.Count == 0)
        {
            return;
        }

        Task.Run(() =>
        {
            foreach (var item in itemsToResolve)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var key = GetCacheKey(item.ProcessName, item.ExecutablePath);
                if (_iconCache.TryGetValue(key, out var cached))
                {
                    _resolvedOrFailedKeys[key] = true;
                    Dispatcher.BeginInvoke(() => item.Icon = cached);
                    continue;
                }

                var resolved = ResolveAndLoadIcon(item.ProcessName, item.ExecutablePath, item.AppName);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (resolved is not null)
                {
                    _iconCache[key] = resolved;
                    _resolvedOrFailedKeys[key] = true;
                    Dispatcher.BeginInvoke(() => item.Icon = resolved);
                }
                else
                {
                    _resolvedOrFailedKeys[key] = true;
                }
            }
        }, token);
    }

    private ImageSource GetApplicationIcon(string processName, string? executablePath, string? appName = null)
    {
        var cacheKey = GetCacheKey(processName, executablePath);
        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
        {
            return cachedIcon;
        }

        var fallback = GenericApplicationIcon;

        // Fast disk cache check
        try
        {
            var diskCachePath = Path.Combine(_iconCacheDirectory, $"{SanitizeFileName(cacheKey)}.png");
            if (File.Exists(diskCachePath))
            {
                var diskImage = LoadBitmapFromPath(diskCachePath);
                if (diskImage is not null)
                {
                    _iconCache[cacheKey] = diskImage;
                    _resolvedOrFailedKeys[cacheKey] = true;
                    return diskImage;
                }
            }
        }
        catch
        {
        }

        // Fast bundled icon check
        var knownIcon = LoadKnownBundledIcon(processName, appName);
        if (knownIcon is not null)
        {
            _iconCache[cacheKey] = knownIcon;
            _resolvedOrFailedKeys[cacheKey] = true;
            SaveIconToDiskCache(cacheKey, knownIcon);
            return knownIcon;
        }

        // Trigger background resolution so UI never stalls
        Task.Run(() =>
        {
            var resolved = ResolveAndLoadIcon(processName, executablePath, appName);
            if (resolved is not null)
            {
                _iconCache[cacheKey] = resolved;
                _resolvedOrFailedKeys[cacheKey] = true;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_capture?.CurrentStatus.CurrentApp?.ProcessName == processName)
                    {
                        CurrentAppIcon.Source = resolved;
                    }
                });
            }
            else
            {
                _resolvedOrFailedKeys[cacheKey] = true;
            }
        });

        return fallback;
    }

    private ImageSource? ResolveAndLoadIcon(string processName, string? executablePath, string? appName)
    {
        var cacheKey = GetCacheKey(processName, executablePath);

        // 1. Check persistent disk icon cache
        try
        {
            var diskCachePath = Path.Combine(_iconCacheDirectory, $"{SanitizeFileName(cacheKey)}.png");
            if (File.Exists(diskCachePath))
            {
                var diskImage = LoadBitmapFromPath(diskCachePath);
                if (diskImage is not null)
                {
                    return diskImage;
                }
            }
        }
        catch
        {
        }

        // 2. Check bundled known icons for uninstalled or legacy applications
        var knownIcon = LoadKnownBundledIcon(processName, appName);
        if (knownIcon is not null)
        {
            SaveIconToDiskCache(cacheKey, knownIcon);
            return knownIcon;
        }

        // 3. Resolve executable path on disk
        var resolvedPath = ResolveExecutablePath(processName, executablePath, appName);
        if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
        {
            try
            {
                System.Drawing.Icon? icon = null;
                if (resolvedPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    icon = new System.Drawing.Icon(resolvedPath, 32, 32);
                }
                else
                {
                    icon = System.Drawing.Icon.ExtractAssociatedIcon(resolvedPath);
                }

                if (icon is not null)
                {
                    using (icon)
                    {
                        ImageSource? image = null;
                        Dispatcher.Invoke(() =>
                        {
                            var bs = Imaging.CreateBitmapSourceFromHIcon(
                                icon.Handle,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromWidthAndHeight(20, 20));
                            bs.Freeze();
                            image = bs;
                        });

                        if (image is not null)
                        {
                            SaveIconToDiskCache(cacheKey, image);
                            return image;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static ImageSource? LoadKnownBundledIcon(string processName, string? appName)
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            string? candidateFile = null;

            var procLower = processName.ToLowerInvariant();
            var appLower = (appName ?? string.Empty).ToLowerInvariant();

            if (procLower.Contains("todesk") || appLower.Contains("todesk"))
            {
                candidateFile = Path.Combine(appDir, "Assets", "KnownIcons", "todesk.png");
            }
            else if (procLower.Contains("awesun") || appLower.Contains("向日葵") || appLower.Contains("awesun"))
            {
                candidateFile = Path.Combine(appDir, "Assets", "KnownIcons", "awesun.png");
            }
            else if (procLower.Contains("clash") && (procLower.Contains("windows") || !procLower.Contains("verge")))
            {
                candidateFile = Path.Combine(appDir, "Assets", "KnownIcons", "clash.png");
            }

            if (!string.IsNullOrWhiteSpace(candidateFile) && File.Exists(candidateFile))
            {
                return LoadBitmapFromPath(candidateFile);
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsRestrictedSearchDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return true;
        }

        var clean = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (clean.Length <= 3)
        {
            return true;
        }

        var restricted = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var r in restricted)
        {
            if (!string.IsNullOrWhiteSpace(r) &&
                string.Equals(clean, r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record UninstallEntry(string DisplayName, string KeyName, string? DisplayIcon, string? InstallLocation);

    private static readonly Lazy<List<UninstallEntry>> _uninstallIndex = new(() =>
    {
        var list = new List<UninstallEntry>();
        var keys = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (root, subKey) in keys)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                if (key is null) continue;

                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = key.OpenSubKey(name);
                        if (appKey is null) continue;

                        var dispName = appKey.GetValue("DisplayName") as string ?? string.Empty;
                        var iconPath = appKey.GetValue("DisplayIcon") as string;
                        var installLoc = appKey.GetValue("InstallLocation") as string;
                        list.Add(new UninstallEntry(dispName, name, iconPath, installLoc));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
        return list;
    });

    private static readonly Lazy<List<(string Name, string Path)>> _startMenuIndex = new(() =>
    {
        var list = new List<(string Name, string Path)>();
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    list.Add((name, lnk));
                }
            }
            catch
            {
            }
        }
        return list;
    });

    private static readonly Lazy<List<(string PkgName, string RootFolder)>> _uwpPackagesIndex = new(() =>
    {
        var list = new List<(string PkgName, string RootFolder)>();
        var keys = new[]
        {
            (Registry.CurrentUser, @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"),
            (Registry.LocalMachine, @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages")
        };

        foreach (var (root, subKey) in keys)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                if (key is null) continue;

                foreach (var pkgName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var pkgKey = key.OpenSubKey(pkgName);
                        if (pkgKey?.GetValue("PackageRootFolder") is string rootFolder && Directory.Exists(rootFolder))
                        {
                            list.Add((pkgName, rootFolder));
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
        return list;
    });

    private static string? ResolveExecutablePath(string processName, string? executablePath, string? appName)
    {
        // 1. Direct path check
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            return executablePath;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : $"{processName}.exe";

        var procLower = processName.ToLowerInvariant();
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // 2. Special process mappings
        if (procLower is "searchhost" or "shellexperiencehost" or "startmenuexperiencehost" or "shellhost" or "lockapp")
        {
            var explorerPath = Path.Combine(systemRoot, "explorer.exe");
            if (File.Exists(explorerPath))
            {
                return explorerPath;
            }
        }

        if (procLower.Contains("wechatappex"))
        {
            var wechatPath = @"C:\Program Files\Tencent\Weixin\Weixin.exe";
            if (File.Exists(wechatPath))
            {
                return wechatPath;
            }
        }

        if (procLower.Contains("clash"))
        {
            var clashVerge = @"C:\Program Files\Clash Verge\clash-verge.exe";
            if (File.Exists(clashVerge))
            {
                return clashVerge;
            }
        }

        if (procLower.Contains("qoder"))
        {
            var qoderLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Qoder", "Qoder.exe");
            if (File.Exists(qoderLocal))
            {
                return qoderLocal;
            }
        }

        // 3. Parent / Ancestor directory scanning (for updated version folders like WPS, MATLAB, RadiumWMPF)
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                var current = Path.GetDirectoryName(executablePath);
                for (var depth = 0; depth < 3 && !string.IsNullOrWhiteSpace(current); depth++)
                {
                    current = Path.GetDirectoryName(current);
                    if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current) && !IsRestrictedSearchDirectory(current))
                    {
                        var match = Directory.EnumerateFiles(current, exeName, new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            MaxRecursionDepth = 2,
                            IgnoreInaccessible = true
                        }).FirstOrDefault();

                        if (!string.IsNullOrWhiteSpace(match) && File.Exists(match))
                        {
                            return match;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        // 4. Registry App Paths (e.g. wps.exe, chrome.exe)
        var appPath = QueryRegistryAppPath(exeName);
        if (!string.IsNullOrWhiteSpace(appPath) && File.Exists(appPath))
        {
            return appPath;
        }

        // 5. UWP / AppX Package Repository in Registry (Photos, Media Player, Phone Link, Armoury Crate, Codex)
        var uwpPath = QueryUwpPackagePath(processName, executablePath, appName);
        if (!string.IsNullOrWhiteSpace(uwpPath) && File.Exists(uwpPath))
        {
            return uwpPath;
        }

        // 6. WindowsApps execution alias
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsAppAlias = Path.Combine(localAppData, "Microsoft", "WindowsApps", exeName);
        if (File.Exists(windowsAppAlias))
        {
            return windowsAppAlias;
        }

        // 7. System32 / Windows standard directories
        var system32Exe = Path.Combine(systemRoot, "System32", exeName);
        if (File.Exists(system32Exe))
        {
            return system32Exe;
        }

        var winExe = Path.Combine(systemRoot, exeName);
        if (File.Exists(winExe))
        {
            return winExe;
        }

        // 8. Registry Uninstall entries (MATLAB, Qoder, etc.)
        var uninstallIcon = QueryRegistryUninstallIcon(processName, appName);
        if (!string.IsNullOrWhiteSpace(uninstallIcon) && File.Exists(uninstallIcon))
        {
            return uninstallIcon;
        }

        // 9. Start Menu Shortcuts
        var shortcutTarget = QueryStartMenuShortcut(processName, appName);
        if (!string.IsNullOrWhiteSpace(shortcutTarget) && File.Exists(shortcutTarget))
        {
            return shortcutTarget;
        }

        return null;
    }

    private static string? QueryRegistryAppPath(string exeName)
    {
        var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
        foreach (var root in roots)
        {
            try
            {
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                if (key?.GetValue(string.Empty) is string path)
                {
                    path = path.Trim('"');
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static string? QueryUwpPackagePath(string processName, string? executablePath, string? appName)
    {
        try
        {
            string? packagePrefix = null;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var match = System.Text.RegularExpressions.Regex.Match(executablePath, @"WindowsApps\\([^\._]+)");
                if (match.Success)
                {
                    packagePrefix = match.Groups[1].Value;
                }
            }

            foreach (var (pkgName, rootFolder) in _uwpPackagesIndex.Value)
            {
                var matchesPrefix = packagePrefix != null && pkgName.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase);
                var matchesProc = pkgName.Contains(processName, StringComparison.OrdinalIgnoreCase);
                var matchesApp = !string.IsNullOrWhiteSpace(appName) && pkgName.Contains(appName, StringComparison.OrdinalIgnoreCase);

                if (matchesPrefix || matchesProc || matchesApp)
                {
                    var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? processName
                        : $"{processName}.exe";

                    var candidate = Path.Combine(rootFolder, exeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    var appCandidate = Path.Combine(rootFolder, "app", exeName);
                    if (File.Exists(appCandidate))
                    {
                        return appCandidate;
                    }

                    foreach (var file in Directory.EnumerateFiles(rootFolder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        if (file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            return file;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? QueryRegistryUninstallIcon(string processName, string? appName)
    {
        var searchTerms = new List<string> { processName };
        if (!string.IsNullOrWhiteSpace(appName))
        {
            searchTerms.Add(appName);
        }

        foreach (var entry in _uninstallIndex.Value)
        {
            var combined = $"{entry.DisplayName} {entry.KeyName}";
            if (!searchTerms.Any(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.DisplayIcon))
            {
                var clean = entry.DisplayIcon.Split(',')[0].Trim('"');
                if (File.Exists(clean))
                {
                    return clean;
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
            {
                var cleanLoc = entry.InstallLocation.Trim('"');
                if (Directory.Exists(cleanLoc))
                {
                    var exe = Path.Combine(cleanLoc, $"{processName}.exe");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }

                    var win64Exe = Path.Combine(cleanLoc, "bin", "win64", $"{processName}.exe");
                    if (File.Exists(win64Exe))
                    {
                        return win64Exe;
                    }

                    var binExe = Path.Combine(cleanLoc, "bin", $"{processName}.exe");
                    if (File.Exists(binExe))
                    {
                        return binExe;
                    }
                }
            }
        }

        return null;
    }

    private static string? QueryStartMenuShortcut(string processName, string? appName)
    {
        var searchTerms = new List<string> { processName };
        if (!string.IsNullOrWhiteSpace(appName))
        {
            searchTerms.Add(appName);
        }

        foreach (var (name, path) in _startMenuIndex.Value)
        {
            if (searchTerms.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private void SaveIconToDiskCache(string cacheKey, ImageSource image)
    {
        try
        {
            if (!Directory.Exists(_iconCacheDirectory))
            {
                Directory.CreateDirectory(_iconCacheDirectory);
            }

            var filePath = Path.Combine(_iconCacheDirectory, $"{SanitizeFileName(cacheKey)}.png");
            if (File.Exists(filePath))
            {
                return;
            }

            if (image is BitmapSource bitmapSource)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                using var stream = File.Create(filePath);
                encoder.Save(stream);
            }
        }
        catch
        {
        }
    }

    private static BitmapImage? LoadBitmapFromPath(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).ToLowerInvariant();
    }

    private static readonly Lazy<ImageSource> _genericApplicationIcon = new(() =>
    {
        using var icon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        var image = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(20, 20));
        image.Freeze();
        return image;
    });

    private static ImageSource GenericApplicationIcon => _genericApplicationIcon.Value;

    public sealed class UsageDisplayRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string AppName { get; }
        public string ProcessName { get; }
        public string? ExecutablePath { get; }
        public string DurationText { get; }
        public double DurationSeconds { get; }
        public string ShareText { get; }
        public double ShareRatio { get; }

        private ImageSource _icon;
        public ImageSource Icon
        {
            get => _icon;
            set
            {
                if (!ReferenceEquals(_icon, value))
                {
                    _icon = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
                }
            }
        }

        public UsageDisplayRow(
            string appName,
            string processName,
            string? executablePath,
            ImageSource icon,
            string durationText,
            double durationSeconds,
            string shareText,
            double shareRatio)
        {
            AppName = appName;
            ProcessName = processName;
            ExecutablePath = executablePath;
            _icon = icon;
            DurationText = durationText;
            DurationSeconds = durationSeconds;
            ShareText = shareText;
            ShareRatio = shareRatio;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public bool Equals(UsageDisplayRow? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return AppName == other.AppName &&
                   ProcessName == other.ProcessName &&
                   DurationSeconds.Equals(other.DurationSeconds) &&
                   ShareRatio.Equals(other.ShareRatio);
        }

        public override bool Equals(object? obj) => Equals(obj as UsageDisplayRow);

        public override int GetHashCode() => HashCode.Combine(AppName, ProcessName, DurationSeconds, ShareRatio);
    }

    public sealed class DeviceFilterItem(string id, string name) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Id { get; } = id;

        public string Name { get; } = name;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
