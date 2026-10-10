using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SoftTrace.App;

public partial class MainWindow
{
    private const string UsageColumnRatiosSettingKey = "usage_column_ratios";
    private UsageColumnResizeState? _usageColumnResize;

    private sealed record UsageColumnResizeState(
        DataGridColumn[] Columns,
        DataGridLength[] OriginalWidths,
        double[] Widths,
        int Boundary);

    private async Task RestoreUsageColumnRatiosAsync()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            // Only restore the four-column ratio setting, never legacy pixel widths.
            var values = (await _store.GetSettingAsync(UsageColumnRatiosSettingKey))?.Split(';');
            if (values?.Length != UsageDataGrid.Columns.Count)
            {
                return;
            }

            var ratios = new double[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out ratios[i]) ||
                    !double.IsFinite(ratios[i]) || ratios[i] <= 0)
                {
                    return;
                }
            }
            var total = ratios.Sum();
            if (!double.IsFinite(total))
            {
                return;
            }
            for (var i = 0; i < ratios.Length; i++)
            {
                UsageDataGrid.Columns[i].Width = new DataGridLength(ratios[i] / total * 100, DataGridLengthUnitType.Star);
            }
        }
        catch
        {
            // Unavailable preferences must not prevent the application from opening.
        }
    }

    private void UsageColumnResize_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb ||
            FindVisualAncestor<DataGridColumnHeader>(thumb) is not { Column: not null } header)
        {
            return;
        }

        var columns = UsageDataGrid.Columns.OrderBy(column => column.DisplayIndex).ToArray();
        var boundary = header.Column.DisplayIndex - (Equals(thumb.Tag, "Left") ? 1 : 0);
        if (boundary < 0 || boundary >= columns.Length - 1)
        {
            return;
        }

        var widths = columns.Select(column => column.ActualWidth).ToArray();
        _usageColumnResize = new UsageColumnResizeState(
            columns, columns.Select(column => column.Width).ToArray(), widths, boundary);
        // Freeze the other columns for the drag; only the adjacent pair will change.
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i].Width = new DataGridLength(widths[i]);
        }
        e.Handled = true;
    }

    private void UsageColumnResize_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_usageColumnResize is not { } state)
        {
            return;
        }

        var i = state.Boundary;
        var left = state.Columns[i];
        var right = state.Columns[i + 1];
        var total = state.Widths[i] + state.Widths[i + 1];
        var minimum = Math.Max(left.MinWidth, total - right.MaxWidth);
        var maximum = Math.Min(left.MaxWidth, total - right.MinWidth);
        state.Widths[i] = Math.Clamp(state.Widths[i] + e.HorizontalChange, minimum, maximum);
        state.Widths[i + 1] = total - state.Widths[i];
        left.Width = new DataGridLength(state.Widths[i]);
        right.Width = new DataGridLength(state.Widths[i + 1]);
        e.Handled = true;
    }

    private async void UsageColumnResize_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_usageColumnResize is not { } state)
        {
            return;
        }
        _usageColumnResize = null;
        e.Handled = true;
        var total = state.Widths.Sum();
        for (var i = 0; i < state.Columns.Length; i++)
        {
            state.Columns[i].Width = e.Canceled
                ? state.OriginalWidths[i]
                : new DataGridLength(state.Widths[i] / total * 100, DataGridLengthUnitType.Star);
        }

        if (e.Canceled || _store is null)
        {
            return;
        }
        try
        {
            var savedRatios = string.Join(";", UsageDataGrid.Columns.Select(column =>
                column.Width.Value.ToString("R", CultureInfo.InvariantCulture)));
            await _store.SetSettingAsync(UsageColumnRatiosSettingKey, savedRatios);
        }
        catch
        {
            // Resizing remains usable if the local preference cannot be saved.
        }
    }
}
