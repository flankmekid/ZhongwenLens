using System.Diagnostics;
using Microsoft.UI.Dispatching;
using ZhongwenLens.App.Results;
using ZhongwenLens.App.Services;
using ZhongwenLens.Core.Capture;
using ZhongwenLens.Core.Lookup;

namespace ZhongwenLens.App.Snip;

/// <summary>
/// Runs one snip end to end: freeze the desktop, select a region, recognise it, look it up,
/// and show the result (DESIGN.md §2.2).
/// </summary>
public sealed class SnipController(AppServices services, DispatcherQueue dispatcher)
{
    private int _busy;
    private ResultWindow? _result;

    /// <summary>Raised when a snip fails, so the shell can surface it rather than swallow it.</summary>
    public event EventHandler<string>? Failed;

    public async void StartSnip()
    {
        // A second hotkey press while an overlay is already up must not stack overlays.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex.Message);
            Log.Error("snip", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task RunAsync()
    {
        var stopwatch = Stopwatch.StartNew();

        // Capture before showing anything: the overlay must not appear in its own screenshot,
        // and freezing stops the target text scrolling away mid-selection.
        using var captured = services.Capture.Capture();
        var captureMs = stopwatch.Elapsed.TotalMilliseconds;

        // Log which monitors the GPU path couldn't serve — that's the first thing to check
        // when a game or video captures black.
        var fallbacks = services.Capture is DesktopDuplicationCapture duplication && duplication.FellBackTo.Count > 0
            ? $" (GDI fallback: {string.Join(", ", duplication.FellBackTo)})"
            : string.Empty;

        Log.Write(
            $"snip: captured {captured.Image.Width}x{captured.Image.Height} in {captureMs:F0}ms " +
            $"via {services.Capture.Name}{fallbacks}");

        var overlay = new SnipOverlayWindow(captured);
        var selection = await overlay.Selection;
        if (selection is null)
        {
            Log.Write("snip: cancelled");
            return;                                          // Esc, or a click without a drag
        }

        stopwatch.Restart();
        using var crop = captured.CropVirtual(selection.Value);

        var ocr = await services.Ocr.RecognizeAsync(crop);
        var ocrMs = stopwatch.Elapsed.TotalMilliseconds;

        var text = ocr.JoinedText;
        Log.Write(
            $"snip: region={selection.Value} crop={crop.Width}x{crop.Height} " +
            $"ocr={ocrMs:F0}ms conf={ocr.Confidence:F2} text='{text}'");

        var result = text.Length == 0
            ? LookupResult.Empty
            : services.Lookup.Analyze(text);

        Log.Write($"snip: tokens={result.Tokens.Count} wholeMatch={result.IsWholeMatch} pinyin='{result.Pinyin}'");

        var region = selection.Value;
        var desktop = captured.Desktop;

        if (!dispatcher.TryEnqueue(() => ShowResult(result, ocr.Confidence, region, desktop)))
        {
            Log.Write("snip: FAILED to enqueue result onto the UI thread");
        }
    }

    private void ShowResult(
        LookupResult result, float confidence,
        System.Drawing.Rectangle anchor, ZhongwenLens.Core.Capture.VirtualDesktop desktop)
    {
        try
        {
            // Reuse a single result window so repeated snips replace the previous reading
            // rather than littering the desktop with windows.
            if (_result is null)
            {
                _result = new ResultWindow(services);
                _result.Closed += (_, _) => _result = null;
            }

            _result.Show(result, confidence, anchor, desktop);
            Log.Write("snip: result window shown");
        }
        catch (Exception ex)
        {
            // An exception here happens on the dispatcher, outside the try in StartSnip, so
            // without this the window silently fails to appear.
            _result = null;
            Log.Error("show result", ex);
            Failed?.Invoke(this, ex.Message);
        }
    }
}
