using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DoomLauncher.WinUI;

// Native wrapping for existing toolbar controls; keeps their tab order intact.
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, rowHeight = 0, used = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { y += rowHeight + Spacing; x = 0; rowHeight = 0; }
            x += size.Width + Spacing; used = Math.Max(used, x - Spacing); rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(used, y + rowHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += rowHeight + Spacing; x = 0; rowHeight = 0; }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Spacing; rowHeight = Math.Max(rowHeight, size.Height);
        }
        return finalSize;
    }
}
