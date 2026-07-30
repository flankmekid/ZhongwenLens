using System.ComponentModel;

namespace ZhongwenLens.App.Interop;

/// <summary>
/// A hidden message-only window, used as the delivery point for Win32 messages the app needs
/// but WinUI doesn't surface.
/// </summary>
/// <remarks>
/// Both the global hotkey and the tray icon require an HWND with a message loop:
/// <c>RegisterHotKey</c> posts <c>WM_HOTKEY</c> to a window, and <c>Shell_NotifyIcon</c> posts
/// its clicks to one. Neither can attach to a WinUI window, and the overlay window doesn't
/// exist between snips anyway. One message-only window owned by the app serves both.
/// </remarks>
public sealed class MessageWindow : IDisposable
{
    private readonly NativeMethods.WndProc _windowProc;
    private bool _disposed;

    public MessageWindow(string className)
    {
        // Kept in a field: the delegate is passed to native code, and if it were only a local
        // the GC would collect it and the next message would jump into freed memory.
        _windowProc = HandleMessage;

        var instance = NativeMethods.GetModuleHandle(null);
        var uniqueClassName = $"{className}_{Guid.NewGuid():N}";

        var wndClass = new NativeMethods.WndClassEx
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WndClassEx>(),
            WindowProc = _windowProc,
            Instance = instance,
            ClassName = uniqueClassName,
        };

        if (NativeMethods.RegisterClassEx(ref wndClass) == 0)
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "failed to register the message window class");
        }

        Handle = NativeMethods.CreateWindowEx(
            0, uniqueClassName, null, 0, 0, 0, 0, 0,
            NativeMethods.HwndMessage, 0, instance, 0);

        if (Handle == 0)
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "failed to create the message window");
        }
    }

    public nint Handle { get; private set; }

    /// <summary>
    /// Raised for every message. Handlers set <see cref="MessageEventArgs.Handled"/> to stop
    /// the message reaching <c>DefWindowProc</c>.
    /// </summary>
    public event EventHandler<MessageEventArgs>? MessageReceived;

    private nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        var args = new MessageEventArgs(message, wParam, lParam);

        try
        {
            MessageReceived?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            // An exception escaping into the native window procedure would tear down the
            // process with no diagnostic. Swallow it here and keep the loop alive.
            System.Diagnostics.Debug.WriteLine($"message handler threw: {ex}");
        }

        return args.Handled ? args.Result : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != 0)
        {
            NativeMethods.DestroyWindow(Handle);
            Handle = 0;
        }

        GC.KeepAlive(_windowProc);
    }
}

public sealed class MessageEventArgs(uint message, nint wParam, nint lParam) : EventArgs
{
    public uint Message { get; } = message;

    public nint WParam { get; } = wParam;

    public nint LParam { get; } = lParam;

    public bool Handled { get; set; }

    public nint Result { get; set; }
}
