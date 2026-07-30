namespace ZhongwenLens.Core.Text;

/// <summary>
/// The vocabulary and frequency priors the segmenter scores against.
/// </summary>
/// <remarks>
/// Separate from <c>IDictionaryService</c> so the segmenter can be unit-tested against a
/// hand-built vocabulary instead of a 25 MB database — the ambiguity cases that matter
/// (研究生命起源 and friends) need an exact, controlled word list to be meaningful tests.
/// </remarks>
public interface IWordLexicon
{
    /// <summary>Longest candidate the segmenter will probe for at any position.</summary>
    int MaxWordLength { get; }

    /// <summary>Sum of all frequencies; the denominator for P(word).</summary>
    long TotalFrequency { get; }

    /// <summary>
    /// Frequency for a candidate substring, or -1 if the word isn't in the vocabulary.
    /// A known word with no frequency data returns 0.
    /// </summary>
    /// <remarks>
    /// Takes a span so the segmenter can probe candidates without allocating a substring
    /// per probe — there are up to <see cref="MaxWordLength"/> probes per character.
    /// </remarks>
    long GetFrequency(ReadOnlySpan<char> word);
}
