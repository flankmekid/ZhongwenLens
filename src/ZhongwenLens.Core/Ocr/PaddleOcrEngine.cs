using System.Drawing;
using Microsoft.ML.OnnxRuntime;

namespace ZhongwenLens.Core.Ocr;

/// <summary>
/// PP-OCRv4 pipeline: detect, orient, recognise (DESIGN.md §3.2).
/// </summary>
/// <remarks>
/// Sessions are created once and reused. Model init costs roughly half a second, which is
/// paid at app start rather than on the first snip.
/// </remarks>
public sealed class PaddleOcrEngine : IOcrEngine
{
    private readonly TextDetector _detector;
    private readonly TextRecognizer _recognizer;
    private readonly OrientationClassifier _orientation;
    private readonly OcrOptions _options;

    /// <summary>Serialises inference: ORT sessions are thread-safe, our bitmaps are not.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PaddleOcrEngine(
        TextDetector detector, TextRecognizer recognizer,
        OrientationClassifier orientation, OcrOptions options)
    {
        _detector = detector;
        _recognizer = recognizer;
        _orientation = orientation;
        _options = options;
    }

    /// <param name="modelDirectory">Directory holding det.onnx, rec.onnx, cls.onnx and ppocr_keys_v1.txt.</param>
    public static PaddleOcrEngine Load(string modelDirectory, OcrOptions? options = null)
    {
        options ??= new OcrOptions();

        var characters = CharacterDictionary.Load(Path.Combine(modelDirectory, "ppocr_keys_v1.txt"));

        // Leaving one core free keeps the UI responsive while a snip is processing, and
        // inter-op parallelism buys nothing for a single-branch graph.
        using var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        TextDetector? detector = null;
        TextRecognizer? recognizer = null;
        OrientationClassifier? orientation = null;

        try
        {
            detector = new TextDetector(
                CreateSession(modelDirectory, "det.onnx", sessionOptions), options);
            recognizer = new TextRecognizer(
                CreateSession(modelDirectory, "rec.onnx", sessionOptions), characters, options);
            orientation = new OrientationClassifier(
                CreateSession(modelDirectory, "cls.onnx", sessionOptions), options);

            return new PaddleOcrEngine(detector, recognizer, orientation, options);
        }
        catch
        {
            detector?.Dispose();
            recognizer?.Dispose();
            orientation?.Dispose();
            throw;
        }
    }

    private static InferenceSession CreateSession(string directory, string file, SessionOptions options)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"OCR model not found at '{path}'. Run scripts\\fetch-models.ps1.", path);
        }

        return new InferenceSession(path, options);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Recognize(image, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OcrResult Recognize(Bitmap image, CancellationToken cancellationToken)
    {
        if (image.Width < 2 || image.Height < 2) return OcrResult.Empty;

        var layout = LayoutProbe.Analyze(image);
        if (!layout.HasInk) return OcrResult.Empty;

        if (_options.EnableSingleLineFastPath && layout.IsSingleLine)
        {
            return RecognizeSingleLine(image, layout);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return RecognizeWithDetection(image, cancellationToken);
    }

    /// <summary>
    /// The fast path (DESIGN.md §3.2): one text band, so detection is skipped and the crop
    /// goes straight to the recogniser. Roughly 4x faster and more accurate on the tight
    /// one-or-two-character snips this app is built around, because detection tends to clip
    /// glyph edges or split a short word into two boxes at that size.
    /// </summary>
    private OcrResult RecognizeSingleLine(Bitmap image, LayoutProbeResult layout)
    {
        // Trim to the ink, with a small margin. A loose selection otherwise wastes recogniser
        // width on background and shrinks the glyphs within the fixed input height.
        var margin = Math.Max(2, layout.InkBounds.Height / 8);
        var region = Rectangle.Inflate(layout.InkBounds, margin, margin);

        using var trimmed = ImageOps.Crop(image, region);
        using var prepared = UpscaleIfSmall(trimmed);

        var recognized = RecognizeOriented(prepared);
        if (recognized.Text.Length == 0 || recognized.Confidence < _options.MinLineConfidence)
        {
            return OcrResult.Empty;
        }

        var bounds = Rectangle.Intersect(region, new Rectangle(0, 0, image.Width, image.Height));
        return new OcrResult([new OcrLine(recognized.Text, recognized.Confidence, bounds)],
            TextOrientation.Horizontal);
    }

    private OcrResult RecognizeWithDetection(Bitmap image, CancellationToken cancellationToken)
    {
        var boxes = _detector.Detect(image);
        if (boxes.Count == 0) return OcrResult.Empty;

        var orientation = InferOrientation(boxes);
        var lines = new List<OcrLine>(boxes.Count);

        foreach (var box in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (box.Bounds.Width <= 0 || box.Bounds.Height <= 0) continue;

            using var crop = ImageOps.Crop(image, box.Bounds);

            // Vertical lines are rotated into horizontal form; the recogniser only reads
            // left to right.
            using var upright = orientation == TextOrientation.Vertical
                ? ImageOps.Rotate90(crop)
                : (Bitmap)crop.Clone();
            using var prepared = UpscaleIfSmall(upright);

            var recognized = RecognizeOriented(prepared);
            if (recognized.Text.Length == 0 || recognized.Confidence < _options.MinLineConfidence) continue;

            lines.Add(new OcrLine(recognized.Text, recognized.Confidence, box.Bounds));
        }

        if (orientation == TextOrientation.Vertical)
        {
            // Vertical Chinese reads right to left by column.
            lines = lines.OrderByDescending(l => l.Bounds.Left).ToList();
        }

        return lines.Count == 0 ? OcrResult.Empty : new OcrResult(lines, orientation);
    }

    private RecognizedText RecognizeOriented(Bitmap crop)
    {
        if (!_orientation.IsUpsideDown(crop)) return _recognizer.Recognize(crop);

        using var flipped = ImageOps.Rotate180(crop);
        return _recognizer.Recognize(flipped);
    }

    /// <summary>
    /// Upscales text that's too small for the recogniser. Screen text at 12-16px is well below
    /// what the model saw in training, and bicubic upscaling is worth more accuracy here than
    /// any change of model (DESIGN.md §3.2).
    /// </summary>
    private Bitmap UpscaleIfSmall(Bitmap crop)
    {
        if (crop.Height >= _options.MinRecognitionBandHeight) return (Bitmap)crop.Clone();

        var factor = Math.Min(4, Math.Max(2, _options.MinRecognitionBandHeight / Math.Max(1, crop.Height)));
        return ImageOps.Resize(crop, crop.Width * factor, crop.Height * factor);
    }

    /// <summary>
    /// Vertical layout is inferred from box shape: a page of vertical Chinese produces boxes
    /// that are taller than they are wide. The median is used so one stray box can't flip the
    /// whole result.
    /// </summary>
    private static TextOrientation InferOrientation(IReadOnlyList<DetectedBox> boxes)
    {
        var ratios = boxes
            .Where(b => b.Bounds.Width > 0)
            .Select(b => (double)b.Bounds.Height / b.Bounds.Width)
            .Order()
            .ToList();

        if (ratios.Count == 0) return TextOrientation.Horizontal;

        // A comfortable margin above 1.0: a single character's box is roughly square, and
        // treating a one-character horizontal line as vertical would rotate it needlessly.
        return ratios[ratios.Count / 2] > 1.8 ? TextOrientation.Vertical : TextOrientation.Horizontal;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
        _orientation.Dispose();
        _gate.Dispose();
    }
}
