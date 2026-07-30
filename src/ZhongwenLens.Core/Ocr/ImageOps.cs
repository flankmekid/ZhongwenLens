using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ZhongwenLens.Core.Ocr;

/// <summary>
/// Bitmap-to-tensor conversion and the geometric operations the OCR pipeline needs.
/// </summary>
internal static class ImageOps
{
    /// <summary>ImageNet channel means, as PP-OCR's detection preprocessing expects.</summary>
    private static readonly float[] DetectionMean = [0.485f, 0.456f, 0.406f];

    private static readonly float[] DetectionStd = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// Copies pixels out as BGRA bytes. One LockBits beats millions of GetPixel calls, which
    /// is the difference between a snip feeling instant and feeling broken.
    /// </summary>
    public static byte[] ToBgraBytes(Bitmap bitmap, out int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Builds an NCHW float tensor with ImageNet normalisation, for the detection model.
    /// </summary>
    public static float[] ToDetectionTensor(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bytes = ToBgraBytes(bitmap, out var stride);
        var tensor = new float[3 * width * height];
        var plane = width * height;

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var offset = row + (x * 4);
                var pixel = y * width + x;

                // Source is BGRA; the models are trained on RGB, so the channels swap here.
                tensor[pixel] = ((bytes[offset + 2] / 255f) - DetectionMean[0]) / DetectionStd[0];
                tensor[plane + pixel] = ((bytes[offset + 1] / 255f) - DetectionMean[1]) / DetectionStd[1];
                tensor[(2 * plane) + pixel] = ((bytes[offset] / 255f) - DetectionMean[2]) / DetectionStd[2];
            }
        }

        return tensor;
    }

    /// <summary>
    /// Builds an NCHW tensor scaled to [-1,1], for the recognition and orientation models.
    /// Crops narrower than <paramref name="paddedWidth"/> are zero-padded on the right,
    /// which lands at mid-grey after this normalisation — the same as PaddleOCR does.
    /// </summary>
    public static float[] ToRecognitionTensor(Bitmap bitmap, int paddedWidth)
    {
        var width = Math.Min(bitmap.Width, paddedWidth);
        var height = bitmap.Height;
        var bytes = ToBgraBytes(bitmap, out var stride);
        var tensor = new float[3 * paddedWidth * height];
        var plane = paddedWidth * height;

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var offset = row + (x * 4);
                var pixel = (y * paddedWidth) + x;

                tensor[pixel] = ((bytes[offset + 2] / 255f) - 0.5f) / 0.5f;
                tensor[plane + pixel] = ((bytes[offset + 1] / 255f) - 0.5f) / 0.5f;
                tensor[(2 * plane) + pixel] = ((bytes[offset] / 255f) - 0.5f) / 0.5f;
            }
        }

        return tensor;
    }

    /// <summary>Extracts an 8-bit luminance map, used for layout probing.</summary>
    public static byte[] ToGrayscale(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bytes = ToBgraBytes(bitmap, out var stride);
        var gray = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var offset = row + (x * 4);
                // Rec. 601 luma, integer arithmetic.
                var luma = ((bytes[offset + 2] * 299) + (bytes[offset + 1] * 587) + (bytes[offset] * 114)) / 1000;
                gray[(y * width) + x] = (byte)luma;
            }
        }

        return gray;
    }

    /// <summary>
    /// Resizes with high-quality interpolation. <see cref="InterpolationMode.HighQualityBicubic"/>
    /// matters when upscaling small screen text: nearest-neighbour leaves stair-stepped
    /// strokes that the recogniser reads as different characters.
    /// </summary>
    public static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            // Explicit source rect avoids GDI+'s half-pixel edge bleed on scaling.
            graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height),
                new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Copies a sub-rectangle, clamped to the source bounds.</summary>
    public static Bitmap Crop(Bitmap source, Rectangle region)
    {
        var clamped = Rectangle.Intersect(region, new Rectangle(0, 0, source.Width, source.Height));
        if (clamped.Width <= 0 || clamped.Height <= 0) clamped = new Rectangle(0, 0, 1, 1);

        var result = new Bitmap(clamped.Width, clamped.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.DrawImage(source, new Rectangle(0, 0, clamped.Width, clamped.Height),
                clamped, GraphicsUnit.Pixel);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public static Bitmap Rotate180(Bitmap source)
    {
        var result = (Bitmap)source.Clone();
        result.RotateFlip(RotateFlipType.Rotate180FlipNone);
        return result;
    }

    /// <summary>
    /// Rotates 90 degrees clockwise. Vertical Chinese text is recognised by rotating the
    /// line into horizontal form, since the recogniser only reads left to right.
    /// </summary>
    public static Bitmap Rotate90(Bitmap source)
    {
        var result = (Bitmap)source.Clone();
        result.RotateFlip(RotateFlipType.Rotate90FlipNone);
        return result;
    }

    /// <summary>
    /// Rounds up to the next multiple of <paramref name="multiple"/>.
    /// </summary>
    public static int RoundUpTo(int value, int multiple)
        => ((value + multiple - 1) / multiple) * multiple;

    /// <summary>
    /// Rounds to the <em>nearest</em> multiple, with a floor of one multiple.
    /// </summary>
    /// <remarks>
    /// The detector needs both input dimensions to be multiples of 32, and PaddleOCR reaches
    /// that by resizing rather than padding — so the choice of rounding directly controls how
    /// much the aspect ratio is distorted. Rounding up is noticeably worse: a 200x70 crop
    /// becomes 224x96, stretching it 1.12x across and 1.37x down. Rounding to nearest gives
    /// 192x64, which is 0.96x and 0.91x — near-uniform, and what PaddleOCR itself does.
    /// </remarks>
    public static int RoundToNearest(int value, int multiple)
        => Math.Max(multiple, (int)Math.Round((double)value / multiple) * multiple);
}
