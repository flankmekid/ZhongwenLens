namespace ZhongwenLens.DataBuild;

/// <summary>
/// jieba's unigram counts, loaded from <c>dict.txt</c> (<c>word count POS</c> per line).
/// These are the priors the segmenter's Viterbi pass scores against (DESIGN.md §3.3) —
/// the same table jieba itself uses, so segmentation quality should track jieba's.
/// </summary>
public sealed class FrequencyTable
{
    private readonly Dictionary<string, long> _counts;

    private FrequencyTable(Dictionary<string, long> counts, long total)
    {
        _counts = counts;
        Total = total;
    }

    /// <summary>Sum of all counts, the denominator for P(word).</summary>
    public long Total { get; }

    public int WordCount => _counts.Count;

    public long this[string word] => _counts.GetValueOrDefault(word, 0L);

    public static FrequencyTable Load(string path)
    {
        var counts = new Dictionary<string, long>(600_000, StringComparer.Ordinal);
        var total = 0L;

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;

            // "word count POS" — split on the first two spaces only; a headword never
            // contains a space, but being permissive about trailing fields is free.
            var first = line.IndexOf(' ');
            if (first <= 0) continue;

            var second = line.IndexOf(' ', first + 1);
            var countSpan = second < 0 ? line.AsSpan(first + 1) : line.AsSpan(first + 1, second - first - 1);

            if (!long.TryParse(countSpan, out var count) || count <= 0) continue;

            var word = line[..first];
            // jieba lists some words twice with different casing/POS; keep the largest.
            if (counts.TryGetValue(word, out var existing))
            {
                if (count <= existing) continue;
                total += count - existing;
                counts[word] = count;
            }
            else
            {
                counts[word] = count;
                total += count;
            }
        }

        return new FrequencyTable(counts, Math.Max(total, 1));
    }
}
