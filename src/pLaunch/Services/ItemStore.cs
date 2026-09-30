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

    public List<LaunchItem> Load()
    {
        if (!File.Exists(FilePath))
            return [];
        try
        {
            var doc = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(FilePath), JsonOptions);
            return doc?.Items?.Where(i => !string.IsNullOrWhiteSpace(i.Target)).ToList() ?? [];
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection instead of overwriting it on the next save
            try { File.Move(FilePath, FilePath + ".bad", overwrite: true); } catch (IOException) { }
            return [];
        }
    }

    public void Save(IEnumerable<LaunchItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(new StoreDocument { Items = items.ToList() }, JsonOptions);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    sealed class StoreDocument
    {
        public int Version { get; set; } = 1;
        public List<LaunchItem>? Items { get; set; }
    }
}
