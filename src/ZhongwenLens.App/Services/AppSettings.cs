using ZhongwenLens.App.Interop;

namespace ZhongwenLens.App.Services;

/// <summary>How pinyin is placed relative to the text in the result headline.</summary>
public enum PinyinLayoutMode
{
    /// <summary>Beside the text for short selections, above each word for long ones.</summary>
    Auto,

    /// <summary>Always beside the text, however long the selection.</summary>
    SideBySide,

    /// <summary>Always above each word, even for a single word.</summary>
    Ruby,
}

public enum ScriptPreference
{
    Simplified,
    Traditional,
}

/// <summary>
/// User-configurable options, persisted as JSON.
/// </summary>
/// <remarks>
/// A record with defaults on every property, so a settings file written by an older build (or
/// one that's been hand-edited and lost a field) still loads — missing values simply keep their
/// default rather than throwing.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Combination of NativeMethods.Mod* flags.</summary>
    public int HotkeyModifiers { get; init; } = NativeMethods.ModControl | NativeMethods.ModAlt;

    /// <summary>Win32 virtual-key code. 0x5A is Z.</summary>
    public int HotkeyVirtualKey { get; init; } = 0x5A;

    public PinyinLayoutMode PinyinLayout { get; init; } = PinyinLayoutMode.Auto;

    /// <summary>Speak the selection as soon as a snip resolves.</summary>
    public bool SpeakOnCapture { get; init; }

    /// <summary>Which script the dictionary cards lead with.</summary>
    public ScriptPreference Script { get; init; } = ScriptPreference.Simplified;

    /// <summary>
    /// Mirrors the registry Run entry rather than driving it. The registry is the source of
    /// truth — the user can remove the entry from Task Manager's Startup tab, and this would
    /// otherwise keep claiming it's enabled.
    /// </summary>
    public bool StartWithWindows { get; init; }
}
