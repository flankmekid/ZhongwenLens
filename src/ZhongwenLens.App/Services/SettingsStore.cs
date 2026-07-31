using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZhongwenLens.App.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in the user's data directory.
/// </summary>
/// <remarks>
/// Lives beside <c>study.db</c> in <c>%LOCALAPPDATA%\ZhongwenLens</c>, not in the install
/// directory: settings have to survive an upgrade or an uninstall, and the install directory
/// does not.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public SettingsStore(string? path = null)
        => _path = path ?? Path.Combine(DataPaths.UserDirectory, "settings.json");

    public string FilePath => _path;

    /// <summary>Reads the settings file, falling back to defaults if it's missing or unreadable.</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new AppSettings();

                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options)
                       ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A corrupt settings file must not stop the app starting. Defaults are always
                // usable, and the next save overwrites the bad file.
                Log.Error("load settings", ex);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(DataPaths.UserDirectory);

                // Written to a temporary file and moved into place, so a crash or a full disk
                // mid-write leaves the previous settings intact rather than a truncated file.
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("save settings", ex);
            }
        }
    }
}
