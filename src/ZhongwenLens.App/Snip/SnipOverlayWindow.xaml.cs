using System.Drawing;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using ZhongwenLens.App.Interop;
using ZhongwenLens.Core.Capture;

namespace ZhongwenLens.App.Snip;

/// <summary>
/// Full-desktop region selector shown over a frozen screenshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordinates.</b> The selection is tracked in physical virtual-desktop pixels read from
/// <c>GetCursorPos</c>, and converted to DIPs only for drawing. Doing it the other way — taking
/// pointer positions in DIPs and scaling up — needs the right scale factor for whichever
/// monitor the cursor is currently over, and gets it wrong the moment the window spans two
/// monitors at different DPI. <c>GetCursorPos</c> is already in the same space as the captured
/// bitmap, so the selection needs no conversion at all to be usable.
/// </para>
/// <para>
/// The window is a plain opaque window showing a static image, not a transparent one. That is
/// deliberate: WinUI 3 handles per-pixel-alpha windows poorly, and freezing the desktop also
/// stops the text moving while it's being selected.
/// </para>
/// </remarks>
public sealed partial class SnipOverlayWindow : Window
{
    /// <summary>Drags smaller than this are treated as a misclick and cancel the snip.</summary>
    private const int MinimumSelectionPixels = 8;

    private const double LoupeSize = 132;
    private const double LoupeZoom = 6.0;

    private readonly CapturedDesktop _captured;
    private readonly TaskCompletionSource<Rectangle?> _completion = new();

    private double _scale = 1.0;
    private bool _dragging;
    private System.Drawing.Point _anchor;
    private System.Drawing.Point _cursor;
    private bool _closed;

    public SnipOverlayWindow(CapturedDesktop captured)
    {
        _captured = captured;
        InitializeComponent();

        ConfigurePresentation();
        Closed += (_, _) => Complete(null);
    }

    /// <summary>
    /// Completes with the selected region in virtual-desktop coordinates, or null if cancelled.
    /// </summary>
    public Task<Rectangle?> Selection => _completion.Task;

    private void ConfigurePresentation()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // Not a real window as far as the user is concerned; it shouldn't appear in Alt+Tab.
        appWindow.IsShownInSwitchers = false;

