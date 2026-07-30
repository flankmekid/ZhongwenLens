using System.Runtime.InteropServices;

namespace ZhongwenLens.Core.Capture;

internal static partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Shcore = "shcore.dll";

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    /// <summary>MONITORINFOF_PRIMARY.</summary>
    public const uint MonitorInfoPrimary = 1;

    /// <summary>MDT_EFFECTIVE_DPI — the scaling the user actually sees.</summary>
    public const int MdtEffectiveDpi = 0;

    [LibraryImport(User32)]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(
        nint hdc, nint clipRect, MonitorEnumProc callback, nint data);

    public delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [LibraryImport(User32, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    /// <summary>
    /// Per-monitor DPI. Unavailable before Windows 8.1, so callers fall back to the system DPI.
    /// </summary>
    [LibraryImport(Shcore)]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Length of MONITORINFOEX's szDevice field, in characters.</summary>
    public const int DeviceNameLength = 32;

    /// <remarks>
    /// szDevice is an inline array rather than a <c>ByValTStr</c> string: a fixed-size string
    /// field makes the struct non-blittable, which source-generated P/Invoke rejects outright
    /// (SYSLIB1051). An inline array keeps the layout identical to the native struct while
    /// staying blittable, so no marshalling is needed at all.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        public DeviceNameBuffer DeviceName;

        /// <summary>Reads szDevice up to its null terminator.</summary>
        public string GetDeviceName()
        {
            ReadOnlySpan<ushort> raw = DeviceName;
            var span = MemoryMarshal.Cast<ushort, char>(raw);
            var terminator = span.IndexOf('\0');
            return new string(terminator < 0 ? span : span[..terminator]);
        }
    }

    /// <remarks>
    /// Elements are <c>ushort</c>, not <c>char</c>: <c>char</c> is subject to runtime
    /// marshalling rules (it can be marshalled as ANSI), which would force
    /// <c>DisableRuntimeMarshalling</c> on the entire assembly. <c>ushort</c> is
    /// unconditionally blittable and is reinterpreted as UTF-16 on read, which keeps the
    /// workaround local to this one struct.
    /// </remarks>
    [System.Runtime.CompilerServices.InlineArray(DeviceNameLength)]
    public struct DeviceNameBuffer
    {
        private ushort _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly System.Drawing.Rectangle ToRectangle()
            => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
}
