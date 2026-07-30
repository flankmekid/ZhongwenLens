namespace ZhongwenLens.Core.Text;

/// <summary>
/// An <see cref="IWordLexicon"/> backed by a plain dictionary. Used by tests, and the
/// basis for any future user-supplied word list.
/// </summary>
public sealed class InMemoryWordLexicon : IWordLexicon
{
    private readonly Dictionary<string, long> _words;
    private readonly Dictionary<string, long>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public InMemoryWordLexicon(IEnumerable<KeyValuePair<string, long>> words)
    {
        _words = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var (word, frequency) in words)
        {
            if (string.IsNullOrEmpty(word)) continue;

            _words[word] = frequency;
            if (word.Length > MaxWordLength) MaxWordLength = word.Length;
            TotalFrequency += Math.Max(0, frequency);
        }

        if (MaxWordLength == 0) MaxWordLength = 1;

        // Lets the segmenter probe candidates as spans, with no substring per probe.
        _lookup = _words.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Builds a lexicon from bare words, all with no frequency data.</summary>
    public static InMemoryWordLexicon FromWords(params string[] words)
        => new(words.Select(w => new KeyValuePair<string, long>(w, 0L)));

    public int MaxWordLength { get; }

    public long TotalFrequency { get; }

    public long GetFrequency(ReadOnlySpan<char> word)
        => _lookup.TryGetValue(word, out var frequency) ? frequency : -1L;
}
