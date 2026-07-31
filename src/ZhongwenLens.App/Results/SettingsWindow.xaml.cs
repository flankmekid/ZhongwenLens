using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using ZhongwenLens.App.Hotkeys;
using ZhongwenLens.App.Interop;
using ZhongwenLens.App.Services;

namespace ZhongwenLens.App.Results;

/// <summary>
/// Settings window. Every change applies immediately and is saved at once — there is no OK or
/// Cancel, because with five independent options a commit step only invites half-applied state.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly GlobalHotkeyService _hotkeys;

    /// <summary>Suppresses change handlers while the controls are being populated.</summary>
    private bool _loading;

    private bool _recording;

    public SettingsWindow(SettingsStore store, AppSettings current, GlobalHotkeyService hotkeys)
    {
        _store = store;
        _hotkeys = hotkeys;
        Current = current;

        InitializeComponent();
        Title = "Zhongwen Lens Settings";
        WindowChrome.ApplyIcon(this);

        PathText.Text = _store.FilePath;

        var appWindow = GetAppWindow();
        appWindow.Resize(new SizeInt32(620, 620));
        if (appWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = false;

        Load(current);

        // Key handling lives on the window content so a combination is caught wherever focus is.
        if (Content is UIElement content)
        {
            content.KeyDown += OnKeyDown;
            content.PreviewKeyDown += OnKeyDown;
        }
    }

    public AppSettings Current { get; private set; }

    /// <summary>Raised whenever a setting changes, so the app can apply it live.</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    private void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            HotkeyText.Text = HotkeyBinding.Describe(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
            LayoutCombo.SelectedIndex = (int)settings.PinyinLayout;
            ScriptCombo.SelectedIndex = (int)settings.Script;
            SpeakToggle.IsOn = settings.SpeakOnCapture;

            // Read from the registry, not the stored flag: the user may have switched it off in
            // Task Manager's Startup tab without the app knowing.
            StartupToggle.IsOn = StartupRegistration.IsEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply(AppSettings settings)
    {
        Current = settings;
        _store.Save(settings);
        SettingsChanged?.Invoke(this, settings);
    }

    // --- hotkey ----------------------------------------------------------------------------

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        _recording = true;
        RecordButton.Content = "Press keys...";
        ShowStatus("Recording. Press the combination you want, or Esc to cancel.", error: false);
        RecordButton.Focus(FocusState.Programmatic);
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        StopRecording();
        TryApplyHotkey(HotkeyBinding.Default.Modifiers, HotkeyBinding.Default.VirtualKey);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            StopRecording();
            ShowStatus("Cancelled - the hotkey is unchanged.", error: false);
            return;
        }

        // A modifier on its own isn't a combination yet; wait for the real key.
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;

        e.Handled = true;

        var modifiers = CurrentModifiers();
        if (modifiers == 0)
        {
            // Without a modifier the hotkey would swallow that key system-wide, everywhere.
            ShowStatus("Add Ctrl, Alt, Shift or Win - a bare key would be captured from every app.",
                error: true);
            return;
        }

        StopRecording();
        TryApplyHotkey(modifiers, (int)e.Key);
    }

    private static int CurrentModifiers()
    {
        var modifiers = 0;
        if (IsDown(VirtualKey.Control)) modifiers |= NativeMethods.ModControl;
        if (IsDown(VirtualKey.Menu)) modifiers |= NativeMethods.ModAlt;
        if (IsDown(VirtualKey.Shift)) modifiers |= NativeMethods.ModShift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= NativeMethods.ModWin;

        return modifiers;
    }

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Registers the combination and keeps it only if Windows accepts it, restoring the previous
    /// one otherwise. Without this the app could end up with no working hotkey at all.
    /// </summary>
    private void TryApplyHotkey(int modifiers, int virtualKey)
    {
        var previous = new HotkeyBinding(Current.HotkeyModifiers, Current.HotkeyVirtualKey);
        var candidate = new HotkeyBinding(modifiers, virtualKey);

        if (_hotkeys.TryRegister(candidate, out var error))
        {
            HotkeyText.Text = candidate.Display;
            Apply(Current with { HotkeyModifiers = modifiers, HotkeyVirtualKey = virtualKey });
            ShowStatus($"{candidate.Display} is now the snip hotkey.", error: false);
            return;
        }

        _hotkeys.TryRegister(previous, out _);
        ShowStatus($"{candidate.Display} is unavailable - another application already uses it. " +
                   $"Still using {previous.Display}.", error: true);
    }

    private void StopRecording()
    {
        _recording = false;
        RecordButton.Content = "Change...";
    }

    private void ShowStatus(string message, bool error)
    {
        HotkeyStatus.Text = message;
        HotkeyStatus.Visibility = Visibility.Visible;
        HotkeyStatus.Foreground = error
            ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    // --- everything else -------------------------------------------------------------------

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (!StartupRegistration.Set(StartupToggle.IsOn))
        {
            _loading = true;
            StartupToggle.IsOn = StartupRegistration.IsEnabled;
            _loading = false;
            return;
        }

        Apply(Current with { StartWithWindows = StartupToggle.IsOn });
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Apply(Current with { PinyinLayout = (PinyinLayoutMode)LayoutCombo.SelectedIndex });
    }

    private void OnScriptChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Apply(Current with { Script = (ScriptPreference)ScriptCombo.SelectedIndex });
    }

    private void OnSpeakToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(Current with { SpeakOnCapture = SpeakToggle.IsOn });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private AppWindow GetAppWindow()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
    }
}
