using System.Windows;
using pLaunch.Models;
using pLaunch.Services;
using pLaunch.ViewModels;

namespace pLaunch;

// Programs already open (the running line, "switch to it"), commands and text snippets
public partial class PopupWindow
{
    int _runningScan;

    /// <summary>
    /// Marks the shown items whose program has an open window. The windows are listed off the UI thread
    /// (reading shortcuts and window properties); a newer scan makes an older one's result obsolete.
    /// </summary>
    void RefreshRunning()
    {
        if (!IsOpen)
            return;
        int scan = ++_runningScan;
        MarkRunning(_items.ToList(), () => scan == _runningScan);
    }

    /// <summary>Marks the given rows (the popup's, or a menu's); <paramref name="current"/> = the result is still wanted.</summary>
    async void MarkRunning(List<ItemViewModel> items, Func<bool>? current = null)
    {
        var shown = items.Where(i => RunningApps.CanSwitch(i.Model)).ToList();
        foreach (var vm in items.Except(shown))
            vm.IsRunning = false;
        if (shown.Count == 0)
            return;
        var models = shown.Select(i => i.Model).ToList();
        bool[] running;
        try
        {
            running = await Task.Run(() =>
            {
                var windows = RunningApps.Windows();
                return models.Select(m => RunningApps.WindowsOf(m, windows).Count > 0).ToArray();
            });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return; // no running lines this time; nothing else depends on them
        }
        if (current != null && !current())
            return;
        for (int i = 0; i < shown.Count; i++)
            shown[i].IsRunning = running[i];
    }

    /// <summary>Add → Command… / Text snippet…: the Properties window fills in a new item.</summary>
    void NewByProperties(ItemKind kind)
    {
        var item = new LaunchItem { Kind = kind };
        var dialog = new Views.PropertiesWindow(item) { Owner = this };
        if (ShowModal(() => dialog.ShowDialog()) != true)
            return;
        InsertNew(item); // saves, which registers its shortcut
        DropTakenHotkey(item);
    }

    /// <summary>After a save: a shortcut another program (or list) already holds is removed from the item.</summary>
    void DropTakenHotkey(LaunchItem item)
    {
        if (item.Hotkey is not { Length: > 0 } hotkey || !_failedHotkeys.Contains(item.Id))
            return;
        ShowError($"The shortcut {hotkey} cannot be used.", "Another program (or another list) already uses it. It was removed from the item.");
        item.Hotkey = null;
        Save();
    }

    /// <summary>A snippet set to paste: copied only, for pasting it somewhere else by hand.</summary>
    void CopySnippet(ItemViewModel item)
    {
        try
        {
            Launcher.Launch(item.Model, paste: false);
            CountLaunches([item.Model]);
            HidePopup();
        }
        catch (InvalidOperationException ex)
        {
            ShowError("Cannot copy the text.", ex.Message);
        }
    }
}
