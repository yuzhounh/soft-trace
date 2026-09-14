using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoftTrace.App;

public sealed class RoundedClipBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arrangedSize = base.ArrangeOverride(finalSize);
        if (Child is not null)
        {
            var horizontalInset = BorderThickness.Left + BorderThickness.Right +
                                  Padding.Left + Padding.Right;
            var verticalInset = BorderThickness.Top + BorderThickness.Bottom +
                                Padding.Top + Padding.Bottom;
            var radius = Math.Max(
                0,
                Math.Min(
                    CornerRadius.TopLeft,
                    Math.Min(finalSize.Width - horizontalInset, finalSize.Height - verticalInset) / 2));
            Child.Clip = new RectangleGeometry(
                new Rect(
                    0,
                    0,
                    Math.Max(0, finalSize.Width - horizontalInset),
                    Math.Max(0, finalSize.Height - verticalInset)),
                radius,
                radius);
        }
        return arrangedSize;
    }
}
