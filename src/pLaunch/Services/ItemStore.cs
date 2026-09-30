using System.Text.Json;
using System.Text.Json.Serialization;
using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>Persists the launcher items as JSON (%AppData%\pLaunch\items.json).</summary>
public sealed class ItemStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%AppData%\pLaunch, or PLAUNCH_DATA_DIR when set (tests, portable setups).</summary>
    public static string DefaultDirectory { get; } =
        Environment.GetEnvironmentVariable("PLAUNCH_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pLaunch");

    public string FilePath { get; }

    public ItemStore(string filePath) => FilePath = filePath;

    public static ItemStore CreateDefault() => new(Path.Combine(DefaultDirectory, "items.json"));

    /// <summary>
    /// Set when the file exists but could not be read: the list starts empty and <see cref="Save"/>
    /// refuses to overwrite the file, which still holds the real list.
    /// </summary>
    public string? LoadError { get; private set; }

    public StoreData Load()
    {
        LoadError = null;
        if (!File.Exists(FilePath))
            return StoreData.Empty();
        string json;
        try
        {
            json = ReadWithRetry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = ex.Message;
            return StoreData.Empty();
        }
        try
        {
            // Version 1 files have no settings and no sub-folders: they load as they are
            var doc = JsonSerializer.Deserialize<StoreDocument>(json, JsonOptions);
            return new StoreData(Sanitize(doc?.Items), doc?.Settings ?? new LauncherSettings());
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection instead of overwriting it on the next save
            try { File.Move(FilePath, FilePath + ".bad", overwrite: true); } catch (IOException) { }
            return StoreData.Empty();
        }
    }

    /// <summary>Drops entries that cannot work (no target) at every level; sub-folders keep their content.</summary>
    static List<LaunchItem> Sanitize(List<LaunchItem>? items)
    {
        var result = new List<LaunchItem>();
        foreach (var item in items ?? [])
        {
            if (item == null)
                continue;
            switch (item.Kind)
            {
                case ItemKind.Separator:
                    item.Children = null;
                    result.Add(item);
                    break;
                case ItemKind.Group:
                    item.Children = Sanitize(item.Children);
                    if (string.IsNullOrWhiteSpace(item.Name))
                        item.Name = "Folder";
                    result.Add(item);
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(item.Target))
                        continue;
                    item.Children = null;
                    result.Add(item);
                    break;
            }
        }
        return result;
    }

    /// <summary>Reads of a locked file, 200 ms apart (lower in tests).</summary>
    internal int ReadAttempts { get; set; } = 10;

    // At logon a sync client or an antivirus may hold the file for a moment
    string ReadWithRetry()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(FilePath);
            }
            catch (IOException) when (attempt < ReadAttempts && File.Exists(FilePath))
            {
                Thread.Sleep(200);
            }
        }
    }

    public void Save(IReadOnlyList<LaunchItem> items, LauncherSettings settings)
    {
        if (LoadError != null)
            throw new IOException($"The saved list could not be read at startup ({LoadError}), so it is not overwritten. Restart pLaunch to try again.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(new StoreDocument { Settings = settings, Items = items.ToList() }, JsonOptions);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    sealed class StoreDocument
    {
        public int Version { get; set; } = 2;
        public LauncherSettings? Settings { get; set; }
        public List<LaunchItem>? Items { get; set; }
    }
}

public sealed record StoreData(List<LaunchItem> Items, LauncherSettings Settings)
{
    public static StoreData Empty() => new([], new LauncherSettings());
}
