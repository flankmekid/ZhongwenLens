namespace ZhongwenLens.Core.Ocr;

/// <summary>
/// Maps recogniser output indices to characters.
/// </summary>
/// <remarks>
/// The index layout is fixed by how PaddleOCR trains CTC and is not negotiable:
/// <list type="bullet">
/// <item>index 0 — the CTC blank</item>
/// <item>indices 1..6623 — the lines of <c>ppocr_keys_v1.txt</c>, in file order</item>
/// <item>index 6624 — space, appended by PaddleOCR and absent from the file</item>
/// </list>
/// Getting this off by one shifts every decoded character into a different one, producing
/// fluent-looking nonsense instead of an error, so the count is validated on load.
/// </remarks>
public sealed class CharacterDictionary
{
    public const int BlankIndex = 0;

    private readonly string[] _characters;

    private CharacterDictionary(string[] characters) => _characters = characters;

    /// <summary>Total classes, which must equal the recogniser's output dimension.</summary>
    public int ClassCount => _characters.Length;

    public static CharacterDictionary Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"character dictionary not found at '{path}'. Run scripts\\fetch-models.ps1.", path);
        }

        // Read as lines without trimming: some entries are punctuation, and one of them is
        // a literal space that Trim() would silently turn into an empty string.
        var keys = File.ReadAllLines(path);

        var characters = new string[keys.Length + 2];
        characters[BlankIndex] = string.Empty;
        for (var i = 0; i < keys.Length; i++) characters[i + 1] = keys[i];
        characters[^1] = " ";

        return new CharacterDictionary(characters);
    }

    /// <summary>
    /// Validates against the recogniser's actual output width. A mismatch means the wrong
    /// dictionary for the model, which must fail loudly rather than decode garbage.
    /// </summary>
    public void ValidateAgainst(int modelClassCount)
    {
        if (modelClassCount == ClassCount) return;

        throw new InvalidOperationException(
            $"character dictionary has {ClassCount} classes but the recognition model " +
            $"outputs {modelClassCount}. These must match exactly or every decoded " +
            "character will be wrong. Re-run scripts\\fetch-models.ps1.");
    }

    public string this[int index]
        => index >= 0 && index < _characters.Length ? _characters[index] : string.Empty;
}
