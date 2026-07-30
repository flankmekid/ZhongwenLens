using System.Text.RegularExpressions;
using ZhongwenLens.Core.Text;

namespace ZhongwenLens.Core.Dictionary;

/// <param name="Senses">Ordinary glosses, with classifier entries removed.</param>
/// <param name="Classifiers">Readable measure words, empty when the entry lists none.</param>
public sealed record FormattedSenses(IReadOnlyList<string> Senses, string Classifiers);

/// <summary>
/// Tidies CC-CEDICT gloss text for display.
/// </summary>
/// <remarks>
/// CC-CEDICT encodes measure words as a pseudo-sense: <c>CL:個|个[ge4]</c>, meaning traditional
/// 個, simplified 个, read ge4. That is genuinely useful — knowing 个 is 字's measure word is
/// part of learning it — but shown raw it reads as line noise among real definitions. It is
/// pulled out and rendered as "个 gè" on its own line instead.
/// </remarks>
public static partial class SenseFormatter
{
    [GeneratedRegex(@"^CL:(?<body>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ClassifierPattern { get; }

    /// <summary>One classifier: optional traditional|simplified, then a bracketed reading.</summary>
    [GeneratedRegex(@"(?:(?<trad>[^|,\[\]]+)\|)?(?<simp>[^|,\[\]]+)\[(?<pinyin>[^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ClassifierEntryPattern { get; }

    public static FormattedSenses Format(IReadOnlyList<string> senses)
    {
        if (senses.Count == 0) return new FormattedSenses([], string.Empty);

        var plain = new List<string>(senses.Count);
        var classifiers = new List<string>();

        foreach (var sense in senses)
        {
            var match = ClassifierPattern.Match(sense);
            if (!match.Success)
            {
                plain.Add(sense);
                continue;
            }

            classifiers.AddRange(ParseClassifiers(match.Groups["body"].Value));
        }

        // An entry consisting only of classifiers would otherwise display as blank.
        if (plain.Count == 0 && classifiers.Count > 0) plain.Add(senses[0]);

        return new FormattedSenses(plain, string.Join(", ", classifiers.Distinct(StringComparer.Ordinal)));
    }

    private static IEnumerable<string> ParseClassifiers(string body)
    {
        foreach (Match match in ClassifierEntryPattern.Matches(body))
        {
            // Simplified is the headword the app shows elsewhere, so it leads here too.
            var word = match.Groups["simp"].Value.Trim();
            if (word.Length == 0) continue;

            var pinyin = PinyinConverter.ToToneMarks(match.Groups["pinyin"].Value);
            yield return pinyin.Length > 0 ? $"{word} {pinyin}" : word;
        }
    }
}
