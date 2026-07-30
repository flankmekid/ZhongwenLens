namespace ZhongwenLens.Core.Text;

public enum TokenKind
{
    /// <summary>A Han-character word that the dictionary knows.</summary>
    Word,

    /// <summary>A Han character with no dictionary entry at all.</summary>
    UnknownHan,

    Latin,
    Digit,
    Punctuation,
    Whitespace,
}

/// <param name="Start">Index into the string that was segmented, for highlight mapping.</param>
public sealed record Token(string Text, int Start, TokenKind Kind)
{
    public int Length => Text.Length;

    /// <summary>Whether this token is worth showing a dictionary card for.</summary>
    public bool IsLookupCandidate => Kind is TokenKind.Word or TokenKind.UnknownHan;
}

public static class CharClassifier
{
    /// <summary>
    /// CJK Unified Ideographs plus Extension A. Deliberately excludes the supplementary
    /// planes: those need surrogate-pair handling and contain nothing CC-CEDICT covers.
    /// </summary>
    public static bool IsHan(char c)
        => c is >= '一' and <= '鿿'      // CJK Unified Ideographs
            or >= '㐀' and <= '䶿';      // Extension A

    public static TokenKind Classify(char c)
    {
        if (IsHan(c)) return TokenKind.Word;
        if (char.IsWhiteSpace(c)) return TokenKind.Whitespace;
        if (char.IsAsciiDigit(c)) return TokenKind.Digit;
        if (char.IsLetter(c)) return TokenKind.Latin;
        return TokenKind.Punctuation;
    }
}
