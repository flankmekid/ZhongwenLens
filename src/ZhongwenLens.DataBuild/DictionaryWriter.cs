using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZhongwenLens.Core.Text;

namespace ZhongwenLens.DataBuild;

/// <summary>
/// Writes the parsed sources into <c>dictionary.db</c>. Schema per DESIGN.md §3.4.
/// </summary>
public sealed class DictionaryWriter : IDisposable
{
    /// <summary>
    /// Words longer than this aren't indexed into <c>char_words</c>. Long CC-CEDICT
    /// entries are mostly proper nouns and full idiom explanations, which aren't useful
    /// as "words containing this character" examples.
    /// </summary>
    private const int MaxCharWordLength = 6;

    private readonly SqliteConnection _connection;

    public DictionaryWriter(string path)
    {
        if (File.Exists(path)) File.Delete(path);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();

        // Build-time only: this file is regenerated from scratch on every run, so
        // durability guarantees buy nothing and cost a great deal of wall time.
        Execute("PRAGMA journal_mode = OFF");
        Execute("PRAGMA synchronous = OFF");
        Execute("PRAGMA temp_store = MEMORY");

        CreateSchema();
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE entries (
                id              INTEGER PRIMARY KEY,
                traditional     TEXT    NOT NULL,
                simplified      TEXT    NOT NULL,
                pinyin_numbered TEXT    NOT NULL,
                pinyin_marks    TEXT    NOT NULL,
                senses          TEXT    NOT NULL,  -- JSON array of strings
                hsk_new         INTEGER,           -- HSK 3.0: 1-6, 7 = the 7-9 band
                hsk_old         INTEGER,           -- HSK 2.0: 1-6
                radical         TEXT,
                frequency       INTEGER NOT NULL DEFAULT 0
            );

            -- Reverse index for "common words using this character" on the
            -- single-character view. A leading-wildcard LIKE '%X%' cannot use an index
            -- and would scan every row per snip.
            CREATE TABLE char_words (
                character TEXT    NOT NULL,
                entry_id  INTEGER NOT NULL,
                frequency INTEGER NOT NULL
            );
            """);
    }

    /// <summary>
    /// Inserts every entry in a single transaction. One transaction for ~125k rows is the
    /// difference between seconds and many minutes.
    /// </summary>
    public WriteResult WriteEntries(
        IEnumerable<CedictEntry> entries,
        FrequencyTable frequencies,
        Dictionary<string, HskInfo> hsk)
    {
        using var transaction = _connection.BeginTransaction();

        using var insertEntry = _connection.CreateCommand();
        insertEntry.CommandText = """
            INSERT INTO entries
                (id, traditional, simplified, pinyin_numbered, pinyin_marks, senses,
                 hsk_new, hsk_old, radical, frequency)
            VALUES ($id, $trad, $simp, $numbered, $marks, $senses,
                    $hskNew, $hskOld, $radical, $frequency);
            """;
        var pId        = insertEntry.Parameters.Add("$id", SqliteType.Integer);
        var pTrad      = insertEntry.Parameters.Add("$trad", SqliteType.Text);
        var pSimp      = insertEntry.Parameters.Add("$simp", SqliteType.Text);
        var pNumbered  = insertEntry.Parameters.Add("$numbered", SqliteType.Text);
        var pMarks     = insertEntry.Parameters.Add("$marks", SqliteType.Text);
        var pSenses    = insertEntry.Parameters.Add("$senses", SqliteType.Text);
        var pHskNew    = insertEntry.Parameters.Add("$hskNew", SqliteType.Integer);
        var pHskOld    = insertEntry.Parameters.Add("$hskOld", SqliteType.Integer);
        var pRadical   = insertEntry.Parameters.Add("$radical", SqliteType.Text);
        var pFrequency = insertEntry.Parameters.Add("$frequency", SqliteType.Integer);

        using var insertCharWord = _connection.CreateCommand();
        insertCharWord.CommandText =
            "INSERT INTO char_words (character, entry_id, frequency) VALUES ($char, $entry, $freq);";
        var pChar      = insertCharWord.Parameters.Add("$char", SqliteType.Text);
        var pCharEntry = insertCharWord.Parameters.Add("$entry", SqliteType.Integer);
        var pCharFreq  = insertCharWord.Parameters.Add("$freq", SqliteType.Integer);

        var id = 0;
        var charWordRows = 0;
        var withHsk = 0;
        var withFrequency = 0;

        foreach (var entry in entries)
        {
            id++;

            var info = hsk.GetValueOrDefault(entry.Simplified);
            var frequency = frequencies[entry.Simplified];

            pId.Value        = id;
            pTrad.Value      = entry.Traditional;
            pSimp.Value      = entry.Simplified;
            pNumbered.Value  = entry.PinyinNumbered;
            // Precomputed at build time so the app never converts on the hot path.
            pMarks.Value     = PinyinConverter.ToToneMarks(entry.PinyinNumbered);
            pSenses.Value    = JsonSerializer.Serialize(entry.Senses);
            pHskNew.Value    = (object?)info?.NewLevel ?? DBNull.Value;
            pHskOld.Value    = (object?)info?.OldLevel ?? DBNull.Value;
            pRadical.Value   = (object?)info?.Radical ?? DBNull.Value;
            pFrequency.Value = frequency;

            insertEntry.ExecuteNonQuery();

            if (info?.NewLevel is not null || info?.OldLevel is not null) withHsk++;
            if (frequency > 0) withFrequency++;

            charWordRows += IndexCharacters(entry, id, frequency, insertCharWord, pChar, pCharEntry, pCharFreq);
        }

        transaction.Commit();

        return new WriteResult(id, charWordRows, withHsk, withFrequency);
    }

    private static int IndexCharacters(
        CedictEntry entry, int id, long frequency,
        SqliteCommand command, SqliteParameter pChar, SqliteParameter pEntry, SqliteParameter pFreq)
    {
        var word = entry.Simplified;
        if (word.Length is < 2 or > MaxCharWordLength) return 0;

        var rows = 0;
        // Distinct characters only: 妈妈 shouldn't list itself twice under 妈.
        HashSet<char>? seen = null;

        foreach (var c in word)
        {
            if (!IsHan(c)) continue;

            seen ??= [];
            if (!seen.Add(c)) continue;

            pChar.Value = c.ToString();
            pEntry.Value = id;
            pFreq.Value = frequency;
            command.ExecuteNonQuery();
            rows++;
        }

        return rows;
    }

    private static bool IsHan(char c)
        => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    /// <summary>
    /// Indexes are created after the bulk insert, not before: building them incrementally
    /// across 125k inserts is markedly slower than sorting once at the end.
    /// </summary>
    public void CreateIndexes()
    {
        Execute("""
            CREATE INDEX idx_entries_simplified  ON entries(simplified);
            CREATE INDEX idx_entries_traditional ON entries(traditional);
            CREATE INDEX idx_char_words          ON char_words(character, frequency DESC);
            """);
    }

    public void WriteMeta(IReadOnlyDictionary<string, string> values)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);";
        var pKey = command.Parameters.Add("$key", SqliteType.Text);
        var pValue = command.Parameters.Add("$value", SqliteType.Text);

        foreach (var (key, value) in values)
        {
            pKey.Value = key;
            pValue.Value = value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Finish()
    {
        Execute("ANALYZE");
        Execute("VACUUM");
    }

    public long QueryScalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    public sealed record WriteResult(int Entries, int CharWordRows, int WithHsk, int WithFrequency);
}
