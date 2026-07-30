using System.Text.Json;

namespace ZhongwenLens.DataBuild;

/// <summary>HSK banding and radical for one headword.</summary>
/// <param name="NewLevel">HSK 3.0 band: 1-6, or 7 for the combined 7-9 band. Null if unlisted.</param>
/// <param name="OldLevel">HSK 2.0 band: 1-6. Null if unlisted.</param>
/// <param name="Radical">Character radical, used by the single-character view (DESIGN.md §3.4).</param>
public sealed record HskInfo(int? NewLevel, int? OldLevel, string? Radical);

/// <summary>
/// Loads HSK bands from complete-hsk-vocabulary's <c>complete.json</c>.
/// </summary>
/// <remarks>
/// That file tags each word against three different schemes at once — "old-N" (HSK 2.0),
/// "new-N" (HSK 3.0, 2021) and "newest-N" (the 2024 revision). Both HSK 2.0 and 3.0 are
/// kept in separate columns rather than collapsed, because learners are split across the
/// two standards and silently showing one as if it were the other would be misleading.
/// "newest-N" is treated as a fallback source for the 3.0 band.
/// </remarks>
public static class HskTable
{
    public static Dictionary<string, HskInfo> Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var result = new Dictionary<string, HskInfo>(12_000, StringComparer.Ordinal);

        foreach (var word in document.RootElement.EnumerateArray())
        {
            if (!word.TryGetProperty("simplified", out var simplifiedProp)) continue;
            var simplified = simplifiedProp.GetString();
            if (string.IsNullOrEmpty(simplified)) continue;

            int? newLevel = null, newestLevel = null, oldLevel = null;

            if (word.TryGetProperty("level", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                foreach (var level in levels.EnumerateArray())
                {
                    var tag = level.GetString();
                    if (string.IsNullOrEmpty(tag)) continue;

                    var dash = tag.LastIndexOf('-');
                    if (dash < 0 || !int.TryParse(tag.AsSpan(dash + 1), out var band)) continue;

                    switch (tag[..dash])
                    {
                        // Keep the lowest band seen: a word listed in several bands is
                        // introduced at the earliest one, which is what a learner wants.
                        case "new":     newLevel    = Min(newLevel, band);    break;
                        case "newest":  newestLevel = Min(newestLevel, band); break;
                        case "old":     oldLevel    = Min(oldLevel, band);    break;
                    }
                }
            }

            string? radical = word.TryGetProperty("radical", out var radicalProp)
                ? radicalProp.GetString()
                : null;

            result[simplified] = new HskInfo(newLevel ?? newestLevel, oldLevel, EmptyToNull(radical));
        }

        return result;
    }

    private static int? Min(int? current, int candidate)
        => current is null ? candidate : Math.Min(current.Value, candidate);

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
