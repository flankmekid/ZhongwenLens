using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ZhongwenLens.Core.Ocr;

internal sealed record DetectedBox(Rectangle Bounds, float Score);

/// <summary>
/// Text detection: DB (Differentiable Binarization) inference plus post-processing into boxes.
/// </summary>
/// <remarks>
/// <para>
/// The model emits a per-pixel text probability map (already sigmoid-activated). Turning that
/// into boxes means: threshold it, find connected components, take each component's bounding
/// box, and grow the box back out — DB shrinks text regions during training, so raw
/// predictions sit inside the real glyph extents and clip strokes if used as-is.
/// </para>
/// <para>
/// <b>Axis-aligned boxes, not rotated ones.</b> Full PaddleOCR fits a rotated minimum-area
/// rectangle to each contour. This app reads rendered text off a screen, which is never
/// rotated — vertical Chinese is still axis-aligned — so contour tracing and rotated-rect
/// fitting would add a large amount of geometry code for no accuracy on the target input.
/// Photographs of signage would need the rotated version; that is out of scope (DESIGN.md §6).
/// </para>
/// </remarks>
internal sealed class TextDetector(InferenceSession session, OcrOptions options) : IDisposable
{
    /// <summary>The detector downsamples by 32, so both input dimensions must be multiples of it.</summary>
    private const int SizeMultiple = 32;

    private readonly string _inputName = session.InputMetadata.Keys.First();

    public IReadOnlyList<DetectedBox> Detect(Bitmap image)
    {
        var (width, height) = TargetSize(image.Width, image.Height);

        using var resized = ImageOps.Resize(image, width, height);
        var tensor = new DenseTensor<float>(ImageOps.ToDetectionTensor(resized), [1, 3, height, width]);

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var probabilities = results[0].AsTensor<float>().ToArray();

        // Map back to the caller's coordinates. Rounding up to a multiple of 32 distorts the
        // aspect ratio slightly, so the two axes get independent scale factors.
        var scaleX = (float)image.Width / width;
        var scaleY = (float)image.Height / height;

        var boxes = ExtractBoxes(probabilities, width, height, scaleX, scaleY, image.Width, image.Height);
        return SortReadingOrder(boxes);
    }

    private (int Width, int Height) TargetSize(int width, int height)
    {
        var longest = Math.Max(width, height);
        var ratio = longest > options.DetectionMaxSide ? (float)options.DetectionMaxSide / longest : 1f;

        // Nearest, not up: rounding up distorts the aspect ratio far more, and the detector's
        // box quality degrades with it. See ImageOps.RoundToNearest.
        return (
            ImageOps.RoundToNearest((int)Math.Round(width * ratio), SizeMultiple),
            ImageOps.RoundToNearest((int)Math.Round(height * ratio), SizeMultiple));
    }

    private List<DetectedBox> ExtractBoxes(
        float[] probabilities, int width, int height,
        float scaleX, float scaleY, int sourceWidth, int sourceHeight)
    {
        var boxes = new List<DetectedBox>();
        var visited = new bool[probabilities.Length];
        var stack = new Stack<int>();

        for (var seed = 0; seed < probabilities.Length; seed++)
        {
            if (visited[seed] || probabilities[seed] < options.BinarizationThreshold) continue;

            // Flood fill this component, accumulating its extent and mean probability.
            int left = int.MaxValue, right = int.MinValue, top = int.MaxValue, bottom = int.MinValue;
            var sum = 0d;
            var count = 0;

            visited[seed] = true;
            stack.Push(seed);

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var x = index % width;
                var y = index / width;

                sum += probabilities[index];
                count++;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;

                // 8-connectivity: thin diagonal strokes would otherwise fragment into
                // several components and produce one box per stroke.
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height) continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width || (dx == 0 && dy == 0)) continue;

                        var neighbour = (ny * width) + nx;
                        if (visited[neighbour] || probabilities[neighbour] < options.BinarizationThreshold) continue;

                        visited[neighbour] = true;
                        stack.Push(neighbour);
                    }
                }
            }

            var score = (float)(sum / count);
            if (score < options.BoxThreshold) continue;

            var box = Unclip(left, top, right - left + 1, bottom - top + 1);

            var mapped = ScaleToSource(box, scaleX, scaleY, sourceWidth, sourceHeight);
            if (mapped.Width < options.MinBoxSize || mapped.Height < options.MinBoxSize) continue;

            boxes.Add(new DetectedBox(mapped, score));
        }

        return boxes;
    }

    /// <summary>
    /// Grows a box to undo DB's training-time shrink. The offset distance follows DB's own
    /// formula — area * ratio / perimeter — but with (ratio - 1), since this operates on the
    /// component's bounding box rather than on a shrunk polygon, and the full ratio would
    /// roughly double a short box's height.
    /// </summary>
    private Rectangle Unclip(int left, int top, int width, int height)
    {
        var area = (double)width * height;
        var perimeter = 2.0 * (width + height);
        var distance = (int)Math.Round(area * (options.UnclipRatio - 1f) / perimeter);

        return new Rectangle(left - distance, top - distance, width + (2 * distance), height + (2 * distance));
    }

    private static Rectangle ScaleToSource(
        Rectangle box, float scaleX, float scaleY, int sourceWidth, int sourceHeight)
    {
        var left = (int)Math.Floor(box.Left * scaleX);
        var top = (int)Math.Floor(box.Top * scaleY);
        var right = (int)Math.Ceiling(box.Right * scaleX);
        var bottom = (int)Math.Ceiling(box.Bottom * scaleY);

        left = Math.Clamp(left, 0, sourceWidth);
        top = Math.Clamp(top, 0, sourceHeight);
        right = Math.Clamp(right, 0, sourceWidth);
        bottom = Math.Clamp(bottom, 0, sourceHeight);

        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Orders boxes the way the text reads: top to bottom, then left to right within a line.
    /// A plain sort by Y alone would interleave two boxes on the same line whose tops differ
    /// by a pixel or two.
    /// </summary>
    private static List<DetectedBox> SortReadingOrder(List<DetectedBox> boxes)
    {
        var sorted = boxes
            .OrderBy(b => b.Bounds.Top)
            .ThenBy(b => b.Bounds.Left)
            .ToList();

        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            var current = sorted[i];
            var next = sorted[i + 1];

            var tolerance = Math.Max(4, Math.Min(current.Bounds.Height, next.Bounds.Height) / 2);
            if (Math.Abs(current.Bounds.Top - next.Bounds.Top) >= tolerance) continue;
            if (next.Bounds.Left >= current.Bounds.Left) continue;

            sorted[i] = next;
            sorted[i + 1] = current;
            if (i > 0) i -= 2;                          // re-check the pair behind us
        }

        return sorted;
    }

    public void Dispose() => session.Dispose();
}
