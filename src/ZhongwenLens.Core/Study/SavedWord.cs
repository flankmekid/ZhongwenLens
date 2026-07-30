namespace ZhongwenLens.Core.Study;

/// <summary>A word the user starred, with the context it was met in.</summary>
/// <param name="SourceContext">
/// The full text of the snip this was saved from. The reason the store exists at all: a bare
/// word on a flashcard is far weaker than one carrying the sentence it was actually seen in.
/// </param>
/// <param name="HskLevel">1-6, 7 meaning the HSK 3.0 7-9 band, or null when ungraded.</param>
/// <param name="Classifiers">
/// Readable measure words, e.g. "个 gè". Stored separately rather than left inside
/// <paramref name="Senses"/>, because CC-CEDICT's raw form (<c>CL:個|个[ge4]</c>) is unreadable
/// on a flashcard — which is exactly where it ended up before this field existed.
/// </param>
public sealed record SavedWord(
    string Simplified,
    string? Traditional,
    string PinyinMarks,
    IReadOnlyList<string> Senses,
    int? HskLevel,
    string? SourceContext,
    DateTimeOffset SavedAt,
    string? Classifiers = null)
{
    /// <summary>Senses joined for single-line display and for export.</summary>
    public string SenseSummary => string.Join("; ", Senses);

    /// <summary>True when the context adds anything beyond the word itself.</summary>
    public bool HasContext
        => !string.IsNullOrWhiteSpace(SourceContext)
           && !string.Equals(SourceContext, Simplified, StringComparison.Ordinal);
}
