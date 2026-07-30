using System.Text;

namespace ZhongwenLens.Core.Text;

/// <summary>
/// Converts CC-CEDICT's numbered pinyin ("ni3 hao3") into tone-marked pinyin ("nǐ hǎo").
/// </summary>
/// <remarks>
/// Readings always come from the dictionary entry rather than a per-character table,
/// which is what makes heteronyms correct for free: 银行 is stored as "yin2 hang2", so it
/// renders yín háng and never yín xíng. See DESIGN.md §3.5.
/// </remarks>
public static class PinyinConverter
{
    /// <summary>Tone-marked forms of each vowel, indexed by tone 1-4.</summary>
    private static readonly Dictionary<char, string> ToneMarks = new()
    {
        ['a'] = "āáǎà", ['e'] = "ēéěè", ['i'] = "īíǐì",
        ['o'] = "ōóǒò", ['u'] = "ūúǔù", ['ü'] = "ǖǘǚǜ",
        ['A'] = "ĀÁǍÀ", ['E'] = "ĒÉĚÈ", ['I'] = "ĪÍǏÌ",
        ['O'] = "ŌÓǑÒ", ['U'] = "ŪÚǓÙ", ['Ü'] = "ǕǗǙǛ",
    };

    /// <summary>
    /// Renders a whole numbered-pinyin string, syllables separated by single spaces.
    /// </summary>
    public static string ToToneMarks(string? numbered)
    {
        if (string.IsNullOrWhiteSpace(numbered)) return string.Empty;
        return string.Join(' ', ToSyllables(numbered));
    }

    /// <summary>
    /// Renders each syllable separately. The UI needs this to align pinyin above
    /// individual characters in ruby layout (DESIGN.md §3.8), where a single joined
    /// string would be useless.
    /// </summary>
    public static IReadOnlyList<string> ToSyllables(string? numbered)
    {
        if (string.IsNullOrWhiteSpace(numbered)) return [];

        var result = new List<string>();
        foreach (var token in numbered.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var syllable = ConvertSyllable(token);
            if (syllable.Length == 0) continue;

            // Erhua: CC-CEDICT writes 花儿 as "hua1 r5". Rendering that as "huā r" reads
            // as two syllables when it is one, so the r attaches to what precedes it.
            if (syllable is "r" && result.Count > 0)
            {
                result[^1] += "r";
                continue;
            }

            result.Add(syllable);
        }

        return result;
    }

    /// <summary>
    /// Strips tones entirely ("ni3 hao3" -> "ni hao"). Used for case- and
    /// tone-insensitive search, where requiring exact tones would be hostile.
    /// </summary>
    public static string StripTones(string? numbered)
    {
        if (string.IsNullOrWhiteSpace(numbered)) return string.Empty;

        var parts = numbered
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => NormalizeUmlaut(TrimToneDigit(t, out _)))
            .Where(t => t.Length > 0);

        return string.Join(' ', parts);
    }

    private static string ConvertSyllable(string token)
    {
        var body = NormalizeUmlaut(TrimToneDigit(token, out var tone));
        if (body.Length == 0) return string.Empty;

        // Tone 5 (neutral) and tokens with no tone digit at all — CC-CEDICT includes
        // bare Latin like "CD" and the placeholder "xx5" — render unmarked.
        if (tone is < 1 or > 4) return body;

        var index = FindToneVowel(body);
        if (index < 0) return body;

        var vowel = body[index];
        if (!ToneMarks.TryGetValue(vowel, out var marked)) return body;

        return string.Concat(body.AsSpan(0, index), marked[tone - 1].ToString(), body.AsSpan(index + 1));
    }

    private static string TrimToneDigit(string token, out int tone)
    {
        tone = 0;
        if (token.Length == 0) return token;

        var last = token[^1];
        if (last is < '1' or > '5') return token;

        // A trailing digit is only a tone if there is a syllable in front of it to carry
        // the mark. CC-CEDICT has numeric headwords — 11区 is "[11 Qu1]", 120 is
        // "[yao1 er4 ling2]" — and reading the second '1' of "11" as a tone would silently
        // delete a digit from the reading.
        var body = token[..^1];
        if (!ContainsSyllableLetter(body)) return token;

        tone = last - '0';
        return body;
    }

    private static bool ContainsSyllableLetter(string body)
    {
        foreach (var c in body)
        {
            if (char.IsAsciiLetter(c) || c is 'ü' or 'Ü') return true;
        }

        return false;
    }

    /// <summary>Maps CC-CEDICT's "u:" digraph, and the common "v" shorthand, to ü.</summary>
    private static string NormalizeUmlaut(string body)
    {
        if (!body.Contains("u:", StringComparison.OrdinalIgnoreCase)
            && body.IndexOfAny(['v', 'V']) < 0)
        {
            return body;
        }

        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if ((c is 'u' or 'U') && i + 1 < body.Length && body[i + 1] == ':')
            {
                sb.Append(c is 'u' ? 'ü' : 'Ü');
                i++;                       // consume the colon
            }
            else if (c is 'v') sb.Append('ü');
            else if (c is 'V') sb.Append('Ü');
            else sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Standard tone-mark placement: a takes it, else o, else e, else the last of i/u/ü.
    /// The final clause is what makes "jiu3" -> jiǔ but "hui4" -> huì.
    /// </summary>
    private static int FindToneVowel(string body)
    {
        var index = body.IndexOfAny(['a', 'A']);
        if (index >= 0) return index;

        index = body.IndexOfAny(['o', 'O']);
        if (index >= 0) return index;

        index = body.IndexOfAny(['e', 'E']);
        if (index >= 0) return index;

        return body.LastIndexOfAny(['i', 'I', 'u', 'U', 'ü', 'Ü']);
    }
}
