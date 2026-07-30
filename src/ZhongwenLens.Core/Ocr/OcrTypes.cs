using System.Drawing;

namespace ZhongwenLens.Core.Ocr;

public enum TextOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>One recognised line of text.</summary>
/// <param name="Confidence">Mean per-character CTC confidence, 0..1.</param>
/// <param name="Bounds">Box in the coordinates of the bitmap that was passed in.</param>
public sealed record OcrLine(string Text, float Confidence, Rectangle Bounds);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, TextOrientation Orientation)
{
    public static readonly OcrResult Empty = new([], TextOrientation.Horizontal);

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Mean confidence across lines, weighted by text length.</summary>
    public float Confidence
    {
        get
        {
            var characters = Lines.Sum(l => l.Text.Length);
            if (characters == 0) return 0f;

            return Lines.Sum(l => l.Confidence * l.Text.Length) / characters;
        }
    }

    /// <summary>
    /// The lines joined into one string for lookup.
    /// </summary>
    /// <remarks>
    /// Chinese has no inter-word spaces, so lines are concatenated directly — a sentence
    /// wrapped across two lines must not gain a word boundary. A space is inserted only
    /// where a line ends and the next begins with Latin or digits, where the gap is real.
    /// </remarks>
    public string JoinedText
    {
        get
        {
            if (Lines.Count == 0) return string.Empty;

            var builder = new System.Text.StringBuilder();
            foreach (var line in Lines)
            {
                if (line.Text.Length == 0) continue;

                if (builder.Length > 0 && NeedsSpace(builder[^1], line.Text[0]))
                {
                    builder.Append(' ');
                }

                builder.Append(line.Text);
            }

            return builder.ToString();
        }
    }

    private static bool NeedsSpace(char previous, char next)
        => IsLatinOrDigit(previous) && IsLatinOrDigit(next);

    private static bool IsLatinOrDigit(char c)
        => char.IsAsciiLetterOrDigit(c);
}

/// <summary>Tunables for the OCR pipeline. Defaults match PaddleOCR's own inference config.</summary>
public sealed record OcrOptions
{
    /// <summary>Probability above which a pixel is considered text.</summary>
    public float BinarizationThreshold { get; init; } = 0.3f;

    /// <summary>Minimum mean probability inside a box for it to be kept.</summary>
    public float BoxThreshold { get; init; } = 0.6f;

    /// <summary>
    /// How far detected boxes are grown. DB shrinks text regions during training, so
    /// predictions must be expanded back or glyph edges get clipped.
    /// </summary>
    /// <remarks>
    /// Calibrated by sweep, not inherited from PaddleOCR's 1.5 — see
    /// UnclipCalibrationDiagnostics. At 1.5 multi-line text scored 0.87 mean similarity, with
    /// characters losing a left radical or top stroke (他 read as 也, 文 as 又). Everything from
    /// 1.8 to 3.0 scores a clean 1.00, so 2.2 sits in the middle of that plateau rather than on
    /// its edge, where a different font or size could tip back over.
    /// </remarks>
    public float UnclipRatio { get; init; } = 2.2f;

    /// <summary>Boxes smaller than this in either dimension are discarded as noise.</summary>
    public int MinBoxSize { get; init; } = 3;

    /// <summary>Longest side fed to the detector; larger inputs are scaled down.</summary>
    public int DetectionMaxSide { get; init; } = 960;

    /// <summary>Input height the recogniser expects.</summary>
    public int RecognitionHeight { get; init; } = 48;

    /// <summary>Widest recogniser input; longer crops are split.</summary>
    public int RecognitionMaxWidth { get; init; } = 1280;

    /// <summary>
    /// Text bands shorter than this are upscaled before recognition. Screen text is often
    /// 12-16px, well under what the model was trained on, and upscaling is worth more
    /// accuracy than any model change (DESIGN.md §3.2).
    /// </summary>
    public int MinRecognitionBandHeight { get; init; } = 32;

    /// <summary>Confidence the orientation classifier needs before a line is flipped.</summary>
    public float OrientationThreshold { get; init; } = 0.9f;

    /// <summary>Lines below this confidence are dropped.</summary>
    public float MinLineConfidence { get; init; } = 0.3f;

    /// <summary>
    /// Whether a crop containing a single text band skips detection entirely
    /// (DESIGN.md §3.2). This is the common case for the app's primary use.
    /// </summary>
    public bool EnableSingleLineFastPath { get; init; } = true;
}
