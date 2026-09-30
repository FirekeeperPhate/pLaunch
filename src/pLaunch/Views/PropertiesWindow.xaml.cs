using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using pLaunch.Models;
using pLaunch.Native;
using pLaunch.Services;

namespace pLaunch.Views;

/// <summary>
/// Edits an item: name, target (or URL), arguments, start-in folder, "always run as administrator" and
/// a custom icon (also for sub-folders). Changes are applied to the item only on OK.
/// </summary>
public partial class PropertiesWindow : Window
{
    readonly LaunchItem _item;
    string? _iconPath;
    int _iconIndex;

    public PropertiesWindow(LaunchItem item)
    {
        InitializeComponent();
        _item = item;
        _iconPath = item.IconPath;
        _iconIndex = item.IconIndex;

        NameBox.Text = item.Name;
        TargetBox.Text = item.Target;
        ArgumentsBox.Text = item.Arguments ?? "";
        StartInBox.Text = item.WorkingDirectory ?? "";
        RunAsAdminBox.IsChecked = item.RunAsAdmin;

        bool file = item.Kind == ItemKind.File, folder = item.Kind == ItemKind.Folder;
        KindText.Text = item.Kind switch
        {
            ItemKind.File => "Program or file",
            ItemKind.Folder => "Folder",
            ItemKind.Url => "Web link",
            ItemKind.Shell => "App",
            ItemKind.Group => "Sub-folder of pLaunch",
            _ => "",
        };
        TargetLabel.Text = item.Kind == ItemKind.Url ? "URL" : "Target";
        var targetRow = item.Kind is ItemKind.File or ItemKind.Folder or ItemKind.Url or ItemKind.Shell;
        SetVisible(targetRow, TargetLabel, TargetBox);
        SetVisible(file || folder, BrowseTargetButton);
        TargetBox.IsReadOnly = item.Kind == ItemKind.Shell; // shell:AppsFolder names are not typed by hand
        SetVisible(file, ArgumentsLabel, ArgumentsBox, StartInLabel, StartInBox, BrowseStartInButton, StartWindowLabel, StartWindowBox);
        SetVisible(Launcher.CanRunAsAdmin(item), RunAsAdminBox);
        SetVisible(item.IsLaunchable, HotkeyLabel, HotkeyPanel);
        SetVisible(folder, ShowContentsBox);
        StartWindowBox.SelectedIndex = (int)item.StartWindow;
        ShowContentsBox.IsChecked = item.ShowContents;
        HotkeyBox.Gesture = item.Hotkey;
        HotkeyWinBox.IsChecked = HotkeyBox.UseWin;
        UsageText.Text = item.LaunchCount == 0 ? ""
            : $"Opened {item.LaunchCount} time{(item.LaunchCount == 1 ? "" : "s")}"
              + (item.LastLaunched is { } last ? $", last on {last.ToLocalTime():g}." : ".");
        UsageText.Visibility = item.LaunchCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        IconGlyph.Text = item.Kind switch { ItemKind.Url => "\xE774", ItemKind.Folder or ItemKind.Group => "\xE8B7", _ => "\xE8A5" };

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            UpdatePreview();
        };
    }

    static void SetVisible(bool visible, params UIElement[] elements)
    {
        foreach (var e in elements)
            e.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    async void UpdatePreview()
    {
        DefaultIconButton.IsEnabled = _iconPath != null;
        var preview = new LaunchItem(_item.Id)
        {
            Kind = _item.Kind,
            Target = TargetBox.Text.Trim(),
            IconPath = _iconPath,
            IconIndex = _iconIndex,
        };
        int pixels = (int)Math.Round(40 * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX);
        var icon = preview.HasShellIcon ? await IconProvider.GetAsync(preview, pixels) : null;
        IconPreview.Source = icon;
        IconGlyph.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
    }

    void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        // Start from the current custom icon, else from the program itself, else the Windows icon library
        var target = TargetBox.Text.Trim();
        var start = _iconPath
            ?? (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target) ? target : null);
        if (ShellInterop.PickIcon(new WindowInteropHelper(this).Handle, start, _iconPath != null ? _iconIndex : 0) is var (path, index))
        {
            _iconPath = path;
            _iconIndex = index;
            UpdatePreview();
        }
    }

    void DefaultIcon_Click(object sender, RoutedEventArgs e)
    {
        _iconPath = null;
        _iconIndex = 0;
        UpdatePreview();
    }

    void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_item.Kind == ItemKind.Folder)
        {
            var dialog = new OpenFolderDialog { Title = "Target folder", InitialDirectory = ExistingFolder(TargetBox.Text) };
            if (dialog.ShowDialog(this) == true)
                TargetBox.Text = dialog.FolderName;
        }
        else
        {
            var dialog = new OpenFileDialog { Title = "Target", DereferenceLinks = false, InitialDirectory = ExistingFolder(Path.GetDirectoryName(TargetBox.Text)) };
            if (dialog.ShowDialog(this) == true)
                TargetBox.Text = dialog.FileName;
        }
        UpdatePreview();
    }

    void BrowseStartIn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Start in",
            InitialDirectory = ExistingFolder(Environment.ExpandEnvironmentVariables(StartInBox.Text)) ?? ExistingFolder(Path.GetDirectoryName(TargetBox.Text)),
        };
        if (dialog.ShowDialog(this) == true)
            StartInBox.Text = dialog.FolderName;
    }

    static string? ExistingFolder(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null; }
        catch (ArgumentException) { return null; }
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Warn("The name cannot be empty.");
            NameBox.Focus();
            return;
        }
        var target = TargetBox.Text.Trim().Trim('"');
        var kind = _item.Kind;
        switch (kind)
        {
            case ItemKind.Url:
                if (!ItemFactory.TryParseUrl(target, out var uri))
                {
                    Warn("This is not a valid web address.");
                    TargetBox.Focus();
                    return;
                }
                target = uri.AbsoluteUri;
                break;
            case ItemKind.File or ItemKind.Folder:
                if (target.Length == 0)
                {
                    Warn("The target cannot be empty.");
                    TargetBox.Focus();
                    return;
                }
                // A folder typed where a file was (or the other way round) changes the kind
                if (Directory.Exists(target))
                    kind = ItemKind.Folder;
                else if (File.Exists(target))
                    kind = ItemKind.File;
                else if (MessageBox.Show(this, $"\"{target}\" was not found.\n\nSave anyway?", "pLaunch",
                             MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                break;
        }

        _item.Name = name;
        _item.Kind = kind;
        if (kind is ItemKind.File or ItemKind.Folder or ItemKind.Url)
            _item.Target = target;
        _item.Arguments = kind == ItemKind.File && ArgumentsBox.Text.Trim() is { Length: > 0 } args ? args : null;
        _item.WorkingDirectory = kind == ItemKind.File && StartInBox.Text.Trim() is { Length: > 0 } folder ? folder : null;
        _item.RunAsAdmin = RunAsAdminBox.Visibility == Visibility.Visible && RunAsAdminBox.IsChecked == true && Launcher.CanRunAsAdmin(_item);
        _item.IconPath = _iconPath;
        _item.IconIndex = _iconPath == null ? 0 : _iconIndex;
        _item.StartWindow = kind == ItemKind.File ? (StartWindow)Math.Max(0, StartWindowBox.SelectedIndex) : StartWindow.Normal;
        _item.ShowContents = kind == ItemKind.Folder && ShowContentsBox.IsChecked == true;
        // Checked against the other programs when saved: the list tells if it is taken
        _item.Hotkey = _item.IsLaunchable ? HotkeyBox.Gesture : null;
        DialogResult = true;
    }

    void HotkeyWinBox_Click(object sender, RoutedEventArgs e) => HotkeyBox.UseWin = HotkeyWinBox.IsChecked == true;

    void Warn(string message) => MessageBox.Show(this, message, "pLaunch", MessageBoxButton.OK, MessageBoxImage.Warning);
}
