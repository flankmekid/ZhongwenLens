using System.Drawing;

namespace ZhongwenLens.Core.Capture;

/// <summary>One physical display, in physical pixels.</summary>
/// <param name="Bounds">
/// Position and size on the virtual desktop. The origin can be negative: a monitor placed to
/// the left of the primary starts at a negative X, and one aligned by its centre rather than
/// its top starts at a non-zero Y.
/// </param>
/// <param name="Dpi">Effective DPI; 96 is 100% scaling.</param>
public sealed record MonitorInfo(
    string DeviceName,
    Rectangle Bounds,
    Rectangle WorkArea,
    bool IsPrimary,
    int Dpi)
{
    /// <summary>Scale factor where 1.0 is 100%.</summary>
    public double ScaleFactor => Dpi / 96.0;
}

/// <summary>
/// The full multi-monitor desktop.
/// </summary>
/// <remarks>
/// <para>
/// Every coordinate here is a physical pixel in virtual-desktop space, where the primary
/// monitor's top-left is (0,0) and other monitors sit at whatever offset Windows gives them —
/// frequently negative.
/// </para>
/// <para>
/// The conversions between virtual-desktop space and bitmap space are the single most common
/// source of "the selection is offset from where I dragged" bugs, because a captured bitmap
/// always starts at pixel (0,0) while the desktop it came from does not. They live here, as
/// named methods with tests, rather than as arithmetic scattered through the overlay.
/// </para>
/// </remarks>
public sealed record VirtualDesktop(Rectangle Bounds, IReadOnlyList<MonitorInfo> Monitors)
{
    /// <summary>Converts a virtual-desktop point into a pixel offset within a capture of it.</summary>
    public Point ToBitmapPoint(Point virtualPoint)
        => new(virtualPoint.X - Bounds.X, virtualPoint.Y - Bounds.Y);

    /// <summary>Converts a pixel offset within a capture back to a virtual-desktop point.</summary>
    public Point ToVirtualPoint(Point bitmapPoint)
        => new(bitmapPoint.X + Bounds.X, bitmapPoint.Y + Bounds.Y);

    public Rectangle ToBitmapRect(Rectangle virtualRect)
        => virtualRect with { X = virtualRect.X - Bounds.X, Y = virtualRect.Y - Bounds.Y };

    public Rectangle ToVirtualRect(Rectangle bitmapRect)
        => bitmapRect with { X = bitmapRect.X + Bounds.X, Y = bitmapRect.Y + Bounds.Y };

    /// <summary>
    /// The monitor containing a virtual-desktop point, or the nearest one if the point falls in
    /// a gap between non-aligned monitors.
    /// </summary>
    public MonitorInfo MonitorContaining(Point virtualPoint)
    {
        foreach (var monitor in Monitors)
        {
            if (monitor.Bounds.Contains(virtualPoint)) return monitor;
        }

        return Monitors
            .OrderBy(m => SquaredDistanceTo(m.Bounds, virtualPoint))
            .FirstOrDefault()
            ?? Primary;
    }

    public MonitorInfo Primary => Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors[0];

    /// <summary>True when monitors report different DPI, so scaling can't be treated as global.</summary>
    public bool HasMixedDpi => Monitors.Select(m => m.Dpi).Distinct().Count() > 1;

    private static long SquaredDistanceTo(Rectangle rectangle, Point point)
    {
        var dx = point.X < rectangle.Left ? rectangle.Left - point.X
            : point.X > rectangle.Right ? point.X - rectangle.Right : 0;
        var dy = point.Y < rectangle.Top ? rectangle.Top - point.Y
            : point.Y > rectangle.Bottom ? point.Y - rectangle.Bottom : 0;

        return ((long)dx * dx) + ((long)dy * dy);
    }

    /// <summary>Union of all monitor bounds. Used to validate what Windows reports.</summary>
    public static Rectangle UnionOf(IEnumerable<MonitorInfo> monitors)
    {
        Rectangle? union = null;
        foreach (var monitor in monitors)
        {
            union = union is null ? monitor.Bounds : Rectangle.Union(union.Value, monitor.Bounds);
        }

        return union ?? Rectangle.Empty;
    }
}
