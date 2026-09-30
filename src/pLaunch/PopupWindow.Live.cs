using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch;

// Live folders: a folder of the disk shown inside the popup, read from the disk each time it opens
public partial class PopupWindow
{
    readonly Dictionary<string, List<LaunchItem>> _liveCache = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _liveLoading = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _liveTruncated = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _liveErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The level shown is the content of a live folder (read-only).</summary>
    bool InLiveFolder => _path.Count > 0 && _path[^1].Kind == ItemKind.Folder;

    /// <summary>
    /// The entries of a live folder: from the cache, or empty while they are read in the background (a
    /// network folder may take a while); the list refreshes when they arrive.
    /// </summary>
    List<LaunchItem> LiveEntries(LaunchItem folder)
    {
        var path = folder.Target;
        if (_liveCache.TryGetValue(path, out var cached))
            return cached;
        if (_liveLoading.Add(path))
            LoadLiveFolder(path);
        return [];
    }

    void LoadLiveFolder(string path)
    {
        _liveErrors.Remove(path);
        Task.Run(() =>
        {
            try
            {
                var (list, truncated) = LiveFolder.List(path);
                return (Items: list, Truncated: truncated, Error: (string?)null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                return (Items: new List<LaunchItem>(), Truncated: false, Error: ex.Message);
            }
        }).ContinueWith(t => Dispatcher.BeginInvoke(() => LiveFolderLoaded(path, t.Result.Items, t.Result.Truncated, t.Result.Error)),
            TaskScheduler.Default);
    }

    // Back on the window's thread, whatever context started the reading
    void LiveFolderLoaded(string path, List<LaunchItem> items, bool truncated, string? error)
    {
        if (error != null)
            _liveErrors[path] = error;
        if (truncated)
            _liveTruncated.Add(path);
        else
            _liveTruncated.Remove(path);
        _liveLoading.Remove(path);
        _liveCache[path] = items;
        if (InLiveFolder && string.Equals(_path[^1].Target, path, StringComparison.OrdinalIgnoreCase))
        {
            Refresh();
            RefreshMissing();
        }
    }

    string LiveFolderHint()
    {
        var path = _path[^1].Target;
        return _liveLoading.Contains(path) ? "Reading the folder\x2026"
            : _liveErrors.TryGetValue(path, out var error) ? $"This folder cannot be read.\n{error}"
            : "This folder is empty.";
    }

    /// <summary>
    /// The saved level that holds the outermost live folder being browsed: "Add to pLaunch" puts copies
    /// of live entries there.
    /// </summary>
    List<LaunchItem> RealLevel()
    {
        int firstLive = _path.FindIndex(p => p.Kind == ItemKind.Folder);
        if (firstLive <= 0)
            return _root;
        return _path[firstLive - 1].Children ??= [];
    }

    /// <summary>A live entry becomes a saved shortcut (in the list that holds the live folder).</summary>
    void AddLiveCopy(LaunchItem live)
    {
        if (ItemFactory.FromPath(live.Target) is not { } copy)
            return;
        var level = RealLevel();
        if (Insert(level, level.Count, [copy]).Count == 0)
            return;
        Save();
        ShowModal(() => System.Windows.MessageBox.Show(this,
            $"\"{copy.Name}\" is now in the list{(level == _root ? "" : ", in the sub-folder that holds this folder")}.",
            "pLaunch", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information));
    }
}
