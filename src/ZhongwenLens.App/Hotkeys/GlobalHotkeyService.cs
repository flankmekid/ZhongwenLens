using System.ComponentModel;
using ZhongwenLens.App.Interop;

namespace ZhongwenLens.App.Hotkeys;

/// <param name="Modifiers">Combination of NativeMethods.Mod* flags.</param>
/// <param name="VirtualKey">Win32 virtual-key code.</param>
public sealed record HotkeyBinding(int Modifiers, int VirtualKey, string Display)
{
    /// <summary>Ctrl+Alt+Z — the default, chosen because Windows and most apps leave it free.</summary>
    public static HotkeyBinding Default { get; } = new(
        NativeMethods.ModControl | NativeMethods.ModAlt,
        0x5A,
        "Ctrl+Alt+Z");
}

/// <summary>
/// Registers a system-wide hotkey and raises an event when it fires.
/// </summary>
/// <remarks>
/// Registration fails if another process already owns the combination, and Windows gives no
/// way to discover which one. That failure is surfaced rather than swallowed: a hotkey that
/// silently does nothing is the worst possible outcome for an app whose entire interface is
/// one hotkey.
/// </remarks>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 1;

    private readonly MessageWindow _window;
    private bool _registered;

    public GlobalHotkeyService(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    public HotkeyBinding? Current { get; private set; }

    public event EventHandler? Pressed;

    /// <summary>Registers <paramref name="binding"/>, replacing any previous one.</summary>
    /// <exception cref="Win32Exception">The combination is already taken by another process.</exception>
    public void Register(HotkeyBinding binding)
    {
        Unregister();

        // MOD_NOREPEAT stops a held-down combination from firing a burst of snips.
        var modifiers = binding.Modifiers | NativeMethods.ModNoRepeat;

        if (!NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, modifiers, binding.VirtualKey))
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"could not register {binding.Display}: another application already owns it");
        }

        _registered = true;
        Current = binding;
    }

    /// <summary>Attempts registration, reporting failure instead of throwing.</summary>
    public bool TryRegister(HotkeyBinding binding, out string? error)
    {
        try
        {
            Register(binding);
            error = null;
            return true;
        }
        catch (Win32Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Unregister()
    {
        if (!_registered) return;

        NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    private void OnMessage(object? sender, MessageEventArgs e)
    {
        if (e.Message != NativeMethods.WmHotkey || e.WParam != HotkeyId) return;

        e.Handled = true;
        Pressed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        Unregister();
    }
}
