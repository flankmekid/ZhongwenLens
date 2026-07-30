using System.Drawing;

namespace ZhongwenLens.Core.Ocr;

/// <param name="IsSingleLine">Whether the crop holds one line of text and can skip detection.</param>
/// <param name="InkBounds">Bounding box of everything that isn't background.</param>
/// <param name="BandCount">Horizontal text bands found.</param>
/// <param name="HasInk">False for a blank or uniform crop.</param>
internal sealed record LayoutProbeResult(bool IsSingleLine, Rectangle InkBounds, int BandCount, bool HasInk)
{
    public static readonly LayoutProbeResult Blank = new(false, Rectangle.Empty, 0, false);
}

/// <summary>
/// Decides whether a crop is a single line of text, without running the detector.
/// </summary>
/// <remarks>
/// This is what enables the fast path in DESIGN.md §3.2. For a tight crop of one or two
/// characters, DB detection is both slower and less accurate than skipping it — it clips
/// glyph edges and sometimes splits one word into two boxes. Since the app's primary use is
/// exactly that kind of snip (§1.4), getting this decision right matters more than the
/// detector's own quality.
///
/// The method is projection profiling: find rows containing non-background pixels, group them
/// into bands, and count the bands. One band means one line. It also returns the ink bounds,
/// which let the caller tighten a loose crop before recognising it.
/// </remarks>
internal static class LayoutProbe
{
    /// <summary>Minimum luminance difference from the background to count as ink.</summary>
    private const int MinInkDelta = 24;

    /// <summary>Ink-free rows shorter than this don't break a band. Covers antialiasing.</summary>
    private const int RawGapTolerance = 3;

    /// <summary>
    /// Below this ink height, a crop is treated as one line regardless of band count. A
    /// single character legitimately has internal horizontal gaps — 二 is two separate
    /// strokes — and two lines of text this short would be barely legible.
    /// </summary>
    private const int AlwaysSingleLineHeight = 24;

    /// <summary>Minimum band height worth checking for a hidden line boundary.</summary>
    private const int TwoLineMinimumHeight = 26;

    /// <summary>Fraction of median row density below which an interior row reads as a boundary.</summary>
    private const double ValleyDensityFraction = 0.34;


    public static LayoutProbeResult Analyze(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0) return LayoutProbeResult.Blank;

        var gray = ImageOps.ToGrayscale(bitmap);

        var histogram = new int[256];
        foreach (var value in gray) histogram[value]++;

        // The background dominates a text crop, so the median luminance is a good estimate
        // of it. This makes the probe work on light-on-dark text as well as dark-on-light,
        // which matters for dark-mode UIs and video subtitles.
        var background = Median(histogram, gray.Length);
        var (min, max) = Range(histogram);
        if (max - min < MinInkDelta) return LayoutProbeResult.Blank;

        // Scale the threshold to the actual contrast so low-contrast text still registers.
        var inkDelta = Math.Max(MinInkDelta, (max - min) / 4);

        var rowInk = new int[height];
        var rowHasInk = new bool[height];
        int inkLeft = width, inkRight = -1, inkTop = -1, inkBottom = -1;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (Math.Abs(gray[row + x] - background) < inkDelta) continue;

                rowInk[y]++;
                rowHasInk[y] = true;
                if (x < inkLeft) inkLeft = x;
                if (x > inkRight) inkRight = x;
                if (inkTop < 0) inkTop = y;
                inkBottom = y;
            }
        }

        if (inkBottom < 0) return LayoutProbeResult.Blank;

        var inkBounds = new Rectangle(inkLeft, inkTop, inkRight - inkLeft + 1, inkBottom - inkTop + 1);
        var bands = FindBands(rowHasInk, inkTop, inkBottom, RawGapTolerance);

        if (bands.Count <= 1)
        {
            // A single band by the gap test isn't conclusive. Tightly-leaded text — a code
            // editor at 18px leaves barely a pixel between lines — never drops to zero ink at
            // the boundary, so gap detection alone merges two lines into one. The density
            // profile still dips sharply there, which is what this catches.
            var split = HasDensityValley(rowInk, inkTop, inkBottom);
            return new LayoutProbeResult(!split, inkBounds, split ? 2 : 1, true);
        }

        // Several bands, but too short to be two lines of legible text — a single character
        // with internal strokes, like 二.
        if (inkBounds.Height <= AlwaysSingleLineHeight)
        {
            return new LayoutProbeResult(true, inkBounds, bands.Count, true);
        }

        // Anything taller falls through to the detector. A large character with disconnected
        // strokes — 二 at 40px — lands here and loses the fast path, costing latency but not
        // correctness, since detection reads it correctly anyway.
        //
        // An aspect-ratio test for "roughly square, therefore one glyph" was tried here and
        // removed: a two-line block of three-character lines measures 54x43px, aspect 1.26, and
        // was misread as a single glyph. Column grouping doesn't separate them reliably either,
        // because adjacent characters often touch. The asymmetry settles it — a wrong
        // multi-line verdict costs a few hundred milliseconds, a wrong single-line verdict runs
        // two lines of text together and corrupts the result.
        return new LayoutProbeResult(false, inkBounds, bands.Count, true);
    }

    /// <summary>
    /// Looks for a sharp dip in ink density inside a band, which marks a line boundary that
    /// never reached zero ink.
    /// </summary>
    /// <remarks>
    /// A line of Chinese fills its box fairly evenly, so an interior row carrying under a third
    /// of the band's typical ink is a boundary rather than a feature of the glyphs. The search
    /// skips the outer 20% of the band, where ascender and descender rows are legitimately
    /// sparse, and only applies to bands tall enough to hold two lines at all.
    /// </remarks>
    private static bool HasDensityValley(int[] rowInk, int top, int bottom)
    {
        var height = bottom - top + 1;
        if (height < TwoLineMinimumHeight) return false;

        var densities = new int[height];
        for (var i = 0; i < height; i++) densities[i] = rowInk[top + i];

        var sorted = densities.Order().ToArray();
        var median = sorted[height / 2];
        if (median <= 0) return false;

        var threshold = median * ValleyDensityFraction;
        var margin = Math.Max(2, height / 5);

        for (var i = margin; i < height - margin; i++)
        {
            if (densities[i] <= threshold) return true;
        }

        return false;
    }

    private static List<(int Start, int End)> FindBands(bool[] rowHasInk, int top, int bottom, int gapTolerance)
    {
        var bands = new List<(int Start, int End)>();
        var start = -1;
        var gap = 0;

        for (var y = top; y <= bottom; y++)
        {
            if (rowHasInk[y])
            {
                if (start < 0) start = y;
                gap = 0;
                continue;
            }

            if (start < 0) continue;

            gap++;
            if (gap <= gapTolerance) continue;

            bands.Add((start, y - gap));
            start = -1;
            gap = 0;
        }

        if (start >= 0) bands.Add((start, bottom));

        return bands;
    }

    private static int Median(int[] histogram, int total)
    {
        var half = total / 2;
        var running = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            running += histogram[value];
            if (running >= half) return value;
        }

        return 128;
    }

    private static (int Min, int Max) Range(int[] histogram)
    {
        var min = 0;
        while (min < histogram.Length && histogram[min] == 0) min++;

        var max = histogram.Length - 1;
        while (max >= 0 && histogram[max] == 0) max--;

        return min > max ? (0, 0) : (min, max);
    }
}
