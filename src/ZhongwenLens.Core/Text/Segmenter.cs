namespace ZhongwenLens.Core.Text;

/// <summary>
/// Splits Chinese text into dictionary words using a DAG of candidate matches scored by
/// unigram frequency (DESIGN.md §3.3).
/// </summary>
/// <remarks>
/// <para>
/// Chinese has no spaces, so word boundaries have to be inferred. Longest-match alone gets
/// the classic cases wrong: 研究生命起源 becomes 研究生 / 命 / 起源 ("graduate student, life,
/// origin") instead of 研究 / 生命 / 起源 ("research the origin of life"). Maximising
/// Σ log P(word) over the whole run instead of greedily taking the longest prefix fixes
/// this, because 生命 and 研究 are both far more common than 命 alone.
/// </para>
/// <para>
/// The vocabulary is the dictionary's own headword list, which guarantees every Word token
/// produced has a definition to show — there is no way to emit a token the UI can't
/// explain.
/// </para>
/// </remarks>
public sealed class Segmenter(IWordLexicon lexicon)
{
    /// <summary>
    /// Pseudo-count for a word that's in the dictionary but absent from the frequency
    /// table. Below any real count, so genuinely attested words still win, but far above
    /// <see cref="UnknownCharCount"/> so a known rare word beats splitting into characters.
    /// </summary>
    private const double KnownWordFloorCount = 0.5;

    /// <summary>Pseudo-count for a character with no dictionary entry at all.</summary>
    private const double UnknownCharCount = 0.08;

    public IReadOnlyList<Token> Segment(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var tokens = new List<Token>();
        var index = 0;

        while (index < text.Length)
        {
            var kind = CharClassifier.Classify(text[index]);

            if (kind == TokenKind.Word)                      // i.e. Han
            {
                var runLength = 1;
                while (index + runLength < text.Length && CharClassifier.IsHan(text[index + runLength]))
                {
                    runLength++;
                }

                SegmentHanRun(text, index, runLength, tokens);
                index += runLength;
                continue;
            }

            // Latin words, digit runs and whitespace group together; punctuation stays
            // one token per mark so it can't glue two sentences into a single token.
            var length = 1;
            if (kind is TokenKind.Latin or TokenKind.Digit or TokenKind.Whitespace)
            {
                while (index + length < text.Length && CharClassifier.Classify(text[index + length]) == kind)
                {
                    length++;
                }
            }

            tokens.Add(new Token(text.Substring(index, length), index, kind));
            index += length;
        }

        return tokens;
    }

    private void SegmentHanRun(string text, int runStart, int runLength, List<Token> output)
    {
        var run = text.AsSpan(runStart, runLength);
        var total = Math.Max(1L, lexicon.TotalFrequency);
        var maxLen = Math.Max(1, lexicon.MaxWordLength);

        // best[i] = best achievable log-probability for the first i characters of the run.
        var best = new double[runLength + 1];
        var previous = new int[runLength + 1];
        Array.Fill(best, double.NegativeInfinity);
        best[0] = 0d;
        previous[0] = -1;

        for (var i = 0; i < runLength; i++)
        {
            if (double.IsNegativeInfinity(best[i])) continue;

            var limit = Math.Min(maxLen, runLength - i);
            var singleCharIsWord = false;

            for (var length = 1; length <= limit; length++)
            {
                var frequency = lexicon.GetFrequency(run.Slice(i, length));
                if (frequency < 0) continue;                  // not in the vocabulary

                if (length == 1) singleCharIsWord = true;

                var score = best[i] + LogProbability(frequency, total);
                if (score <= best[i + length]) continue;

                best[i + length] = score;
                previous[i + length] = i;
            }

            // An out-of-vocabulary character always gets an edge, so the lattice can never
            // dead-end on unknown input and leave the whole run unsegmentable.
            if (singleCharIsWord) continue;

            var fallback = best[i] + Math.Log(UnknownCharCount / total);
            if (fallback <= best[i + 1]) continue;

            best[i + 1] = fallback;
            previous[i + 1] = i;
        }

        var boundaries = new List<int>();
        for (var i = runLength; i > 0; i = previous[i]) boundaries.Add(i);
        boundaries.Add(0);
        boundaries.Reverse();

        for (var k = 0; k + 1 < boundaries.Count; k++)
        {
            var start = boundaries[k];
            var end = boundaries[k + 1];
            var slice = text.Substring(runStart + start, end - start);
            var kind = lexicon.GetFrequency(slice) >= 0 ? TokenKind.Word : TokenKind.UnknownHan;

            output.Add(new Token(slice, runStart + start, kind));
        }
    }

    private static double LogProbability(long frequency, long total)
        => Math.Log((frequency <= 0 ? KnownWordFloorCount : frequency) / total);
}
