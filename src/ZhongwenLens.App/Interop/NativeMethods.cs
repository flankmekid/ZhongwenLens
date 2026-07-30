using System.Runtime.InteropServices;

namespace ZhongwenLens.App.Interop;

internal static class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Shell32 = "shell32.dll";

    public const int WmDestroy = 0x0002;
    public const int WmClose = 0x0010;
    public const int WmCommand = 0x0111;
    public const int WmHotkey = 0x0312;
    public const int WmLButtonUp = 0x0202;
    public const int WmRButtonUp = 0x0205;

    /// <summary>First message id applications may define for themselves.</summary>
    public const int WmApp = 0x8000;

    /// <summary>Message the tray icon sends back to us.</summary>
    public const int WmTrayCallback = WmApp + 1;

    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>Suppresses auto-repeat while the combination is held down.</summary>
    public const int ModNoRepeat = 0x4000;

    /// <summary>Parent value that makes a window message-only: no UI, no z-order, no input.</summary>
    public static readonly nint HwndMessage = -3;

    public const int NimAdd = 0x00000000;
    public const int NimModify = 0x00000001;
    public const int NimDelete = 0x00000002;

    public const int NifMessage = 0x00000001;
    public const int NifIcon = 0x00000002;
    public const int NifTip = 0x00000004;

    public const uint TpmRightButton = 0x0002;
    public const uint TpmReturnCmd = 0x0100;

    public const uint MfString = 0x00000000;
    public const uint MfSeparator = 0x00000800;

    public const int SwpNoSize = 0x0001;
    public const int SwpNoMove = 0x0002;
    public const int SwpNoActivate = 0x0010;
    public const int SwpShowWindow = 0x0040;

    public static readonly nint HwndTopmost = -1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hwnd, int id, int modifiers, int virtualKey);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport(User32)]
    public static extern nint CreatePopupMenu();

    [DllImport(User32, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(nint menu, uint flags, nuint itemId, string? item);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport(User32)]
    public static extern int TrackPopupMenuEx(
        nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    /// <summary>
    /// Required before TrackPopupMenuEx on a tray menu, or the menu stays open after the user
    /// clicks elsewhere — a documented quirk of tray context menus.
    /// </summary>
    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int cx, int cy, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? moduleName);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint icon);

    public const int SwHide = 0;

    /// <summary>SM_CXSMICON — the small-icon size the shell uses, which scales with DPI.</summary>
    public const int SmCxSmIcon = 49;

    [DllImport(User32)]
    public static extern int GetSystemMetrics(int index);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hwnd, int command);
}
