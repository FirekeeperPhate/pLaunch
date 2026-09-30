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

    /// <summary>
    /// A live entry or a search suggestion becomes a saved shortcut: in the list that holds the live
    /// folder, or in the level being shown.
    /// </summary>
    void AddLiveCopy(LaunchItem live)
    {
        if (SavedCopy(live) is not { } copy)
            return;
        bool fromLiveFolder = InLiveFolder;
        var level = fromLiveFolder ? RealLevel() : CurrentLevel;
        if (Insert(level, level.Count, [copy]).Count == 0)
            return;
        Save();
        var where = level == _root ? "" : fromLiveFolder ? ", in the sub-folder that holds this folder" : ", in this sub-folder";
        ShowModal(() => System.Windows.MessageBox.Show(this, $"\"{copy.Name}\" is now in the list{where}.",
            "pLaunch", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information));
    }

    /// <summary>A saved item for something made on the fly, named like an item added by hand.</summary>
    static LaunchItem? SavedCopy(LaunchItem live)
    {
        switch (live.Kind)
        {
            case ItemKind.File or ItemKind.Folder:
                // A network path is not probed (it can hang): taken as it is
                var copy = Launcher.IsNetworkPath(live.Target)
                    ? new LaunchItem { Kind = live.Kind, Target = live.Target, Name = ItemFactory.FileDisplayName(live.Target.TrimEnd('\\')) }
                    : ItemFactory.FromPath(live.Target);
                if (copy != null && live.Arguments is { Length: > 0 } arguments)
                {
                    // "ping 1.1.1.1" from the search box: the command with what was typed after it
                    copy.Arguments = arguments;
                    copy.WorkingDirectory = live.WorkingDirectory;
                    copy.Name += " " + arguments;
                }
                return copy;
            case ItemKind.Url:
                return ItemFactory.FromUrl(live.Target);
            case ItemKind.Shell:
                return ItemFactory.FromShell(live.Target, null);
            default:
                return null;
        }
    }
}
