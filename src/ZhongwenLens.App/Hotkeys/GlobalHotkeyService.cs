using System.ComponentModel;
using ZhongwenLens.App.Interop;

namespace ZhongwenLens.App.Hotkeys;

/// <param name="Modifiers">Combination of NativeMethods.Mod* flags.</param>
/// <param name="VirtualKey">Win32 virtual-key code.</param>
public sealed record HotkeyBinding(int Modifiers, int VirtualKey)
{
    /// <summary>Ctrl+Alt+Z — the default, chosen because Windows and most apps leave it free.</summary>
    public static HotkeyBinding Default { get; } = new(
        NativeMethods.ModControl | NativeMethods.ModAlt, 0x5A);

    /// <summary>Human-readable form, e.g. "Ctrl+Alt+Z".</summary>
    public string Display => Describe(Modifiers, VirtualKey);

    /// <summary>
    /// Builds a label from raw Win32 codes, in the order Windows writes shortcuts.
    /// </summary>
    public static string Describe(int modifiers, int virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.ModWin) != 0) parts.Add("Win");

        parts.Add(DescribeKey(virtualKey));
        return string.Join('+', parts);
    }

    private static string DescribeKey(int virtualKey) => virtualKey switch
    {
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),          // 0-9
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),          // A-Z
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",                // F1-F24
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0xC0 => "`",
        0xBD => "-",
        0xBB => "=",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBA => ";",
        0xDE => "'",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        _ => $"Key{virtualKey:X2}",
    };
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
