using System.Drawing;
using System.Drawing.Imaging;

namespace ZhongwenLens.Core.Capture;

/// <summary>
/// Desktop capture via GDI BitBlt.
/// </summary>
/// <remarks>
/// <para>
/// Fast, synchronous, no dependencies, and correct for ordinary windowed applications —
/// browsers, PDF readers, document apps and image viewers. It reads the composited desktop, so
/// it handles overlapping windows and per-monitor scaling without special cases.
/// </para>
/// <para>
/// What it cannot see: exclusive-fullscreen games, some hardware video overlay planes, and
/// DRM-protected surfaces, all of which come back black. The first two need the Direct3D
/// backend; DRM stays black under every capture API by design (DESIGN.md §6).
/// </para>
/// </remarks>
public sealed class GdiScreenCapture : IScreenCapture
{
    public string Name => "GDI";

    public bool IsAvailable => true;

    public CapturedDesktop Capture()
    {
        var desktop = MonitorEnumerator.Enumerate();
        var bounds = desktop.Bounds;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                $"virtual desktop has no area ({bounds.Width}x{bounds.Height})");
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);

            // Source coordinates are virtual-desktop space and are routinely negative; the
            // destination is always (0,0) because the bitmap starts at the desktop's top-left
            // corner, wherever that happens to be.
            //
            // Plain SourceCopy, without CAPTUREBLT: CopyFromScreen validates its argument
            // against the CopyPixelOperation enum and rejects the OR'd flag outright. It isn't
            // needed on Windows 11 regardless — DWM composites layered windows into the screen,
            // so a blit from the screen DC already includes tooltips and dropdowns, and
            // CAPTUREBLT is known to cause visible flicker on some systems.
            graphics.CopyFromScreen(
                bounds.X, bounds.Y,
                0, 0,
                new Size(bounds.Width, bounds.Height),
                CopyPixelOperation.SourceCopy);

            return new CapturedDesktop(bitmap, desktop);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        // Nothing retained between captures.
    }
}
