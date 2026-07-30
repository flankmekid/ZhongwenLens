using System.Drawing;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ZhongwenLens.Core.Ocr;

internal sealed record RecognizedText(string Text, float Confidence);

/// <summary>
/// Text recognition: CRNN inference plus greedy CTC decoding.
/// </summary>
/// <remarks>
/// Each crop is scaled to a fixed height, keeping its aspect ratio, and padded to a common
/// width. The model returns a probability distribution over 6625 classes per timestep
/// (softmax already applied), which CTC decoding collapses into a string: take the argmax at
/// each step, drop blanks, and collapse runs of the same class. The run-collapsing is why CTC
/// needs a blank at all — it's what lets 田 (one class twice in a row) stay distinct from a
/// genuine repeat.
/// </remarks>
internal sealed class TextRecognizer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly CharacterDictionary _characters;
    private readonly OcrOptions _options;
    private readonly string _inputName;

    public TextRecognizer(InferenceSession session, CharacterDictionary characters, OcrOptions options)
    {
        _session = session;
        _characters = characters;
        _options = options;
        _inputName = session.InputMetadata.Keys.First();

        // Fail loudly on a model/dictionary mismatch rather than decoding shifted nonsense.
        var classes = session.OutputMetadata.Values.First().Dimensions[^1];
        if (classes > 0) characters.ValidateAgainst(classes);
    }

    public RecognizedText Recognize(Bitmap crop)
    {
        var height = _options.RecognitionHeight;
        var scaledWidth = Math.Max(1, (int)Math.Round(crop.Width * (float)height / crop.Height));

        // Multiples of 8 keep the timestep count predictable across widths.
        var paddedWidth = Math.Min(
            _options.RecognitionMaxWidth,
            Math.Max(16, ImageOps.RoundUpTo(scaledWidth, 8)));

        using var resized = ImageOps.Resize(crop, Math.Min(scaledWidth, paddedWidth), height);
        var tensor = new DenseTensor<float>(
            ImageOps.ToRecognitionTensor(resized, paddedWidth), [1, 3, height, paddedWidth]);

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results[0].AsTensor<float>();

        return Decode(output);
    }

    private RecognizedText Decode(Tensor<float> output)
    {
        var timesteps = output.Dimensions[1];
        var classes = output.Dimensions[2];

        var builder = new StringBuilder();
        var confidenceSum = 0d;
        var kept = 0;
        var previousClass = -1;

        for (var t = 0; t < timesteps; t++)
        {
            var bestClass = 0;
            var bestProbability = 0f;

            for (var c = 0; c < classes; c++)
            {
                var probability = output[0, t, c];
                if (probability <= bestProbability) continue;

                bestProbability = probability;
                bestClass = c;
            }

            // CTC: blanks are separators, and a class repeated on consecutive timesteps is
            // one character being observed across several frames, not two characters.
            if (bestClass == CharacterDictionary.BlankIndex || bestClass == previousClass)
            {
                previousClass = bestClass;
                continue;
            }

            previousClass = bestClass;
            builder.Append(_characters[bestClass]);
            confidenceSum += bestProbability;
            kept++;
        }

        var confidence = kept == 0 ? 0f : (float)(confidenceSum / kept);
        return new RecognizedText(builder.ToString(), confidence);
    }

    public void Dispose() => _session.Dispose();
}
