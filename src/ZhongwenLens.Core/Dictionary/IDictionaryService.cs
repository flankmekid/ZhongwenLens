namespace ZhongwenLens.Core.Dictionary;

public interface IDictionaryService
{
    /// <summary>Total entry count, for the about box and diagnostics.</summary>
    int EntryCount { get; }

    /// <summary>
    /// Every entry for a spelling, matched against both simplified and traditional forms,
    /// most frequent first. Multiple results mean multiple readings, not duplicates
    /// (DESIGN.md §3.4).
    /// </summary>
    IReadOnlyList<DictEntry> Lookup(string? word);

    /// <summary>
    /// Whether the segmenter treats this string as a word. Bounded by the lexicon's
    /// maximum candidate length, so it answers "is this segmentable" rather than
    /// "does an entry exist" — use <see cref="Lookup"/> for the latter.
    /// </summary>
    bool IsKnownWord(string? word);

    /// <summary>
    /// Common multi-character words containing <paramref name="character"/>, most frequent
    /// first. Powers the single-character view, where seeing a character in real words is
    /// the point (DESIGN.md §3.4).
    /// </summary>
    IReadOnlyList<DictEntry> WordsContaining(char character, int limit = 12);
}
