using System.Text.Json;
using System.Text.Json.Serialization;
using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>Persists a list as JSON (items.json for the default list, lists\&lt;name&gt;.json for the others).</summary>
public sealed class ItemStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Not WhenWritingDefault: settings that default to true (Translucent, WebIcons) would be dropped
        // when false and come back true
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public ItemStore(string filePath) => FilePath = filePath;

    /// <summary>The store of a list in the current data folder.</summary>
    public static ItemStore For(ListProfile profile) => new(profile.FilePath(AppConfig.DataDirectoryPath));

    /// <summary>
    /// Hash of the content last read or written by this store: a change notification whose content
    /// differs came from elsewhere (another PC through a synced folder, an editor).
    /// </summary>
    public string? LastContentHash { get; private set; }

    public static string HashOf(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Current hash of the file, or null when it cannot be read right now.</summary>
    public string? ReadFileHash()
    {
        try { return File.Exists(FilePath) ? HashOf(File.ReadAllText(FilePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Set when the file exists but could not be read: the list starts empty and <see cref="Save"/>
    /// refuses to overwrite the file, which still holds the real list.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>
    /// The last <see cref="Load"/> had to give items new ids (missing or repeated ones): the caller that
    /// owns the list should save it, so the file matches what the jump list refers to.
    /// </summary>
    public bool IdsRepaired { get; private set; }

    public StoreData Load()
    {
        LoadError = null;
        IdsRepaired = false;
        if (!File.Exists(FilePath))
            return StoreData.Empty();
        string json;
        try
        {
            json = ReadWithRetry();
            LastContentHash = HashOf(json);
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

    /// <summary>
    /// Drops entries that cannot work (no target) at every level; sub-folders keep their content.
    /// Missing or repeated ids (a hand-edited or merged file) get new ones: the popup, moves and the
    /// jump list ("--launch &lt;id&gt;") all find items by id. The new ids are derived from the file
    /// ("same" -> "same-2", missing -> "item"), so another process reading the same file (the one a jump
    /// list entry starts) comes to the same ids even before the list is saved again.
    /// </summary>
    List<LaunchItem> Sanitize(List<LaunchItem>? items, HashSet<string>? ids = null)
    {
        ids ??= [];
        var result = new List<LaunchItem>();
        foreach (var item in items ?? [])
        {
            if (item == null)
                continue;
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
            {
                var baseId = string.IsNullOrWhiteSpace(item.Id) ? "item" : item.Id;
                var candidate = baseId;
                for (int n = 2; !ids.Add(candidate); n++)
                    candidate = $"{baseId}-{n}";
                item.Id = candidate;
                IdsRepaired = true;
            }
            switch (item.Kind)
            {
                case ItemKind.Separator:
                    item.Children = null;
                    result.Add(item);
                    break;
                case ItemKind.Group:
                    item.Children = Sanitize(item.Children, ids);
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
        LastContentHash = HashOf(json);
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
