using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using pLaunch.Models;
using pLaunch.Native;
using pLaunch.Services;

namespace pLaunch;

// Several lists, each with its own taskbar button: "pLaunch --list Name"
public partial class PopupWindow
{
    /// <summary>
    /// A named list gets its own taskbar button: the process AppUserModelID (set in Program) plus the
    /// relaunch properties on the window, so a pinned copy of the button starts this list again, with its
    /// own icon if one was chosen.
    /// </summary>
    async void ApplyListIdentity()
    {
        if (_profile.IsDefault || _hwnd == IntPtr.Zero || Environment.ProcessPath is not { } exe)
            return;
        var icon = ButtonIconResource() ?? $"{exe},0";
        ShellInterop.SetWindowIdentity(_hwnd, _profile.AppUserModelId!, $"\"{exe}\" {_profile.Arguments}".Trim(), _profile.Title, icon);

        // The running button shows the window's icon
        if (_settings.ButtonIconPath is { Length: > 0 } path)
        {
            var probe = new LaunchItem { Kind = ItemKind.Group, IconPath = path, IconIndex = _settings.ButtonIconIndex };
            if (await IconProvider.GetAsync(probe, 256) is { } image)
                Icon = image;
        }
        else
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pLaunch;component/Assets/pLaunch.ico"));
        }
    }

    /// <summary>"path,index" of the chosen button icon for the pinned shortcut, or null.</summary>
    string? ButtonIconResource()
    {
        if (_settings.ButtonIconPath is not { Length: > 0 } path)
            return null;
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.GetExtension(expanded).Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? $"{expanded},0"
            : $"{expanded},{_settings.ButtonIconIndex}";
    }

    MenuItem CreateListsMenu()
    {
        var lists = CreateSubmenu("Lists", "\xE8FD");
        var dataDirectory = AppConfig.DataDirectoryPath;
        if (!_profile.IsDefault)
            lists.Items.Add(CreateMenuItem("pLaunch (main list)", () => OpenList(ListProfile.Default)));
        foreach (var name in ListProfile.ExistingNames(dataDirectory))
        {
            if (name.Equals(_profile.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var profile = ListProfile.Named(name);
            lists.Items.Add(CreateMenuItem(name, () => OpenList(profile)));
        }
        if (lists.Items.Count > 0)
            lists.Items.Add(new Separator());
        lists.Items.Add(CreateMenuItem("New list\x2026", NewList, "\xE710"));
        if (!_profile.IsDefault)
        {
            lists.Items.Add(new Separator());
            var icon = CreateMenuItem("Taskbar icon\x2026", ChangeButtonIcon, "\xE790");
            icon.ToolTip = "A button already pinned keeps its icon: unpin it and pin it again to see the new one";
            lists.Items.Add(icon);
            if (_settings.ButtonIconPath != null)
                lists.Items.Add(CreateMenuItem("Default taskbar icon", () => SetButtonIcon(null, 0)));
            lists.Items.Add(CreateMenuItem($"Delete the list \"{_profile.Name}\"\x2026", DeleteThisList, "\xE74D"));
        }
        return lists;
    }

    /// <summary>Starts (or, when it is running, opens) another list; each has its own taskbar button.</summary>
    void OpenList(ListProfile profile)
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        HidePopup();
        Process.Start(new ProcessStartInfo(exe, profile.Arguments.Trim()) { UseShellExecute = false })?.Dispose();
    }

    void NewList()
    {
        var dataDirectory = AppConfig.DataDirectoryPath;
        var existing = ListProfile.ExistingNames(dataDirectory);
        var prompt = new Views.TextPromptWindow("New list",
            "Name of the new list. It gets its own taskbar button: right-click it and choose \"Pin to taskbar\" to keep it there.",
            "", name =>
                !ListProfile.IsValidName(name) ? $"Use letters, digits, spaces, - and _ (at most {ListProfile.MaxNameLength} characters)."
                : existing.Contains(name, StringComparer.OrdinalIgnoreCase) ? "A list with this name already exists."
                : null)
        { Owner = this };
        if (ShowModal(() => prompt.ShowDialog()) != true)
            return;
        var profile = ListProfile.Named(prompt.Value);
        try
        {
            // Same look as this list, no items yet
            var settings = new LauncherSettings
            {
                View = _settings.View,
                Size = _settings.Size,
                Sort = _settings.Sort,
                Theme = _settings.Theme,
                Background = _settings.Background,
                Translucent = _settings.Translucent,
                WebIcons = _settings.WebIcons,
            };
            new ItemStore(profile.FilePath(dataDirectory)).Save([], settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Cannot create the list.", ex.Message);
            return;
        }
        OpenList(profile);
    }

    void ChangeButtonIcon()
    {
        if (ShowModal(() => ShellInterop.PickIcon(_hwnd, _settings.ButtonIconPath, _settings.ButtonIconIndex)) is var (path, index))
            SetButtonIcon(path, index);
        Activate();
    }

    void SetButtonIcon(string? path, int index)
    {
        _settings.ButtonIconPath = path;
        _settings.ButtonIconIndex = path == null ? 0 : index;
        Save();
        ApplyListIdentity();
    }

    void DeleteThisList()
    {
        var answer = ShowModal(() => MessageBox.Show(this,
            $"Delete the list \"{_profile.Name}\" and its shortcuts?\n\nA pinned taskbar button for it has to be unpinned by hand.",
            "pLaunch", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No));
        if (answer != MessageBoxResult.Yes)
            return;
        try
        {
            Autostart.SetEnabled(_profile, false);
            // Kept as .deleted next to the others, in case it was a mistake
            File.Move(_store.FilePath, _store.FilePath + ".deleted", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowError("Cannot delete the list.", ex.Message);
            return;
        }
        _storeWatcher?.Dispose();
        Close();
    }
}
