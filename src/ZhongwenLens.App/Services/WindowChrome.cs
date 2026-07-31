using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ZhongwenLens.App.Services;

/// <summary>
/// Applies the app icon to a window's title bar and taskbar entry.
/// </summary>
/// <remarks>
/// <c>ApplicationIcon</c> in the project file only sets the icon embedded in the .exe, which
/// Explorer and the taskbar launcher use. WinUI 3 does not carry it through to windows, so every
/// window shows the generic default until <c>AppWindow.SetIcon</c> is called explicitly.
/// </remarks>
internal static class WindowChrome
{
    private static readonly string IconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    public static void ApplyIcon(Window window)
    {
        if (!File.Exists(IconPath)) return;

        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            AppWindow.GetFromWindowId(windowId).SetIcon(IconPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // A missing or malformed icon must never stop a window opening.
            Log.Error("window icon", ex);
        }
    }
}
