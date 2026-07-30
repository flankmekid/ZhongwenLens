using ZhongwenLens.Core.Capture;
using ZhongwenLens.Core.Dictionary;
using ZhongwenLens.Core.Lookup;
using ZhongwenLens.Core.Ocr;
using ZhongwenLens.Core.Speech;
using ZhongwenLens.Core.Study;
using ZhongwenLens.Core.Text;

namespace ZhongwenLens.App.Services;

/// <summary>
/// Long-lived services, created once at startup and held for the process lifetime.
/// </summary>
/// <remarks>
/// This is why the app is tray-resident. Loading the ONNX sessions and the 124k-entry
/// dictionary costs roughly a second; keeping them warm makes a snip about 30 ms. Constructing
/// them per snip would make the hotkey feel broken.
/// </remarks>
public sealed class AppServices : IDisposable
{
    private AppServices(
        SqliteDictionary dictionary,
        Segmenter segmenter,
        LookupService lookup,
        PaddleOcrEngine ocr,
        IScreenCapture capture,
        ISpeechService speech,
        IStudyStore study)
    {
        Dictionary = dictionary;
        Segmenter = segmenter;
        Lookup = lookup;
        Ocr = ocr;
        Capture = capture;
        Speech = speech;
        Study = study;
    }

    public SqliteDictionary Dictionary { get; }

    public Segmenter Segmenter { get; }

    public LookupService Lookup { get; }

    public PaddleOcrEngine Ocr { get; }

    public IScreenCapture Capture { get; }

    public ISpeechService Speech { get; }

    /// <summary>The user's starred words. Their own file, never touched by DataBuild.</summary>
    public IStudyStore Study { get; }

    public static AppServices Load()
    {
        SqliteDictionary? dictionary = null;
        PaddleOcrEngine? ocr = null;
        IScreenCapture? capture = null;
        ISpeechService? speech = null;
        IStudyStore? study = null;

        try
        {
            dictionary = SqliteDictionary.Open(DataPaths.DictionaryDatabase);
            var segmenter = new Segmenter(dictionary);
            var lookup = new LookupService(dictionary, segmenter);

            ocr = PaddleOcrEngine.Load(DataPaths.ModelDirectory);

            // Desktop Duplication reads the composited GPU output, so games and
            // hardware-decoded video capture correctly where GDI returns black. It degrades
            // per monitor to GDI on its own, so there's no separate fallback to arrange here.
            capture = new DesktopDuplicationCapture();
            speech = new SapiSpeechService();
            study = SqliteStudyStore.Open(Path.Combine(DataPaths.UserDirectory, "study.db"));

            WarmUp(capture);

            return new AppServices(dictionary, segmenter, lookup, ocr, capture, speech, study);
        }
        catch
        {
            study?.Dispose();
            speech?.Dispose();
            capture?.Dispose();
            ocr?.Dispose();
            dictionary?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes and discards one capture, so the D3D device and per-output duplication are built
    /// before the user ever presses the hotkey.
    /// </summary>
    /// <remarks>
    /// Measured: the first capture costs around 480 ms and subsequent ones around 19 ms. Paying
    /// that on the first snip would put half a second between the hotkey and the overlay — on
    /// exactly the interaction the app is judged by. The app is resident precisely so this kind
    /// of cost lands at startup instead.
    /// </remarks>
    private static void WarmUp(IScreenCapture capture)
    {
        try
        {
            using var discarded = capture.Capture();
            Log.Write($"warmed {capture.Name}");
        }
        catch (Exception ex)
        {
            // Non-fatal: the first real snip will simply pay the setup cost itself.
            Log.Error("capture warm-up", ex);
        }
    }

    public void Dispose()
    {
        Study.Dispose();
        Speech.Dispose();
        Capture.Dispose();
        Ocr.Dispose();
        Dictionary.Dispose();
    }
}
