using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class UsageTableLayoutTests
{
    private static System.Windows.Application CreateLayoutApplication()
    {
        // Use the real styles, but never construct the app that starts capture and sync.
        using var stream = typeof(UsageTableLayoutTests).Assembly.GetManifestResourceStream("AppResources.xaml")!;
        var root = XDocument.Load(stream).Root!;
        var resources = new XElement(root.Name.Namespace + "ResourceDictionary",
            root.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)),
            root.Element(root.Name.Namespace + "Application.Resources")!.Nodes());
        return new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString())
        };
    }

    [Fact]
    public void ResizingPreservesFourColumnPercentagesAndReadableNumericContent()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
                var app = CreateLayoutApplication();
                var window = new MainWindow { DataContext = null };
                window.DataContext = window;
                var grid = (DataGrid)window.FindName("UsageDataGrid");
                var root = (FrameworkElement)window.Content;
                var longAppName = new string('长', 60);
                var longProcessName = new string('w', 80);
                for (var i = 0; i < 20; i++)
                {
                    var row = new MainWindow.UsageDisplayRow(
                        longAppName, longProcessName, null, new DrawingImage(),
                        "586 小时 32 分钟", 1, "100.0%", 1);
                    window.UsageRows.Add(row);
                }
                var percentages = new[] { 32, 26, 28, 14 };
                var typeface = new Typeface(window.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double TextWidth(string text) => new FormattedText(
                    text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 15,
                    Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip).WidthIncludingTrailingWhitespace;

                void Layout(double width)
                {
                    root.Measure(new Size(width, 800));
                    root.Arrange(new Rect(0, 0, width, 800));
                    root.UpdateLayout();
                    var frame = new DispatcherFrame();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    root.UpdateLayout();
                }

                foreach (var width in new[] { 1200, 980, 800, 600, 580, 980 })
                {
                    Layout(width);
                    var scrollViewer = Descendants<ScrollViewer>(grid).First();
                    Assert.InRange(scrollViewer.ScrollableWidth, 0, 0.5);
                    Assert.InRange(Math.Abs(grid.Columns.Sum(c => c.ActualWidth) - scrollViewer.ViewportWidth), 0, 0.5);
                    for (var i = 0; i < percentages.Length; i++)
                    {
                        Assert.True(grid.Columns[i].Width.IsStar);
                        Assert.Equal(percentages[i], grid.Columns[i].Width.Value);
                        Assert.InRange(Math.Abs(grid.Columns[i].ActualWidth - scrollViewer.ViewportWidth * percentages[i] / 100), 0, 0.5);
                    }
                    var durationText = Descendants<TextBlock>(grid).First(t => t.Text == "586 小时 32 分钟");
                    var shareText = Descendants<TextBlock>(grid).First(t => t.Text == "100.0%");
                    Assert.True(durationText.ActualWidth >= TextWidth(durationText.Text));
                    Assert.True(shareText.ActualWidth >= TextWidth(shareText.Text));
                    var appText = Descendants<TextBlock>(grid).First(t => t.Text == longAppName);
                    Assert.Equal(TextTrimming.CharacterEllipsis, appText.TextTrimming);
                    Assert.True(appText.ActualWidth < TextWidth(longAppName));
                    Assert.True(appText.ActualWidth <= grid.Columns[0].ActualWidth - 45);
                    Assert.Equal(longAppName, ((Grid)VisualTreeHelper.GetParent(appText)).ToolTip);
                    var processText = Descendants<TextBlock>(grid).First(t => t.Text == longProcessName);
                    Assert.Equal(longProcessName, processText.ToolTip);
                    Assert.Equal(TextTrimming.CharacterEllipsis, processText.TextTrimming);
                }
                // A page of shorter content retains the same proportions and widths.
                var previousWidths = grid.Columns.Select(c => c.ActualWidth).ToArray();
                window.UsageRows.Clear();
                for (var i = 0; i < 20; i++)
                {
                    window.UsageRows.Add(new MainWindow.UsageDisplayRow(
                        "Other", "other", null, new DrawingImage(), "1 秒", 1, "0.1%", 0.001));
                }
                Layout(980);
                for (var i = 0; i < percentages.Length; i++)
                {
                    Assert.InRange(Math.Abs(previousWidths[i] - grid.Columns[i].ActualWidth), 0, 0.5);
                }
                VerifyDragPersistence(window, grid, Layout);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF layout check timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void VerifyDragPersistence(MainWindow window, DataGrid grid, Action<double> layout)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"softtrace-columns-{Guid.NewGuid():N}.db");
        try
        {
            var store = new ActivityStore(databasePath);
            store.InitializeAsync().GetAwaiter().GetResult();
            store.SetSettingAsync("usage_column_widths", "900;600;20;20").GetAwaiter().GetResult();
            SetStore(window, store);
            RestoreRatios(window);
            Assert.Equal(new double[] { 32, 26, 28, 14 }, grid.Columns.Select(c => c.Width.Value).ToArray());
            Assert.True(grid.CanUserResizeColumns);
            Assert.All(grid.Columns, c => Assert.True(c.CanUserResize));
            var headers = Descendants<DataGridColumnHeader>(grid).Where(h => h.Column is not null)
                .OrderBy(h => h.Column.DisplayIndex).ToArray();
            Thumb Gripper(int column, string side) =>
                (Thumb)headers[column].Template.FindName($"{side}ColumnResizeThumb", headers[column]);
            Assert.Equal(Visibility.Collapsed, Gripper(0, "Left").Visibility);
            Assert.Equal(Visibility.Collapsed, Gripper(3, "Right").Visibility);

            for (var boundary = 0; boundary < 3; boundary++)
            {
                var thumb = Gripper(boundary, "Right");
                Assert.Equal(Cursors.SizeWE, thumb.Cursor);
                var before = grid.Columns.Select(c => c.ActualWidth).ToArray();
                thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                thumb.RaiseEvent(new DragDeltaEventArgs(25, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                layout(980);
                for (var i = 0; i < 4; i++)
                {
                    var expected = before[i] + (i == boundary ? 25 : i == boundary + 1 ? -25 : 0);
                    Assert.InRange(Math.Abs(grid.Columns[i].ActualWidth - expected), 0, 0.5);
                }
                thumb.RaiseEvent(new DragCompletedEventArgs(25, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                layout(980);
                Assert.All(grid.Columns, c => Assert.True(c.Width.IsStar));
            }

            var saved = store.GetSettingAsync("usage_column_ratios").GetAwaiter().GetResult();
            Assert.NotNull(saved);
            var ratios = saved.Split(';').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(4, ratios.Length);
            Assert.InRange(Math.Abs(ratios.Sum() - 100), 0, 0.000001);
            foreach (var width in new[] { 580, 1200, 980 })
            {
                layout(width);
                for (var i = 0; i < 4; i++)
                {
                    Assert.Equal(ratios[i], grid.Columns[i].Width.Value);
                }
                Assert.InRange(Descendants<ScrollViewer>(grid).First().ScrollableWidth, 0, 0.5);
                Assert.Equal(saved, store.GetSettingAsync("usage_column_ratios").GetAwaiter().GetResult());
            }

            // Check the left side of a boundary, clamping, and cancellation without saving.
            var canceledThumb = Gripper(3, "Left");
            canceledThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            canceledThumb.RaiseEvent(new DragDeltaEventArgs(100000, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Assert.Equal(grid.Columns[3].MinWidth, grid.Columns[3].Width.Value);
            canceledThumb.RaiseEvent(new DragCompletedEventArgs(100000, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
            layout(980);
            Assert.Equal(saved, store.GetSettingAsync("usage_column_ratios").GetAwaiter().GetResult());

            // A new window and store must recover the preference from the database.
            var reopened = new MainWindow();
            SetStore(reopened, new ActivityStore(databasePath));
            RestoreRatios(reopened);
            var reopenedGrid = (DataGrid)reopened.FindName("UsageDataGrid");
            for (var i = 0; i < 4; i++)
            {
                Assert.True(reopenedGrid.Columns[i].Width.IsStar);
                Assert.InRange(Math.Abs(ratios[i] - reopenedGrid.Columns[i].Width.Value), 0, 0.000001);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(databasePath + suffix);
            }
        }
    }

    private static void SetStore(MainWindow window, ActivityStore store) => typeof(MainWindow)
        .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, store);

    private static void RestoreRatios(MainWindow window) => ((Task)typeof(MainWindow)
        .GetMethod("RestoreUsageColumnRatiosAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, null)!).GetAwaiter().GetResult();

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
