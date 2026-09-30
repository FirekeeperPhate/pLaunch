using System.Windows;
using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch;

// Global shortcuts: one opens this list's popup, others launch single items
public partial class PopupWindow
{
    GlobalHotkeys? _hotkeys;
    string _registeredHotkeys = "\0"; // what is registered now; "\0" = nothing yet
    /// <summary>Items whose shortcut could not be registered (taken by another app or list).</summary>
    readonly HashSet<string> _failedHotkeys = [];

    /// <summary>Whether the popup's own shortcut is active (false: invalid or taken).</summary>
    internal bool PopupHotkeyActive { get; private set; }

    /// <summary>
    /// (Re)registers the shortcuts when they changed since the last call: the popup's and every item's
    /// with one (at any depth).
    /// </summary>
    void RegisterHotkeys(bool force = false)
    {
        if (_hotkeys == null)
            return;
        var items = new List<LaunchItem>();
        void Walk(IEnumerable<LaunchItem> level)
        {
            foreach (var item in level)
            {
                if (item.IsLaunchable && !string.IsNullOrWhiteSpace(item.Hotkey))
                    items.Add(item);
                Walk(item.Children ?? []);
            }
        }
        Walk(_root);
        var signature = _settings.Hotkey + "|" + string.Join("|", items.Select(i => i.Id + "=" + i.Hotkey));
        if (!force && signature == _registeredHotkeys)
            return;
        _registeredHotkeys = signature;

        _hotkeys.UnregisterAll();
        _failedHotkeys.Clear();
        PopupHotkeyActive = HotkeyGesture.TryParse(_settings.Hotkey, out var popup)
            && _hotkeys.Register(popup, TogglePopupFromHotkey);
        foreach (var item in items)
        {
            var id = item.Id;
            if (!HotkeyGesture.TryParse(item.Hotkey, out var gesture) || !_hotkeys.Register(gesture, () => LaunchFromHotkey(id)))
                _failedHotkeys.Add(id);
        }
    }

    /// <summary>The popup's shortcut: opens it (centered above the taskbar), or closes it when it is open.</summary>
    void TogglePopupFromHotkey()
    {
        if (IsOpen && IsActive)
        {
            HidePopup();
            return;
        }
        ShowPopup();
        Activate();
    }

    void LaunchFromHotkey(string id)
    {
        if (ItemTree.Find(_root, id) is not { } item)
            return;
        if (Launcher.IsMissing(item))
        {
            ShowError($"\"{item.Name}\" was not found.", item.Target);
            return;
        }
        try
        {
            if (Launcher.Launch(item))
                CountLaunches([item]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError($"Cannot open \"{item.Name}\".", ex.Message);
        }
    }

    /// <summary>
    /// Changes the popup's shortcut (null/"" = none). Returns an error message when the new one cannot be
    /// used; the previous shortcut then stays.
    /// </summary>
    internal string? SetPopupHotkey(string? text)
    {
        var previous = _settings.Hotkey;
        if (string.IsNullOrWhiteSpace(text))
        {
            _settings.Hotkey = "";
            Save();
            return null;
        }
        if (!HotkeyGesture.TryParse(text, out var gesture))
            return "Use at least one of Ctrl, Alt or Win together with a key.";
        // The popup's shortcut is registered first: it would silently take the item's away
        if (ItemTree.Launchables(_root, "").Select(l => l.Item)
                .FirstOrDefault(i => HotkeyGesture.TryParse(i.Hotkey, out var g) && g == gesture) is { } owner)
            return $"{gesture} is already the shortcut of \"{owner.Name}\".";
        _settings.Hotkey = gesture.ToString();
        Save(); // registers it
        if (!PopupHotkeyActive)
        {
            _settings.Hotkey = previous;
            Save();
            return $"{gesture} is already used by another program or list.";
        }
        return null;
    }
}
