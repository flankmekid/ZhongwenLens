namespace ZhongwenLens.App.Services;

/// <summary>
/// Locates <c>dictionary.db</c> and the OCR models.
/// </summary>
/// <remarks>
/// Search order is deliberate: an explicit environment variable wins, then the per-user data
/// directory an installed build would use, then the repository's <c>data/</c> folder. The last
/// one is what makes F5 work during development without a copy step or a post-build task.
/// </remarks>
public static class DataPaths
{
    private const string OverrideVariable = "ZHONGWENLENS_DATA";

    public static string DataDirectory { get; } = Resolve();

    public static string DictionaryDatabase => Path.Combine(DataDirectory, "dictionary.db");

    public static string ModelDirectory => Path.Combine(DataDirectory, "models");

    /// <summary>Per-user directory for settings and the saved-word store.</summary>
    public static string UserDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZhongwenLens");

    public static bool IsComplete =>
        File.Exists(DictionaryDatabase)
        && File.Exists(Path.Combine(ModelDirectory, "det.onnx"))
        && File.Exists(Path.Combine(ModelDirectory, "rec.onnx"))
        && File.Exists(Path.Combine(ModelDirectory, "cls.onnx"))
        && File.Exists(Path.Combine(ModelDirectory, "ppocr_keys_v1.txt"));

    /// <summary>Actionable message naming what's missing and how to produce it.</summary>
    public static string DescribeMissing()
    {
        var missing = new List<string>();
        if (!File.Exists(DictionaryDatabase)) missing.Add("dictionary.db");
        foreach (var model in new[] { "det.onnx", "rec.onnx", "cls.onnx", "ppocr_keys_v1.txt" })
        {
            if (!File.Exists(Path.Combine(ModelDirectory, model))) missing.Add(model);
        }

        return $"""
            Missing data in {DataDirectory}:
              {string.Join(", ", missing)}

            Run from the repository root:
              pwsh -File scripts/fetch-data.ps1
              pwsh -File scripts/fetch-models.ps1
              dotnet run --project src/ZhongwenLens.DataBuild
            """;
    }

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        // Alongside the executable. This is the shipped layout: an MSIX install puts the
        // dictionary and models in the package folder, where the repository walk below can
        // never reach because there is no solution file above it.
        var installed = Path.Combine(AppContext.BaseDirectory, "data");
        if (File.Exists(Path.Combine(installed, "dictionary.db"))) return installed;

        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZhongwenLens", "data");
        if (File.Exists(Path.Combine(userData, "dictionary.db"))) return userData;

        var repository = FindRepositoryData();
        return repository ?? userData;
    }

    private static string? FindRepositoryData()
    {
        string[] markers = ["ZhongwenLens.slnx", "ZhongwenLens.sln"];

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (markers.Any(m => File.Exists(Path.Combine(directory.FullName, m))))
            {
                return Path.Combine(directory.FullName, "data");
            }

            directory = directory.Parent;
        }

        return null;
    }
}
