using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ZhongwenLens.App.Hotkeys;
using ZhongwenLens.App.Interop;
using ZhongwenLens.App.Services;
using ZhongwenLens.App.Snip;
using ZhongwenLens.App.Tray;

namespace ZhongwenLens.App;

/// <summary>
/// Tray-resident shell. There is no main window: the app's entire interface is a hotkey, a
/// tray icon, and the two windows a snip produces.
/// </summary>
public partial class App : Application
{
    private MessageWindow? _messageWindow;
    private GlobalHotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private AppServices? _services;
    private SnipController? _controller;
    private Window? _lifetimeAnchor;
    private Results.SavedWordsWindow? _savedWords;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!DataPaths.IsComplete)
        {
            ShowStartupError(DataPaths.DescribeMissing());
            return;
        }

        try
        {
            // Loading here rather than lazily is the whole reason the app stays resident:
            // the models and dictionary cost about a second, and a snip should not.
            _services = AppServices.Load();
        }
        catch (Exception ex)
        {
            ShowStartupError($"Failed to start:\n\n{ex.Message}");
            return;
        }

        CreateLifetimeAnchor();

        _messageWindow = new MessageWindow("ZhongwenLensMessageWindow");
        _controller = new SnipController(_services, DispatcherQueue.GetForCurrentThread());

        _hotkeys = new GlobalHotkeyService(_messageWindow);
        _hotkeys.Pressed += (_, _) => _controller.StartSnip();

        var binding = HotkeyBinding.Default;
        var registered = _hotkeys.TryRegister(binding, out var hotkeyError);

        _tray = new TrayIcon(_messageWindow, registered
            ? $"Zhongwen Lens — {binding.Display} to snip"
            : $"Zhongwen Lens — {binding.Display} unavailable, click to snip");

        _tray.SnipRequested += (_, _) => _controller.StartSnip();
        _tray.SavedWordsRequested += (_, _) => ShowSavedWords();
        _tray.ExitRequested += (_, _) => Shutdown();

        _controller.Failed += (_, message) => _tray?.SetTooltip($"Zhongwen Lens — {message}");

        if (!registered)
        {
            // A hotkey that silently does nothing is the worst failure mode for an app whose
            // entire interface is that hotkey, so say so — the tray icon still works.
            ShowStartupError(
                $"""
                 {hotkeyError}

                 Zhongwen Lens is running in the system tray and you can still snip by
                 clicking its icon. Another application is holding {binding.Display}.
                 """,
                fatal: false);
        }
    }

    /// <summary>Opens the saved-word list, reusing the window if it's already up.</summary>
    private void ShowSavedWords()
    {
        if (_services is null) return;

        if (_savedWords is null)
        {
            _savedWords = new Results.SavedWordsWindow(_services);
            _savedWords.Closed += (_, _) => _savedWords = null;
        }

        _savedWords.Activate();
        Interop.NativeMethods.SetForegroundWindow(
            WinRT.Interop.WindowNative.GetWindowHandle(_savedWords));
    }

    /// <summary>
    /// Creates a hidden window whose only job is to keep the process alive.
    /// </summary>
    /// <remarks>
    /// WinUI 3 exits as soon as its last window closes. For a tray-resident app that is fatal:
    /// pressing Esc on the overlay closed the only open window and terminated the process, so
    /// the hotkey worked exactly once. This window is never shown and never closed until
    /// shutdown, so window count never reaches zero. It is hidden at the Win32 level rather
    /// than left unactivated, which avoids relying on undocumented behaviour about whether an
    /// unactivated window counts.
    /// </remarks>
    private void CreateLifetimeAnchor()
    {
        _lifetimeAnchor = new Window();

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_lifetimeAnchor);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.IsShownInSwitchers = false;
        NativeMethods.ShowWindow(handle, NativeMethods.SwHide);
    }

    private void ShowStartupError(string message, bool fatal = true)
    {
        var window = new StartupErrorWindow(message, fatal ? Shutdown : null);
        window.Activate();
    }

    private void Shutdown()
    {
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _messageWindow?.Dispose();
        _services?.Dispose();

        _lifetimeAnchor?.Close();
        _lifetimeAnchor = null;

        Exit();
    }
}
