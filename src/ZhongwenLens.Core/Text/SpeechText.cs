using System.Text;

namespace ZhongwenLens.Core.Text;

/// <summary>
/// Reduces recognised text to the part worth speaking aloud.
/// </summary>
/// <remarks>
/// <para>
/// A snip usually catches more than the Chinese: page furniture ("Simplified Chinese",
/// "Learn More"), pinyin printed beside the characters, navigation labels. Handing all of that
/// to a Mandarin voice produces nonsense — it reads English letter shapes with Chinese
/// pronunciation rules, and reads pinyin as though it were a word.
/// </para>
/// <para>
/// Latin letters are therefore dropped. Digits are kept only where they touch a Han character.
/// That distinction matters: 2019冠状病毒病 and 21三体综合症 are real dictionary headwords whose
/// numbers must survive, but a bare number is almost always either a tone mark in numbered
/// pinyin ("tu2 shu1 guan3", which would be spoken as "two one three") or page furniture like a
/// copyright year. Keeping every digit was tried first and produced exactly those two failures.
/// </para>
/// <para>
/// CJK punctuation is kept because it drives prosody: a voice pauses at 。 and ，, and stripping
/// them runs an entire sentence together in one breath.
/// </para>
/// </remarks>
public static class SpeechText
{
    /// <summary>Punctuation that shapes how a Chinese sentence is spoken.</summary>
    private const string SpokenPunctuation = "。，、；：？！…—《》（）「」『』“”‘’";

    /// <summary>
    /// Returns only the speakable Chinese content, or an empty string when there is none.
    /// </summary>
    public static string Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (CharClassifier.IsHan(c) || SpokenPunctuation.Contains(c))
            {
                builder.Append(c);
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                // Take the whole run at once, so "2019" is judged as a number rather than four
                // separate digits.
                var end = i;
                while (end < text.Length && char.IsAsciiDigit(text[end])) end++;

                var touchesHan = (i > 0 && CharClassifier.IsHan(text[i - 1]))
                                 || (end < text.Length && CharClassifier.IsHan(text[end]));

                if (touchesHan)
                {
                    builder.Append(text, i, end - i);
                }
                else if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                i = end - 1;
                continue;
            }

            // Everything else — Latin, pinyin, ASCII punctuation, symbols — becomes a break, so
            // words either side of a dropped run don't get glued into one.
            if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }

        var result = builder.ToString().Trim();

        // A run of punctuation left behind after the words around it were dropped would be read
        // as odd noises, so treat that as nothing to say.
        return result.Any(c => CharClassifier.IsHan(c) || char.IsAsciiDigit(c))
            ? result
            : string.Empty;
    }

    /// <summary>Whether there is any Chinese worth speaking in this text.</summary>
    public static bool HasSpeakableContent(string? text) => Extract(text).Length > 0;
}
