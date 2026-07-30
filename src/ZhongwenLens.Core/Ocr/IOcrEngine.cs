using System.Drawing;

namespace ZhongwenLens.Core.Ocr;

/// <summary>
/// Turns a bitmap into recognised text. The boundary that keeps the model choice out of the
/// rest of the app — swapping PP-OCRv4 for v5, or for a cloud engine, happens here alone
/// (DESIGN.md §3.2).
/// </summary>
public interface IOcrEngine : IDisposable
{
    Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default);
}
