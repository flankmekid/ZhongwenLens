using System.Text.RegularExpressions;

namespace ZhongwenLens.DataBuild;

public sealed record CedictEntry(
    string Traditional,
    string Simplified,
    string PinyinNumbered,
    IReadOnlyList<string> Senses);

/// <summary>
/// Streams CC-CEDICT's <c>cedict_ts.u8</c>, one entry per line:
/// <code>漢字 汉字 [han4 zi4] /Chinese character/CL:個|个[ge4]/</code>
/// </summary>
public static partial class CedictParser
{
    // Headwords can contain digits and Latin ("11區 11区 [11 Qu1]"), so the first two
    // fields are matched as runs of non-whitespace rather than as Han-only.
    [GeneratedRegex(@"^(?<trad>\S+)\s+(?<simp>\S+)\s+\[(?<pinyin>[^\]]*)\]\s+/(?<senses>.*)/\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern { get; }

    public static IEnumerable<CedictEntry> Parse(string path, ParseStats? stats = null)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;      // '#!' metadata and comments

            var match = EntryPattern.Match(line);
            if (!match.Success)
            {
                stats?.Skip(line);
                continue;
            }

            var senses = match.Groups["senses"].Value
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            if (senses.Length == 0)
            {
                stats?.Skip(line);
                continue;
            }

            stats?.Accept();
            yield return new CedictEntry(
                match.Groups["trad"].Value,
                match.Groups["simp"].Value,
                match.Groups["pinyin"].Value.Trim(),
                senses);
        }
    }

    /// <summary>
    /// Counts what the parser accepted and rejected. A silent regex mismatch would
    /// quietly drop entries, so the build reports the tally and the first few offenders.
    /// </summary>
    public sealed class ParseStats
    {
        private readonly List<string> _samples = [];

        public int Accepted { get; private set; }
        public int Skipped { get; private set; }
        public IReadOnlyList<string> SkippedSamples => _samples;

        public void Accept() => Accepted++;

        public void Skip(string line)
        {
            Skipped++;
            if (_samples.Count < 5) _samples.Add(line);
        }
    }
}
