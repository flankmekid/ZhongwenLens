namespace ZhongwenLens.Core.Dictionary;

/// <summary>One CC-CEDICT entry: a single spelling with a single reading.</summary>
/// <remarks>
/// A spelling with several readings is several entries, not one entry with several
/// readings — 行 is xíng, háng and hàng. Lookups therefore return a list, and the UI
/// shows every reading rather than picking one (DESIGN.md §3.4).
/// </remarks>
/// <param name="HskNew">HSK 3.0 band: 1-6, or 7 meaning the combined 7-9 band.</param>
/// <param name="HskOld">HSK 2.0 band: 1-6.</param>
/// <param name="Frequency">jieba unigram count; 0 when the word isn't in that table.</param>
public sealed record DictEntry(
    int Id,
    string Traditional,
    string Simplified,
    string PinyinNumbered,
    string PinyinMarks,
    IReadOnlyList<string> Senses,
    int? HskNew,
    int? HskOld,
    string? Radical,
    long Frequency)
{
    /// <summary>The gloss shown when only one line fits, e.g. in the literal chain.</summary>
    public string PrimarySense => Senses.Count > 0 ? Senses[0] : string.Empty;

    /// <summary>True when the traditional and simplified spellings differ.</summary>
    public bool HasDistinctTraditional
        => !string.Equals(Traditional, Simplified, StringComparison.Ordinal);
}
