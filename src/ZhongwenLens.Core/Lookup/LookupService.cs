using ZhongwenLens.Core.Dictionary;
using ZhongwenLens.Core.Text;

namespace ZhongwenLens.Core.Lookup;

/// <summary>A segmented token together with everything the dictionary knows about it.</summary>
public sealed record LookupToken(Token Token, IReadOnlyList<DictEntry> Entries)
{
    public string Text => Token.Text;

    public bool HasEntries => Entries.Count > 0;

    /// <summary>The reading shown next to the token; the most frequent when several exist.</summary>
    public string Pinyin => Entries.Count > 0 ? Entries[0].PinyinMarks : string.Empty;

    /// <summary>True when the spelling has more than one reading, e.g. 行 xíng / háng.</summary>
    public bool HasMultipleReadings => Entries.Count > 1;

    public int? HskLevel => Entries.Count > 0 ? Entries[0].HskNew ?? Entries[0].HskOld : null;
}

/// <param name="WholeMatch">
/// Entries for the entire selection when it is itself a headword — the 成语 case, and the best
/// possible result (DESIGN.md §1.4 tier 1).
/// </param>
/// <param name="LiteralChain">
/// First glosses of each token joined with a separator, when the selection is a short run of
/// known words (tier 2). Empty otherwise.
/// </param>
/// <param name="CharacterWords">
/// Common multi-character words containing the selection, populated only for a
/// single-character selection. Seeing a character in real words is the highest-value thing the
/// app can show for one character (DESIGN.md §3.4).
/// </param>
public sealed record LookupResult(
    string SourceText,
    IReadOnlyList<LookupToken> Tokens,
    IReadOnlyList<DictEntry> WholeMatch,
    string LiteralChain,
    IReadOnlyList<DictEntry> CharacterWords)
{
    public static readonly LookupResult Empty = new(string.Empty, [], [], string.Empty, []);

    public bool IsEmpty => SourceText.Length == 0;

    /// <summary>True when the whole selection resolved to a dictionary entry of its own.</summary>
    public bool IsWholeMatch => WholeMatch.Count > 0;

    /// <summary>True for a one-character selection, which gets the expanded view (§3.4).</summary>
    public bool IsSingleCharacter
        => SourceText.Length == 1 && CharClassifier.IsHan(SourceText[0]);

    /// <summary>Pinyin for the whole selection, assembled from the per-token readings.</summary>
    public string Pinyin => IsWholeMatch
        ? WholeMatch[0].PinyinMarks
        : string.Join(' ', Tokens.Where(t => t.HasEntries).Select(t => t.Pinyin));
}

/// <summary>
/// Turns recognised text into a displayable breakdown: segment, look up, and resolve the
/// selection as a whole where possible (DESIGN.md §1.4).
/// </summary>
public sealed class LookupService(IDictionaryService dictionary, Segmenter segmenter)
{
    /// <summary>Longest selection still given a literal chain; beyond this only the cards help.</summary>
    private const int MaxLiteralChainTokens = 4;

    /// <summary>Example words shown for a single character. Enough to see usage, not a wall.</summary>
    private const int CharacterWordLimit = 10;

    public LookupResult Analyze(string? text)
    {
        var cleaned = text?.Trim() ?? string.Empty;
        if (cleaned.Length == 0) return LookupResult.Empty;

        // Tier 1: the whole selection is a headword. Checked before segmenting, because
        // 马马虎虎 must resolve to its own gloss rather than to four character cards.
        var wholeMatch = dictionary.Lookup(cleaned);

        var tokens = segmenter.Segment(cleaned)
            .Where(t => t.IsLookupCandidate)
            .Select(t => new LookupToken(t, dictionary.Lookup(t.Text)))
            .ToList();

        // Only for a one-character selection: for anything longer the per-word cards already
        // carry the useful detail, and this would be noise.
        var characterWords = cleaned.Length == 1 && CharClassifier.IsHan(cleaned[0])
            ? dictionary.WordsContaining(cleaned[0], CharacterWordLimit)
            : [];

        return new LookupResult(
            cleaned, tokens, wholeMatch, BuildLiteralChain(wholeMatch, tokens), characterWords);
    }

    /// <summary>
    /// Tier 2: a word-by-word gloss for short runs. Explicitly literal, not fluent — this is the
    /// honest output of a dictionary-only design, not an attempt at translation.
    /// </summary>
    private static string BuildLiteralChain(IReadOnlyList<DictEntry> wholeMatch, List<LookupToken> tokens)
    {
        if (wholeMatch.Count > 0) return string.Empty;      // tier 1 already answered it
        if (tokens.Count is < 2 or > MaxLiteralChainTokens) return string.Empty;
        if (tokens.Any(t => !t.HasEntries)) return string.Empty;

        var glosses = tokens.Select(t => FirstSense(t.Entries[0])).Where(g => g.Length > 0).ToList();
        return glosses.Count < 2 ? string.Empty : string.Join(" · ", glosses);
    }

    /// <summary>
    /// The first gloss, trimmed of CC-CEDICT's parenthetical qualifiers and classifier notes,
    /// which are noise in a one-line chain.
    /// </summary>
    private static string FirstSense(DictEntry entry)
    {
        var sense = entry.PrimarySense;
        if (sense.StartsWith("CL:", StringComparison.Ordinal)) return string.Empty;

        var cut = sense.IndexOf(';');
        if (cut > 0) sense = sense[..cut];

        return sense.Trim();
    }
}
