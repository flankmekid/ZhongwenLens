using System.Text;
using ZhongwenLens.Core.Dictionary;

namespace ZhongwenLens.Core.Study;

/// <summary>
/// Writes saved words as a tab-separated file that Anki imports directly.
/// </summary>
/// <remarks>
/// <para>
/// TSV rather than a real <c>.apkg</c>, and that is a deliberate trade. Generating an
/// <c>.apkg</c> means hand-writing Anki's internal SQLite schema, which changes between Anki
/// versions and fails silently when it drifts. The TSV import format has been stable for years,
/// and it leaves the user in control of which note type and deck the cards land in
/// (DESIGN.md §3.7).
/// </para>
/// <para>
/// The <c>#</c> header lines are Anki's own import directives: they tell it the separator, that
/// fields may contain HTML, and what each column is. With them present the user imports without
/// configuring anything.
/// </para>
/// </remarks>
public static class AnkiExporter
{
    public static readonly string[] Columns =
        ["Simplified", "Pinyin", "Meaning", "Context", "HSK", "Traditional", "MeasureWord"];

    /// <summary>Writes <paramref name="words"/> to <paramref name="path"/>. Returns the count.</summary>
    public static int Export(IEnumerable<SavedWord> words, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // UTF-8 with BOM: without it Anki on Windows can misread Han characters as mojibake.
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return Write(words, writer);
    }

    /// <summary>Writes to any TextWriter. Exposed so tests don't need the filesystem.</summary>
    public static int Write(IEnumerable<SavedWord> words, TextWriter writer)
    {
        writer.WriteLine("#separator:tab");
        writer.WriteLine("#html:true");
        writer.WriteLine($"#columns:{string.Join('\t', Columns)}");

        var count = 0;
        foreach (var word in words)
        {
            // Re-run the formatter even though saving already does it. Rows saved before
            // classifiers were split out still hold raw "CL:個|个[ge4]" in their senses, and a
            // flashcard is the last place that should surface. Harmless on already-clean rows.
            var formatted = SenseFormatter.Format(word.Senses);
            var classifiers = string.IsNullOrEmpty(word.Classifiers)
                ? formatted.Classifiers
                : word.Classifiers;

            writer.WriteLine(string.Join('\t',
                Escape(word.Simplified),
                Escape(word.PinyinMarks),
                Escape(FormatSenses(formatted.Senses)),
                Escape(word.HasContext ? word.SourceContext : string.Empty),
                Escape(FormatHsk(word.HskLevel)),
                Escape(word.Traditional ?? string.Empty),
                Escape(classifiers)));

            count++;
        }

        return count;
    }

    /// <summary>Numbered senses, as line breaks HTML so a card shows them stacked.</summary>
    private static string FormatSenses(IReadOnlyList<string> senses)
        => senses.Count switch
        {
            0 => string.Empty,
            1 => senses[0],
            _ => string.Join("<br>", senses.Select((sense, index) => $"{index + 1}. {sense}")),
        };

    private static string FormatHsk(int? level) => level switch
    {
        null => string.Empty,
        7 => "HSK 7-9",
        _ => $"HSK {level}",
    };

    /// <summary>
    /// Makes a value safe for one TSV cell. A literal tab would shift every later column into
    /// the wrong field, and a newline would split one card into two broken ones.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value
            .Replace("\t", " ", StringComparison.Ordinal)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal);
    }
}
