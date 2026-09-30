using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch;

// Backup, import, data folder (e.g. synced by OneDrive) and reloading when the file changes elsewhere
public partial class PopupWindow
{
    FileSystemWatcher? _storeWatcher;
    DispatcherTimer? _reloadTimer;

    internal void ExportList()
    {
        var name = _profile.IsDefault ? "pLaunch" : $"pLaunch-{_profile.Name}";
        var dialog = new SaveFileDialog
        {
            Title = "Export list",
            FileName = $"{name}-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = "pLaunch list|*.json",
            DefaultExt = ".json",
        };
        if (ShowModal(() => dialog.ShowDialog(this)) != true)
            return;
        try
        {
            new ItemStore(dialog.FileName).Save(_root, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Cannot export the list.", ex.Message);
        }
    }

    internal void ImportList()
    {
        var dialog = new OpenFileDialog { Title = "Import list", Filter = "pLaunch list|*.json|All files|*.*" };
        if (ShowModal(() => dialog.ShowDialog(this)) != true)
            return;
        var source = new ItemStore(dialog.FileName) { ReadAttempts = 2 };
        var data = source.Load();
        if (source.LoadError != null || data.Items.Count == 0)
        {
            ShowError("Nothing to import.", source.LoadError ?? "The file holds no shortcuts (or is not a pLaunch list).");
            return;
        }
        var answer = ShowModal(() => MessageBox.Show(this,
            $"The file holds {ItemTree.Launchables(data.Items, "").Count} shortcuts.\n\n" +
            "Yes = replace this list with them\nNo = add them as a new sub-folder\nCancel = do nothing",
            "Import list", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel));
        switch (answer)
        {
            case MessageBoxResult.Yes:
                _root.Clear();
                _root.AddRange(data.Items);
                _path.Clear();
                break;
            case MessageBoxResult.No:
                // New ids: the file may be an export of this very list
                RenewIds(data.Items);
                var group = new LaunchItem
                {
                    Kind = ItemKind.Group,
                    Name = "Imported " + Path.GetFileNameWithoutExtension(dialog.FileName),
                    Children = data.Items,
                };
                CurrentLevel.Add(group);
                break;
            default:
                return;
        }
        Save();
        Refresh();
    }

    static void RenewIds(IEnumerable<LaunchItem> items)
    {
        foreach (var item in items)
        {
            item.Id = Guid.NewGuid().ToString("N");
            RenewIds(item.Children ?? []);
        }
    }

    /// <summary>
    /// Moves every list (items.json and lists\*.json) to another folder, or starts using the lists already
    /// there, then tells the other running lists to reload from it.
    /// </summary>
    internal void MoveDataFolder()
    {
        var current = AppConfig.DataDirectoryPath;
        var dialog = new OpenFolderDialog { Title = "Folder for the pLaunch lists", InitialDirectory = current };
        if (ShowModal(() => dialog.ShowDialog(this)) != true)
            return;
        var target = dialog.FolderName;
        if (string.Equals(Path.GetFullPath(target).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;

        bool targetHasLists = File.Exists(ListProfile.Default.FilePath(target)) || ListProfile.ExistingNames(target).Count > 0;
        bool copy = true;
        if (targetHasLists)
        {
            var answer = ShowModal(() => MessageBox.Show(this,
                "That folder already holds pLaunch lists (from another PC?).\n\n" +
                "Yes = use the lists in that folder\nNo = replace them with the lists of this PC\nCancel = do nothing",
                "Move data folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel));
            if (answer == MessageBoxResult.Cancel)
                return;
            copy = answer == MessageBoxResult.No;
        }

        var names = ListProfile.ExistingNames(current);
        try
        {
            if (copy)
            {
                // Save first: the files must hold the latest state of this list
                Save();
                CopyIfExists(ListProfile.Default.FilePath(current), ListProfile.Default.FilePath(target));
                foreach (var name in names)
                {
                    var profile = ListProfile.Named(name);
                    CopyIfExists(profile.FilePath(current), profile.FilePath(target));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Cannot copy the lists to that folder.", ex.Message);
            return;
        }

        var defaultFolder = AppConfig.ConfigDirectory;
        AppConfig.Update(c => c.DataDirectory =
            string.Equals(Path.GetFullPath(target).TrimEnd('\\'), Path.GetFullPath(defaultFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? null : target);

        // The other running lists (of the old and of the new folder) reload from the new place
        var others = names.Concat(ListProfile.ExistingNames(target)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ListProfile.Named).Append(ListProfile.Default)
            .Where(p => !string.Equals(p.Name, _profile.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var profile in others)
            SingleInstance.SendTo(profile, [SingleInstance.ReloadCommand]);
        ReloadData();
    }

    static void CopyIfExists(string from, string to)
    {
        if (!File.Exists(from))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    /// <summary>
    /// Reads the list again: from the current data folder (moved by this or another list), or from the
    /// same file (<paramref name="sameFile"/>: changed by a sync).
    /// </summary>
    public void ReloadData() => ReloadData(sameFile: false);

    void ReloadData(bool sameFile)
    {
        CommitRename(cancel: true);
        var store = sameFile ? new ItemStore(_store.FilePath) : ItemStore.For(_profile);
        var data = store.Load();
        if (store.LoadError != null)
        {
            ShowError("The list could not be read.", store.LoadError);
            return;
        }
        _store = store;
        _root = data.Items;
        _settings = data.Settings;
        IconProvider.WebIconsEnabled = _settings.WebIcons;
        _path.Clear();
        ClearSearchText();
        List.SelectedItems.Clear();
        ApplyView();
        ApplyAppearance();
        ApplyListIdentity();
        ScheduleJumpList();
        WatchStoreFile();
        RegisterHotkeys(); // the shortcuts may be different in the file that came in
    }

    /// <summary>
    /// Watches the list's file: when its content changes and it was not this process writing it (another
    /// PC through a synced folder, a restored backup), the list reloads.
    /// </summary>
    void WatchStoreFile()
    {
        _storeWatcher?.Dispose();
        _storeWatcher = null;
        try
        {
            var folder = Path.GetDirectoryName(_store.FilePath)!;
            Directory.CreateDirectory(folder);
            var watcher = new FileSystemWatcher(folder, Path.GetFileName(_store.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(ScheduleReloadCheck);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleReloadCheck);
            watcher.EnableRaisingEvents = true;
            _storeWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreachable folder: no live reload, the list still works
        }
    }

    void ScheduleReloadCheck()
    {
        if (_reloadTimer == null)
        {
            // Sync clients write in several steps: wait for the file to settle
            _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _reloadTimer.Tick += (_, _) => CheckForExternalChange();
        }
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    void CheckForExternalChange()
    {
        _reloadTimer!.Stop();
        var hash = _store.ReadFileHash();
        if (hash == null || hash == _store.LastContentHash)
            return; // our own save, or unreadable for the moment (a later change notification comes)
        // Not in the middle of a rename or a dialog: try again a bit later
        if (_suppressHide > 0 || _items.Any(i => i.IsEditing) || _draggingId != null)
        {
            _reloadTimer.Start();
            return;
        }
        ReloadData(sameFile: true);
    }
}
