using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ZhongwenLens.App;

/// <summary>
/// Reports a startup problem the user can act on.
/// </summary>
/// <remarks>
/// A tray-only app has nowhere to show an error: with no main window, a failure to load the
/// dictionary or claim the hotkey would otherwise be completely invisible.
/// </remarks>
public sealed partial class StartupErrorWindow : Window
{
    private readonly Action? _onDismiss;

    public StartupErrorWindow(string message, Action? onDismiss)
    {
        _onDismiss = onDismiss;
        InitializeComponent();

        Title = "Zhongwen Lens";
        Services.WindowChrome.ApplyIcon(this);
        TitleText.Text = onDismiss is null ? "Zhongwen Lens" : "Zhongwen Lens can't start";
        MessageText.Text = message;
        DismissButton.Content = onDismiss is null ? "Close" : "Exit";

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(700, 420));
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        Close();
        _onDismiss?.Invoke();
    }
}
