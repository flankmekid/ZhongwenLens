using System.Diagnostics;
using System.Text;
using ZhongwenLens.DataBuild;

Console.OutputEncoding = Encoding.UTF8;

var repoRoot = FindRepoRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("could not locate the repository root (no ZhongwenLens.sln above this directory)");
    return 1;
}

var rawDir = Path.Combine(repoRoot, "data", "raw");
var cedictPath = Path.Combine(rawDir, "cedict_ts.u8");
var jiebaPath = Path.Combine(rawDir, "jieba_dict.txt");
var hskPath = Path.Combine(rawDir, "hsk.json");
var outputPath = args.Length > 0 ? args[0] : Path.Combine(repoRoot, "data", "dictionary.db");

foreach (var (label, path) in new[]
         {
             ("cedict_ts.u8", cedictPath), ("jieba_dict.txt", jiebaPath), ("hsk.json", hskPath),
         })
{
    if (File.Exists(path)) continue;
    Console.Error.WriteLine($"missing {label} at {path}");
    Console.Error.WriteLine(@"run scripts\fetch-data.ps1 first");
    return 1;
}

var stopwatch = Stopwatch.StartNew();

Console.WriteLine("loading jieba frequency table...");
var frequencies = FrequencyTable.Load(jiebaPath);
Console.WriteLine($"  {frequencies.WordCount:N0} words, {frequencies.Total:N0} total occurrences");

Console.WriteLine("loading HSK levels...");
var hsk = HskTable.Load(hskPath);
Console.WriteLine($"  {hsk.Count:N0} graded words");

Console.WriteLine("writing dictionary.db...");
var stats = new CedictParser.ParseStats();

using var writer = new DictionaryWriter(outputPath);
var result = writer.WriteEntries(CedictParser.Parse(cedictPath, stats), frequencies, hsk);

Console.WriteLine("  creating indexes...");
writer.CreateIndexes();

writer.WriteMeta(new Dictionary<string, string>
{
    ["schema_version"] = "1",
    ["built_at"] = DateTimeOffset.UtcNow.ToString("O"),
    ["entry_count"] = result.Entries.ToString(),
    ["frequency_total"] = frequencies.Total.ToString(),
    ["sources"] = "CC-CEDICT (CC BY-SA 4.0); jieba dict.txt (MIT); complete-hsk-vocabulary (MIT)",
});

writer.Finish();
stopwatch.Stop();

Console.WriteLine();
Console.WriteLine($"entries          {result.Entries,9:N0}   (parser skipped {stats.Skipped:N0})");
Console.WriteLine($"char_words rows  {result.CharWordRows,9:N0}");
Console.WriteLine($"with HSK band    {result.WithHsk,9:N0}");
Console.WriteLine($"with frequency   {result.WithFrequency,9:N0}");
Console.WriteLine($"file size        {new FileInfo(outputPath).Length / 1024.0 / 1024.0,9:N1} MB");
Console.WriteLine($"elapsed          {stopwatch.Elapsed.TotalSeconds,9:N1}s");

if (stats.SkippedSamples.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("skipped lines (sample):");
    foreach (var sample in stats.SkippedSamples)
    {
        Console.WriteLine($"  {(sample.Length > 100 ? sample[..100] + "..." : sample)}");
    }
}

// Spot-checks. A row count proves the file isn't empty; it does not prove the data is
// right. These assert the specific properties the rest of the app depends on.
Console.WriteLine();
Console.WriteLine("spot-checks:");
var failures = 0;

failures += Check(writer, "你好 exists",
    "SELECT COUNT(*) FROM entries WHERE simplified = '你好'", n => n >= 1);
failures += Check(writer, "行 has 2+ readings (heteronym)",
    "SELECT COUNT(*) FROM entries WHERE simplified = '行'", n => n >= 2);
failures += Check(writer, "马马虎虎 is a single phrase entry",
    "SELECT COUNT(*) FROM entries WHERE simplified = '马马虎虎'", n => n >= 1);
failures += Check(writer, "segmenter ambiguity set present",
    "SELECT COUNT(DISTINCT simplified) FROM entries WHERE simplified IN ('研究','生命','起源','研究生')", n => n == 4);
failures += Check(writer, "字 indexes into char_words",
    "SELECT COUNT(*) FROM char_words WHERE character = '字'", n => n >= 20);
failures += Check(writer, "HSK 3.0 band 1 populated",
    "SELECT COUNT(*) FROM entries WHERE hsk_new = 1", n => n >= 100);
failures += Check(writer, "HSK 2.0 band 1 populated",
    "SELECT COUNT(*) FROM entries WHERE hsk_old = 1", n => n >= 100);
failures += Check(writer, "no blank pinyin_marks",
    "SELECT COUNT(*) FROM entries WHERE pinyin_marks IS NULL OR pinyin_marks = ''", n => n == 0);
failures += Check(writer, "radicals populated",
    "SELECT COUNT(*) FROM entries WHERE radical IS NOT NULL", n => n >= 1000);

Console.WriteLine();
if (failures > 0)
{
    Console.Error.WriteLine($"{failures} spot-check(s) FAILED - dictionary.db is not trustworthy");
    return 1;
}

Console.WriteLine($"dictionary.db written to {outputPath}");
Console.WriteLine("CC-CEDICT is CC BY-SA 4.0 - see THIRD-PARTY-NOTICES.md before distributing.");
return 0;

static int Check(DictionaryWriter writer, string label, string sql, Func<long, bool> predicate)
{
    var value = writer.QueryScalar(sql);
    var ok = predicate(value);
    Console.WriteLine($"  {(ok ? "pass" : "FAIL")}  {label,-42} (= {value:N0})");
    return ok ? 0 : 1;
}

static string? FindRepoRoot()
{
    // The .NET 10 SDK emits the XML-based .slnx by default, so both spellings are
    // accepted rather than assuming either one.
    string[] markers = ["ZhongwenLens.slnx", "ZhongwenLens.sln"];

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (markers.Any(m => File.Exists(Path.Combine(dir.FullName, m)))) return dir.FullName;
        dir = dir.Parent;
    }

    return null;
}
