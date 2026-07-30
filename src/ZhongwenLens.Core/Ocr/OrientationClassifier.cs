using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ZhongwenLens.Core.Ocr;

/// <summary>
/// Two-class check for whether a text line is upside down.
/// </summary>
/// <remarks>
/// Needed because rotating a vertical line into horizontal form can land it 180 degrees out,
/// and the recogniser reads strictly left to right — an inverted line decodes to confident
/// nonsense rather than failing.
/// </remarks>
internal sealed class OrientationClassifier(InferenceSession session, OcrOptions options) : IDisposable
{
    /// <summary>Fixed input shape this model was trained with.</summary>
    private const int InputHeight = 48;

    private const int InputWidth = 192;

    private readonly string _inputName = session.InputMetadata.Keys.First();

    /// <summary>True when the crop should be rotated 180 degrees before recognition.</summary>
    public bool IsUpsideDown(Bitmap crop)
    {
        using var resized = ImageOps.Resize(crop, InputWidth, InputHeight);
        var tensor = new DenseTensor<float>(
            ImageOps.ToRecognitionTensor(resized, InputWidth), [1, 3, InputHeight, InputWidth]);

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results[0].AsTensor<float>();

        // Class 0 is upright, class 1 is 180 degrees. Only act on a confident prediction:
        // wrongly flipping an upright line is far worse than leaving one inverted.
        return output[0, 1] > options.OrientationThreshold;
    }

    public void Dispose() => session.Dispose();
}
