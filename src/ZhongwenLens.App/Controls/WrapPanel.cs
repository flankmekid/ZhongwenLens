using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ZhongwenLens.App.Controls;

/// <summary>
/// Lays children out left to right, wrapping onto a new line when the width runs out.
/// </summary>
/// <remarks>
/// WinUI 3 ships no <c>WrapPanel</c> — <c>ItemsWrapGrid</c> only works inside an items control,
/// and the Community Toolkit's version would mean a dependency for forty lines of layout. The
/// word chips in the result window need exactly this and nothing more.
/// </remarks>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var lineWidth = 0d;
        var lineHeight = 0d;
        var totalWidth = 0d;
        var totalHeight = 0d;

        // An infinite width means "measure as one line" — the caller will scroll it.
        var limit = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;

        foreach (var child in Children)
        {
            child.Measure(new Size(limit, availableSize.Height));
            var desired = child.DesiredSize;

            var advance = lineWidth == 0 ? desired.Width : lineWidth + HorizontalSpacing + desired.Width;
            if (advance > limit && lineWidth > 0)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
                continue;
            }

            lineWidth = advance;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;

            if (x > 0 && x + desired.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, desired.Width, desired.Height));

            x += desired.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return new Size(finalSize.Width, y + lineHeight);
    }
}
