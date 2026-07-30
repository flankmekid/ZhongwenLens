using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ZhongwenLens.Core.Study;

public interface IStudyStore : IDisposable
{
    int Count { get; }

    /// <summary>Adds a word, or refreshes it if already saved. Returns true when newly added.</summary>
    bool Save(SavedWord word);

    bool Remove(string simplified);

    bool Contains(string simplified);

    /// <summary>All saved words, most recently saved first.</summary>
    IReadOnlyList<SavedWord> GetAll();
}

/// <summary>
/// The user's starred words, in their own SQLite file.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>dictionary.db</c>: that file is regenerated from scratch every
/// time <c>DataBuild</c> runs, and the user's own data must never be inside something the build
/// deletes (DESIGN.md §3.7).
/// </remarks>
public sealed class SqliteStudyStore : IStudyStore
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    private SqliteStudyStore(SqliteConnection connection)
    {
        _connection = connection;
        CreateSchema();
    }

    public static SqliteStudyStore Open(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();
        return new SqliteStudyStore(connection);
    }

    private void CreateSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS saved_words (
                id             INTEGER PRIMARY KEY,
                simplified     TEXT NOT NULL UNIQUE,
                traditional    TEXT,
                pinyin_marks   TEXT NOT NULL,
                senses         TEXT NOT NULL,   -- JSON array
                hsk_level      INTEGER,
                source_context TEXT,
                saved_at       TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_saved_at ON saved_words(saved_at DESC);
            """;
        command.ExecuteNonQuery();

        AddColumnIfMissing("classifiers", "TEXT");
    }

    /// <summary>
    /// Adds a column to an existing store. This file belongs to the user and is never
    /// regenerated, so a schema change has to migrate in place rather than rebuild — SQLite has
    /// no "ADD COLUMN IF NOT EXISTS", hence the check.
    /// </summary>
    private void AddColumnIfMissing(string column, string type)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('saved_words') WHERE name = $name";
        check.Parameters.AddWithValue("$name", column);

        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE saved_words ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM saved_words";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
    }

    public bool Save(SavedWord word)
    {
        if (string.IsNullOrWhiteSpace(word.Simplified)) return false;

        lock (_gate)
        {
            var existed = ContainsCore(word.Simplified);

            using var command = _connection.CreateCommand();

            // Upsert rather than insert-or-ignore: saving the same word again from a different
            // sentence should keep the newer context, which is the more recent encounter.
            command.CommandText = """
                INSERT INTO saved_words
                    (simplified, traditional, pinyin_marks, senses, hsk_level, source_context,
                     saved_at, classifiers)
                VALUES ($simplified, $traditional, $pinyin, $senses, $hsk, $context,
                        $savedAt, $classifiers)
                ON CONFLICT(simplified) DO UPDATE SET
                    traditional    = excluded.traditional,
                    pinyin_marks   = excluded.pinyin_marks,
                    senses         = excluded.senses,
                    hsk_level      = excluded.hsk_level,
                    source_context = excluded.source_context,
                    saved_at       = excluded.saved_at,
                    classifiers    = excluded.classifiers;
                """;

            command.Parameters.AddWithValue("$simplified", word.Simplified);
            command.Parameters.AddWithValue("$traditional", (object?)word.Traditional ?? DBNull.Value);
            command.Parameters.AddWithValue("$pinyin", word.PinyinMarks);
            command.Parameters.AddWithValue("$senses", JsonSerializer.Serialize(word.Senses));
            command.Parameters.AddWithValue("$hsk", (object?)word.HskLevel ?? DBNull.Value);
            command.Parameters.AddWithValue("$context", (object?)word.SourceContext ?? DBNull.Value);
            command.Parameters.AddWithValue("$savedAt", word.SavedAt.ToString("O"));
            command.Parameters.AddWithValue("$classifiers",
                string.IsNullOrEmpty(word.Classifiers) ? DBNull.Value : word.Classifiers);

            command.ExecuteNonQuery();
            return !existed;
        }
    }

    public bool Remove(string simplified)
    {
        if (string.IsNullOrWhiteSpace(simplified)) return false;

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM saved_words WHERE simplified = $simplified";
            command.Parameters.AddWithValue("$simplified", simplified);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool Contains(string simplified)
    {
        if (string.IsNullOrWhiteSpace(simplified)) return false;

        lock (_gate)
        {
            return ContainsCore(simplified);
        }
    }

    private bool ContainsCore(string simplified)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM saved_words WHERE simplified = $simplified LIMIT 1";
        command.Parameters.AddWithValue("$simplified", simplified);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<SavedWord> GetAll()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT simplified, traditional, pinyin_marks, senses, hsk_level, source_context,
                       saved_at, classifiers
                FROM saved_words
                ORDER BY saved_at DESC, id DESC
                """;

            var results = new List<SavedWord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SavedWord(
                    Simplified: reader.GetString(0),
                    Traditional: reader.IsDBNull(1) ? null : reader.GetString(1),
                    PinyinMarks: reader.GetString(2),
                    Senses: DeserializeSenses(reader.GetString(3)),
                    HskLevel: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    SourceContext: reader.IsDBNull(5) ? null : reader.GetString(5),
                    SavedAt: ParseTimestamp(reader.GetString(6)),
                    Classifiers: reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return results;
        }
    }

    private static IReadOnlyList<string> DeserializeSenses(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [json];
        }
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.TryParse(value, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    public void Dispose() => _connection.Dispose();
}
