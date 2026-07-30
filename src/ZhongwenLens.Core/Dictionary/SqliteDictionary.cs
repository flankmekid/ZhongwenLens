using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZhongwenLens.Core.Text;

namespace ZhongwenLens.Core.Dictionary;

/// <summary>
/// Read-only access to <c>dictionary.db</c>, serving both dictionary lookups and the
/// segmenter's vocabulary (DESIGN.md §3.3, §3.4).
/// </summary>
/// <remarks>
/// One class implements both interfaces on purpose: it is what guarantees the segmenter and
/// the dictionary can never disagree about what a word is, so the UI can't be handed a
/// token it has no entry for.
/// </remarks>
public sealed class SqliteDictionary : IDictionaryService, IWordLexicon, IDisposable
{
    private const string EntryColumns =
        "id, traditional, simplified, pinyin_numbered, pinyin_marks, senses, " +
        "hsk_new, hsk_old, radical, frequency";

    /// <summary>
    /// Longest substring the segmenter will probe for. CC-CEDICT contains entries far
    /// longer than this — full idiom explanations and proper nouns — but they are not
    /// plausible segmentation candidates, and probing to their length would cost a
    /// lookup per character for no gain. Exact matches of any length still resolve
    /// through <see cref="Lookup"/>, which goes to SQL.
    /// </summary>
    public const int DefaultMaxWordLength = 16;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _vocabulary;
    private readonly Dictionary<string, long>.AlternateLookup<ReadOnlySpan<char>> _vocabularyLookup;

    private SqliteDictionary(SqliteConnection connection, int maxWordLength)
    {
        _connection = connection;
        MaxWordLength = maxWordLength;

        _vocabulary = LoadVocabulary(connection, maxWordLength, out var total, out var entryCount);
        _vocabularyLookup = _vocabulary.GetAlternateLookup<ReadOnlySpan<char>>();
        TotalFrequency = Math.Max(1L, total);
        EntryCount = entryCount;
    }

    public static SqliteDictionary Open(string path, int maxWordLength = DefaultMaxWordLength)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"dictionary.db not found at '{path}'. Run scripts\\fetch-data.ps1 then " +
                "dotnet run --project src\\ZhongwenLens.DataBuild.", path);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());

        connection.Open();
        return new SqliteDictionary(connection, maxWordLength);
    }

    public int EntryCount { get; }

    public int MaxWordLength { get; }

    public long TotalFrequency { get; }

    /// <summary>Distinct segmentable spellings held in memory.</summary>
    public int VocabularySize => _vocabulary.Count;

    /// <summary>
    /// Loads every headword into memory once at startup. Segmentation probes up to
    /// <see cref="MaxWordLength"/> candidates per character, so serving those from SQL
    /// would put thousands of queries on the path of a single snip.
    /// </summary>
    private static Dictionary<string, long> LoadVocabulary(
        SqliteConnection connection, int maxWordLength, out long total, out int entryCount)
    {
        var vocabulary = new Dictionary<string, long>(200_000, StringComparer.Ordinal);
        total = 0L;
        entryCount = 0;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT simplified, traditional, frequency FROM entries";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entryCount++;

            var simplified = reader.GetString(0);
            var traditional = reader.GetString(1);
            var frequency = reader.GetInt64(2);

            // Both scripts are segmentable, so traditional text works without a
            // conversion step. Frequency is carried over from the simplified form.
            var added = Add(vocabulary, simplified, frequency, maxWordLength);
            if (!string.Equals(simplified, traditional, StringComparison.Ordinal))
            {
                Add(vocabulary, traditional, frequency, maxWordLength);
            }

            // Counted once per distinct simplified spelling: heteronyms are several
            // entries for one word and must not inflate the probability denominator.
            if (added) total += frequency;
        }

        return vocabulary;
    }

    private static bool Add(Dictionary<string, long> vocabulary, string word, long frequency, int maxWordLength)
    {
        if (word.Length == 0 || word.Length > maxWordLength) return false;

        if (vocabulary.TryGetValue(word, out var existing))
        {
            if (frequency > existing) vocabulary[word] = frequency;
            return false;
        }

        vocabulary[word] = frequency;
        return true;
    }

    public long GetFrequency(ReadOnlySpan<char> word)
        => _vocabularyLookup.TryGetValue(word, out var frequency) ? frequency : -1L;

    public bool IsKnownWord(string? word)
        => !string.IsNullOrEmpty(word) && _vocabulary.ContainsKey(word);

    public IReadOnlyList<DictEntry> Lookup(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return [];

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {EntryColumns} FROM entries
                WHERE simplified = $word OR traditional = $word
                ORDER BY frequency DESC, id
                """;
            command.Parameters.AddWithValue("$word", word);

            return RankForLearners(ReadEntries(command));
        }
    }

    /// <summary>
    /// Puts the reading a learner most likely wants first.
    /// </summary>
    /// <remarks>
    /// Frequency is stored per spelling, so every reading of a word shares it and ties break on
    /// row id — which is arbitrary. That produced real nonsense: 书 led with "abbr. for 書經"
    /// and the capitalised reading Shū, rather than shū meaning "book". Surnames and
    /// abbreviations are almost never the sense being looked up, so they sort last while
    /// everything else keeps its existing order.
    /// </remarks>
    private static List<DictEntry> RankForLearners(List<DictEntry> entries)
    {
        if (entries.Count < 2) return entries;

        return entries.OrderBy(IsNicheReading).ToList();     // OrderBy is a stable sort
    }

    private static int IsNicheReading(DictEntry entry)
    {
        var sense = entry.PrimarySense;

        return sense.StartsWith("abbr. for", StringComparison.OrdinalIgnoreCase)
            || sense.StartsWith("surname ", StringComparison.OrdinalIgnoreCase)
            || sense.StartsWith("variant of", StringComparison.OrdinalIgnoreCase)
            || sense.StartsWith("old variant of", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
    }

    public IReadOnlyList<DictEntry> WordsContaining(char character, int limit = 12)
    {
        if (!CharClassifier.IsHan(character) || limit <= 0) return [];

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {string.Join(", ", EntryColumns.Split(", ").Select(c => "e." + c))}
                FROM char_words cw
                JOIN entries e ON e.id = cw.entry_id
                WHERE cw.character = $char
                ORDER BY cw.frequency DESC, e.id
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$char", character.ToString());
            command.Parameters.AddWithValue("$limit", limit);

            return ReadEntries(command);
        }
    }

    /// <summary>Value from the <c>meta</c> table, or null when absent.</summary>
    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    private static List<DictEntry> ReadEntries(SqliteCommand command)
    {
        var results = new List<DictEntry>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new DictEntry(
                Id: reader.GetInt32(0),
                Traditional: reader.GetString(1),
                Simplified: reader.GetString(2),
                PinyinNumbered: reader.GetString(3),
                PinyinMarks: reader.GetString(4),
                Senses: DeserializeSenses(reader.GetString(5)),
                HskNew: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                HskOld: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Radical: reader.IsDBNull(8) ? null : reader.GetString(8),
                Frequency: reader.GetInt64(9)));
        }

        return results;
    }

    private static IReadOnlyList<string> DeserializeSenses(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            // A malformed row shouldn't take down a lookup; show the raw text instead.
            return [json];
        }
    }

    public void Dispose() => _connection.Dispose();
}
