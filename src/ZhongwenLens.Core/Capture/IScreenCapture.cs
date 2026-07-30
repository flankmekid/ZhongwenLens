using System.Drawing;

namespace ZhongwenLens.Core.Capture;

/// <summary>
/// A frozen image of the whole virtual desktop, plus the geometry needed to map coordinates
/// into it.
/// </summary>
/// <remarks>
/// Pixel (0,0) of <see cref="Image"/> is virtual-desktop point
/// <c>Desktop.Bounds.Location</c> — which is negative whenever a monitor sits left of or above
/// the primary. Always convert through <see cref="VirtualDesktop.ToBitmapPoint"/> rather than
/// using virtual coordinates as pixel offsets.
/// </remarks>
public sealed class CapturedDesktop(Bitmap image, VirtualDesktop desktop) : IDisposable
{
    public Bitmap Image { get; } = image;

    public VirtualDesktop Desktop { get; } = desktop;

    /// <summary>Crops a region given in virtual-desktop coordinates.</summary>
    public Bitmap CropVirtual(Rectangle virtualRegion)
    {
        var bitmapRegion = Rectangle.Intersect(
            Desktop.ToBitmapRect(virtualRegion),
            new Rectangle(0, 0, Image.Width, Image.Height));

        if (bitmapRegion.Width <= 0 || bitmapRegion.Height <= 0)
        {
            return new Bitmap(1, 1);
        }

        var result = new Bitmap(bitmapRegion.Width, bitmapRegion.Height);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.DrawImage(Image, new Rectangle(0, 0, bitmapRegion.Width, bitmapRegion.Height),
                bitmapRegion, GraphicsUnit.Pixel);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Captures the desktop. Behind an interface because the GDI implementation can't see
/// hardware-accelerated content and a Direct3D one is needed for games and video (DESIGN.md §3.1).
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>Human-readable name of the backend, for diagnostics and settings.</summary>
    string Name { get; }

    /// <summary>Whether this backend can run on the current machine.</summary>
    bool IsAvailable { get; }

    CapturedDesktop Capture();
}
