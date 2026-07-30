using System.Drawing;

namespace ZhongwenLens.Core.Capture;

/// <summary>Reads the current monitor layout from Windows.</summary>
public static class MonitorEnumerator
{
    /// <summary>
    /// Enumerates monitors and the virtual desktop that contains them.
    /// </summary>
    /// <remarks>
    /// The virtual bounds come from <c>GetSystemMetrics</c> rather than from unioning the
    /// monitor rectangles: Windows is authoritative about the virtual screen, and the two can
    /// disagree at the edges. The union is still computed as a cross-check, and the larger of
    /// the two wins so that no part of the desktop is ever outside the captured bitmap.
    /// </remarks>
    public static VirtualDesktop Enumerate()
    {
        var monitors = new List<MonitorInfo>();

        NativeMethods.EnumDisplayMonitors(0, 0, Callback, 0);

        bool Callback(nint monitor, nint hdc, nint rect, nint data)
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return true;

            monitors.Add(new MonitorInfo(
                DeviceName: info.GetDeviceName(),
                Bounds: info.Monitor.ToRectangle(),
                WorkArea: info.WorkArea.ToRectangle(),
                IsPrimary: (info.Flags & NativeMethods.MonitorInfoPrimary) != 0,
                Dpi: ReadDpi(monitor)));

            return true;
        }

        if (monitors.Count == 0)
        {
            // No monitor should ever be missing, but a desktop with no capture target would
            // crash everything downstream. Fall back to the virtual screen as one display.
            var fallback = ReadVirtualBounds();
            monitors.Add(new MonitorInfo("\\\\.\\DISPLAY1", fallback, fallback, true, 96));
            return new VirtualDesktop(fallback, monitors);
        }

        var reported = ReadVirtualBounds();
        var union = VirtualDesktop.UnionOf(monitors);
        var bounds = reported.IsEmpty ? union : Rectangle.Union(reported, union);

        return new VirtualDesktop(bounds, monitors);
    }

    private static Rectangle ReadVirtualBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);

        return width <= 0 || height <= 0 ? Rectangle.Empty : new Rectangle(x, y, width, height);
    }

    private static int ReadDpi(nint monitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0)
            {
                return (int)dpiX;
            }
        }
        catch (DllNotFoundException)
        {
            // shcore.dll is absent before Windows 8.1.
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 96;
    }
}