        // AppWindow works in physical pixels, the same space as the virtual desktop bounds,
        // so this needs no scaling.
        var bounds = _captured.Desktop.Bounds;
        appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));

        Activate();
        NativeMethods.SetForegroundWindow(handle);

        Root.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        if (_scale <= 0) _scale = 1.0;

        var source = CreateImageSource(_captured.Image);
        FrozenImage.Source = source;

        // The loupe shows the same image at the same DIP dimensions, so a coordinate in the
        // main image means the same thing inside the loupe.
        LoupeImage.Source = source;
        LoupeImage.Width = _captured.Image.Width / _scale;
        LoupeImage.Height = _captured.Image.Height / _scale;
        LoupeViewport.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, LoupeSize - 2, LoupeSize - 2),
        };

        Root.Focus(FocusState.Programmatic);

        NativeMethods.GetCursorPos(out var cursor);
        _cursor = new System.Drawing.Point(cursor.X, cursor.Y);

        UpdateVisuals();
    }

    /// <summary>
    /// Copies the captured bitmap into a WinUI image source.
    /// </summary>
    /// <remarks>
    /// A raw pixel copy into a <see cref="WriteableBitmap"/>, not an encode-then-decode.
    /// PNG-encoding a 4480x1440 desktop would cost several hundred milliseconds on the one path
    /// where latency is most visible; a straight BGRA memcpy of the same image is around 20 ms.
    /// </remarks>
    private static WriteableBitmap CreateImageSource(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;

        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        try
        {
            var writeable = new WriteableBitmap(width, height);
            var bytes = new byte[width * height * 4];

            // Row by row: the source stride can exceed width*4 because of row padding, so a
            // single block copy would shear the image.
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * data.Stride), bytes, y * width * 4, width * 4);
            }

            using var stream = writeable.PixelBuffer.AsStream();
            stream.Write(bytes, 0, bytes.Length);

            writeable.Invalidate();
            return writeable;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NativeMethods.GetCursorPos(out var cursor);
        _anchor = new System.Drawing.Point(cursor.X, cursor.Y);
        _cursor = _anchor;
        _dragging = true;

        Root.CapturePointer(e.Pointer);
        HintPanel.Visibility = Visibility.Collapsed;
        UpdateVisuals();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Read the true cursor position rather than the event's, which is in DIPs relative to
        // an element and would need per-monitor scaling to get back to desktop pixels.
        NativeMethods.GetCursorPos(out var cursor);
        _cursor = new System.Drawing.Point(cursor.X, cursor.Y);

        UpdateVisuals();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        NativeMethods.GetCursorPos(out var cursor);
        _cursor = new System.Drawing.Point(cursor.X, cursor.Y);

        var selection = CurrentSelection();

        // A click without a drag is a misclick, not a request to OCR an 8px region.
        if (selection.Width < MinimumSelectionPixels || selection.Height < MinimumSelectionPixels)
        {
            Complete(null);
            CloseOverlay();
            return;
        }

        Complete(selection);
        CloseOverlay();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;

        e.Handled = true;
        Complete(null);
        CloseOverlay();
    }

    /// <summary>The dragged region in virtual-desktop pixels, normalised for drag direction.</summary>
    private Rectangle CurrentSelection()
    {
        var left = Math.Min(_anchor.X, _cursor.X);
        var top = Math.Min(_anchor.Y, _cursor.Y);
        var right = Math.Max(_anchor.X, _cursor.X);
        var bottom = Math.Max(_anchor.Y, _cursor.Y);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private void UpdateVisuals()
    {
        var bounds = _captured.Desktop.Bounds;
        var widthDip = bounds.Width / _scale;
        var heightDip = bounds.Height / _scale;

        if (!_dragging)
        {
            SetRect(DimTop, 0, 0, widthDip, heightDip);
            SetRect(DimBottom, 0, 0, 0, 0);
            SetRect(DimLeft, 0, 0, 0, 0);
            SetRect(DimRight, 0, 0, 0, 0);
            SelectionBorder.Visibility = Visibility.Collapsed;
            ReadoutPanel.Visibility = Visibility.Collapsed;

            PositionHint(widthDip, heightDip);
            UpdateLoupe(widthDip, heightDip);
            return;
        }

        var selection = CurrentSelection();

        // Physical desktop coordinates -> DIPs relative to this window's top-left.
        var left = (selection.Left - bounds.X) / _scale;
        var top = (selection.Top - bounds.Y) / _scale;
        var width = selection.Width / _scale;
        var height = selection.Height / _scale;

        SetRect(DimTop, 0, 0, widthDip, top);
        SetRect(DimBottom, 0, top + height, widthDip, Math.Max(0, heightDip - top - height));
        SetRect(DimLeft, 0, top, left, height);
        SetRect(DimRight, left + width, top, Math.Max(0, widthDip - left - width), height);

        SelectionBorder.Visibility = Visibility.Visible;
        SetRect(SelectionBorder, left, top, width, height);

        ReadoutPanel.Visibility = Visibility.Visible;
        Readout.Text = $"{selection.Width} × {selection.Height}";
        PositionReadout(left, top, width, height, widthDip, heightDip);

        UpdateLoupe(widthDip, heightDip);
    }

    /// <summary>
    /// Points the loupe at the cursor by scaling the whole screenshot and translating so the
    /// cursor's pixel lands at the loupe's centre. Cheaper than cropping a new bitmap on every
    /// pointer move, and it reuses the texture already uploaded for the background.
    /// </summary>
    private void UpdateLoupe(double widthDip, double heightDip)
    {
        var bounds = _captured.Desktop.Bounds;
        var cursorDipX = (_cursor.X - bounds.X) / _scale;
        var cursorDipY = (_cursor.Y - bounds.Y) / _scale;

        var centre = (LoupeSize - 2) / 2;

        LoupeImage.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform { ScaleX = LoupeZoom, ScaleY = LoupeZoom },
                new TranslateTransform
                {
                    X = centre - (cursorDipX * LoupeZoom),
                    Y = centre - (cursorDipY * LoupeZoom),
                },
            },
        };

        // Offset from the cursor, flipping to the opposite side near a screen edge so the loupe
        // is never clipped or covering the thing being selected.
        var x = cursorDipX + 24;
        var y = cursorDipY + 24;
        if (x + LoupeSize > widthDip) x = cursorDipX - LoupeSize - 24;
        if (y + LoupeSize > heightDip) y = cursorDipY - LoupeSize - 24;

        Canvas.SetLeft(Loupe, Math.Max(0, x));
        Canvas.SetTop(Loupe, Math.Max(0, y));
        Loupe.Visibility = Visibility.Visible;
    }

    private void PositionReadout(
        double left, double top, double width, double height, double widthDip, double heightDip)
    {
        // Prefer just under the selection; move inside it when there's no room below.
        var x = left;
        var y = top + height + 6;
        if (y + 28 > heightDip) y = Math.Max(0, top - 28);
        if (x + 90 > widthDip) x = Math.Max(0, widthDip - 90);

        Canvas.SetLeft(ReadoutPanel, x);
        Canvas.SetTop(ReadoutPanel, y);
    }

    private void PositionHint(double widthDip, double heightDip)
    {
        // Centred on whichever monitor the cursor is on, not on the whole virtual desktop,
        // which would put it in the middle of the seam between two screens.
        var monitor = _captured.Desktop.MonitorContaining(_cursor);
        var bounds = _captured.Desktop.Bounds;

        var centreX = (monitor.Bounds.X + (monitor.Bounds.Width / 2.0) - bounds.X) / _scale;
        var bottomY = (monitor.Bounds.Bottom - bounds.Y) / _scale;

        HintPanel.Visibility = Visibility.Visible;
        Canvas.SetLeft(HintPanel, Math.Max(0, centreX - 150));
        Canvas.SetTop(HintPanel, Math.Max(0, Math.Min(bottomY - 90, heightDip - 60)));
    }

    private static void SetRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private void Complete(Rectangle? selection) => _completion.TrySetResult(selection);

    private void CloseOverlay()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }
}
