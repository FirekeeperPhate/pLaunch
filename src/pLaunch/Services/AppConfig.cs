using System.Text.Json;

namespace pLaunch.Services;

/// <summary>
/// Settings of the pLaunch installation rather than of a list: where the lists live and the update
/// checks. Stored in %AppData%\pLaunch\app.json (PLAUNCH_DATA_DIR moves it too, for tests), which never
/// moves, so it can point the lists to another folder (e.g. one synced by OneDrive).
/// </summary>
public sealed class AppConfig
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Folder of the lists; null = the configuration folder.</summary>
    public string? DataDirectory { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>A version the user chose to skip ("1.2.0").</summary>
    public string? SkippedVersion { get; set; }

    static string? _envDirectory = Environment.GetEnvironmentVariable("PLAUNCH_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>Where app.json, errors.log, the favicon cache and the relaunch list are kept.</summary>
    public static string ConfigDirectory { get; } = _envDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pLaunch");

    static string FilePath => Path.Combine(ConfigDirectory, "app.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken or locked app.json: defaults (the lists stay in the configuration folder)
        }
        return new AppConfig();
    }

    /// <summary>Re-reads, applies <paramref name="change"/> and writes, so several lists do not undo each other's changes.</summary>
    public static AppConfig Update(Action<AppConfig> change)
    {
        var config = Load();
        change(config);
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Cannot save app.json", ex);
        }
        return config;
    }

    /// <summary>The folder of the lists: PLAUNCH_DATA_DIR, else app.json's choice, else the configuration folder.</summary>
    public static string DataDirectoryPath =>
        _envDirectory ?? (Load().DataDirectory is { Length: > 0 } custom ? custom : ConfigDirectory);
}
