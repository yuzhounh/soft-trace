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

        CurrentAppIcon.Source = GetApplicationIcon(currentApp.ProcessName, currentApp.ExecutablePath);
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
            _allUsageRows.Add(new UsageDisplayRow(
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

    private ImageSource GetApplicationIcon(string processName, string? executablePath)
    {
        var cacheKey = executablePath ?? processName;
        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
        {
            return cachedIcon;
        }

        var resolvedPath = ResolveExecutablePath(processName, executablePath);
        System.Drawing.Icon icon;
        try
        {
            icon = !string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath)
                ? System.Drawing.Icon.ExtractAssociatedIcon(resolvedPath)
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

    private static string? ResolveExecutablePath(string processName, string? executablePath)
    {
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

        // 1. Check %LOCALAPPDATA%\Microsoft\WindowsApps\<ProcessName>.exe (UWP execution aliases)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsAppAlias = Path.Combine(localAppData, "Microsoft", "WindowsApps", exeName);
        if (File.Exists(windowsAppAlias))
        {
            return windowsAppAlias;
        }

        // 2. Check %SystemRoot%\System32\<ProcessName>.exe
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32Exe = Path.Combine(systemRoot, "System32", exeName);
        if (File.Exists(system32Exe))
        {
            return system32Exe;
        }

        // 3. Special check for explorer.exe
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            var explorerExe = Path.Combine(systemRoot, "explorer.exe");
            if (File.Exists(explorerExe))
            {
                return explorerExe;
            }
        }

        // 4. Check %ProgramFiles%\WindowsApps (e.g. Notepad, Calculator)
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var windowsAppsDir = Path.Combine(programFiles, "WindowsApps");
            if (Directory.Exists(windowsAppsDir))
            {
                var candidate = Directory.EnumerateFiles(windowsAppsDir, exeName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Security or access restrictions to WindowsApps should safely fall back
        }

        return null;
    }

    public sealed record UsageDisplayRow(
        string AppName,
        string ProcessName,
        ImageSource Icon,
        string DurationText,
        double DurationSeconds,
        string ShareText,
        double ShareRatio);

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
