using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZhongwenLens.App.Interop;

namespace ZhongwenLens.App.Tray;

/// <summary>
/// System tray presence: left-click snips, right-click opens a menu.
/// </summary>
/// <remarks>
/// The app is tray-resident so the OCR sessions and dictionary stay warm — a snip costs about
/// 30 ms that way, against roughly a second if the models had to load first.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const uint CommandSnip = 1;
    private const uint CommandSavedWords = 2;
    private const uint CommandSettings = 3;
    private const uint CommandExit = 4;

    private readonly MessageWindow _window;
    private nint _iconHandle;
    private bool _added;
    private bool _disposed;

    public TrayIcon(MessageWindow window, string tooltip)
    {
        _window = window;
        _window.MessageReceived += OnMessage;

        _iconHandle = CreateIcon();

        var data = CreateData();
        data.Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip;
        data.CallbackMessage = NativeMethods.WmTrayCallback;
        data.Icon = _iconHandle;
        data.Tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data);
    }

    public event EventHandler? SnipRequested;

    public event EventHandler? SavedWordsRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    /// <summary>Shortcut shown beside Snip in the menu. Follows the user's rebinding.</summary>
    public string HotkeyLabel { get; set; } = "Ctrl+Alt+Z";

    /// <summary>Updates the hover tooltip, used to show hotkey registration problems.</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added) return;

        var data = CreateData();
        data.Flags = NativeMethods.NifTip;
        data.Tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref data);
    }

    private NativeMethods.NotifyIconData CreateData() => new()
    {
        Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = _window.Handle,
        Id = IconId,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    /// <summary>
    /// Loads the app icon at the size Windows wants for the notification area, falling back to
    /// a drawn glyph if the asset is missing.
    /// </summary>
    /// <remarks>
    /// The size matters: handing Windows a 256px icon leaves it to downscale, and the result is
    /// visibly softer than the 16px variant the .ico already contains. <c>SM_CXSMICON</c> is the
    /// size the shell actually asks for, and it changes with display scaling.
    /// </remarks>
    private static nint CreateIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(path))
        {
            try
            {
                var size = NativeMethods.GetSystemMetrics(NativeMethods.SmCxSmIcon);
                if (size <= 0) size = 16;

                using var icon = new Icon(path, size, size);

                // Clone the handle: disposing the Icon destroys the one it owns, and the tray
                // would then be left pointing at freed memory.
                return icon.ToBitmap().GetHicon();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                // Corrupt or unreadable asset: fall through to the drawn glyph.
            }
        }

        return DrawFallbackIcon();
    }

    /// <summary>Draws a plain 中 glyph, used only when the icon asset can't be loaded.</summary>
    private static nint DrawFallbackIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(220, 30, 90, 200));
            graphics.FillEllipse(background, 0, 0, 31, 31);

            using var font = new Font("Microsoft YaHei", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            graphics.DrawString("中", font, foreground, new RectangleF(0, 0, 32, 32), format);
        }

        return bitmap.GetHicon();
    }

    private void OnMessage(object? sender, MessageEventArgs e)
    {
        if (e.Message == NativeMethods.WmCommand)
        {
            switch ((uint)(e.WParam & 0xFFFF))
            {
                case CommandSnip:
                    e.Handled = true;
                    SnipRequested?.Invoke(this, EventArgs.Empty);
                    return;
                case CommandSavedWords:
                    e.Handled = true;
                    SavedWordsRequested?.Invoke(this, EventArgs.Empty);
                    return;
                case CommandSettings:
                    e.Handled = true;
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                    return;
                case CommandExit:
                    e.Handled = true;
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    return;
                default:
                    return;
            }
        }

        if (e.Message != NativeMethods.WmTrayCallback) return;

        switch ((int)(e.LParam & 0xFFFF))
        {
            case NativeMethods.WmLButtonUp:
                e.Handled = true;
                SnipRequested?.Invoke(this, EventArgs.Empty);
                break;
            case NativeMethods.WmRButtonUp:
                e.Handled = true;
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, CommandSnip, $"Snip\t{HotkeyLabel}");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, CommandSavedWords, "Saved words...");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, CommandSettings, "Settings...");
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, CommandExit, "Exit");

            NativeMethods.GetCursorPos(out var cursor);

            // Without this the menu refuses to dismiss when the user clicks away — a
            // long-documented quirk of menus owned by a tray icon.
            NativeMethods.SetForegroundWindow(_window.Handle);

            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCmd,
                cursor.X, cursor.Y, _window.Handle, 0);

            switch ((uint)command)
            {
                case CommandSnip: SnipRequested?.Invoke(this, EventArgs.Empty); break;
                case CommandSavedWords: SavedWordsRequested?.Invoke(this, EventArgs.Empty); break;
                case CommandSettings: SettingsRequested?.Invoke(this, EventArgs.Empty); break;
                case CommandExit: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.MessageReceived -= OnMessage;

        if (_added)
        {
            var data = CreateData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            _added = false;
        }

        if (_iconHandle != 0)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }
}
