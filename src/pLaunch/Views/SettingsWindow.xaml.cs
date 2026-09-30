using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using pLaunch.Models;
using pLaunch.Native;
using pLaunch.Services;

namespace pLaunch.Views;

/// <summary>
/// All the settings of a list in one place. Every change applies at once (and is saved) through the
/// popup, exactly like the quick menu entries.
/// </summary>
public partial class SettingsWindow : Window
{
    const string AcrylicLabel = "Acrylic (Windows)";

    readonly PopupWindow _popup;
    bool _loading;

    public SettingsWindow(PopupWindow popup)
    {
        InitializeComponent();
        _popup = popup;
        Title = popup.Profile.IsDefault ? "pLaunch settings" : $"pLaunch settings \x2013 {popup.Profile.Name}";

        foreach (var (_, label) in PopupWindow.ViewChoices) ViewBox.Items.Add(label);
        foreach (var (_, label) in PopupWindow.SizeChoices) SizeBox.Items.Add(label);
        foreach (var (_, label) in PopupWindow.SortChoices) SortBox.Items.Add(label);
        foreach (var (_, label) in PopupWindow.ThemeChoices) ThemeBox.Items.Add(label);
        HotkeyBox.GestureChanged += (_, _) => HotkeyWinBox.IsChecked = HotkeyBox.UseWin;
        LoadValues();
    }

    /// <summary>Shows the current values (again after an import or a data folder change reloaded the list).</summary>
    void LoadValues()
    {
        _loading = true;
        var s = _popup.Settings;
        ViewBox.SelectedIndex = Array.FindIndex(PopupWindow.ViewChoices, c => c.Mode == s.View);
        SizeBox.SelectedIndex = Array.FindIndex(PopupWindow.SizeChoices, c => c.Size == s.Size);
        SortBox.SelectedIndex = Array.FindIndex(PopupWindow.SortChoices, c => c.Mode == s.Sort);
        ThemeBox.SelectedIndex = Array.FindIndex(PopupWindow.ThemeChoices, c => c.Theme == s.Theme);
        ThemeNote.Visibility = s.Background != null ? Visibility.Visible : Visibility.Collapsed;
        FillBackgrounds();
        TranslucentBox.IsChecked = s.Translucent;
        TranslucentBox.IsEnabled = s.Background != null && _popup.AcrylicAvailable;
        WebIconsBox.IsChecked = s.WebIcons;
        AutostartBox.IsChecked = _popup.SafeAutostart();
        HotkeyBox.Gesture = s.Hotkey;
        HotkeyWinBox.IsChecked = HotkeyBox.UseWin;
        HotkeyStatus.Text = string.IsNullOrWhiteSpace(s.Hotkey) ? "No shortcut."
            : _popup.PopupHotkeyActive ? $"{s.Hotkey} opens this list."
            : $"{s.Hotkey} is not active: another program or list uses it. Choose another one.";
        ButtonIconPanel.Visibility = _popup.Profile.IsDefault ? Visibility.Collapsed : Visibility.Visible;
        DefaultButtonIconButton.IsEnabled = s.ButtonIconPath != null;
        DataFolderText.Text = "Data folder: " + AppConfig.DataDirectoryPath;
        AutoUpdateBox.IsChecked = AppConfig.Load().CheckForUpdates;
        VersionText.Text = $"pLaunch {UpdateService.CurrentVersion} ({UpdateService.Edition})";
        _loading = false;
    }

    /// <summary>The acrylic, the presets with a swatch, and the custom color when there is one.</summary>
    void FillBackgrounds()
    {
        BackgroundBox.Items.Clear();
        BackgroundBox.Items.Add(new ComboBoxItem { Content = AcrylicLabel, Tag = null });
        var current = _popup.Settings.Background;
        int selected = 0;
        foreach (var (name, hex) in Appearance.Presets)
        {
            if (string.Equals(hex, current, StringComparison.OrdinalIgnoreCase))
                selected = BackgroundBox.Items.Count;
            BackgroundBox.Items.Add(new ComboBoxItem { Content = Swatch(hex, name), Tag = hex });
        }
        if (current != null && selected == 0)
        {
            selected = BackgroundBox.Items.Count;
            BackgroundBox.Items.Add(new ComboBoxItem { Content = Swatch(current, "Custom " + current), Tag = current });
        }
        BackgroundBox.SelectedIndex = selected;
    }

    static StackPanel Swatch(string hex, string name)
    {
        Appearance.TryParse(hex, out var color);
        var border = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(color),
            Margin = new Thickness(0, 0, 10, 0),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(border);
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    // ---------------------------------------------------------------- appearance

    void ViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ViewBox.SelectedIndex >= 0)
            _popup.SetView(PopupWindow.ViewChoices[ViewBox.SelectedIndex].Mode);
    }

    void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SizeBox.SelectedIndex >= 0)
            _popup.SetSize(PopupWindow.SizeChoices[SizeBox.SelectedIndex].Size);
    }

    void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SortBox.SelectedIndex >= 0)
            _popup.SetSort(PopupWindow.SortChoices[SortBox.SelectedIndex].Mode);
    }

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ThemeBox.SelectedIndex >= 0)
            _popup.SetTheme(PopupWindow.ThemeChoices[ThemeBox.SelectedIndex].Theme);
    }

    void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackgroundBox.SelectedItem is not ComboBoxItem item)
            return;
        _popup.SetBackground(item.Tag as string);
        LoadValues(); // the theme note and "translucent" depend on it
    }

    void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        var start = Appearance.TryParse(_popup.Settings.Background, out var current) ? current : Color.FromRgb(0x2B, 0x34, 0x40);
        if (NativeMethods.PickColor(new WindowInteropHelper(this).Handle, start) is { } picked)
        {
            _popup.SetBackground(Appearance.ToHex(picked));
            LoadValues();
        }
    }

    void TranslucentBox_Click(object sender, RoutedEventArgs e) => _popup.SetTranslucent(TranslucentBox.IsChecked == true);

    void WebIconsBox_Click(object sender, RoutedEventArgs e) => _popup.SetWebIcons(WebIconsBox.IsChecked == true);

    // ---------------------------------------------------------------- behavior

    void AutostartBox_Click(object sender, RoutedEventArgs e)
    {
        _popup.SetAutostart(AutostartBox.IsChecked == true);
        AutostartBox.IsChecked = _popup.SafeAutostart();
    }

    void HotkeyWinBox_Click(object sender, RoutedEventArgs e) => HotkeyBox.UseWin = HotkeyWinBox.IsChecked == true;

    void HotkeyApply_Click(object sender, RoutedEventArgs e)
    {
        if (HotkeyBox.Gesture is not { } gesture)
        {
            HotkeyStatus.Text = "Press a key together with Ctrl, Alt or Win (or tick \"Win +\").";
            return;
        }
        if (_popup.SetPopupHotkey(gesture) is { } error)
        {
            LoadValues();
            HotkeyStatus.Text = error;
            return;
        }
        LoadValues();
    }

    void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        _popup.SetPopupHotkey(null);
        LoadValues();
    }

    void ButtonIcon_Click(object sender, RoutedEventArgs e)
    {
        _popup.ChangeButtonIcon();
        LoadValues();
        Activate();
    }

    void DefaultButtonIcon_Click(object sender, RoutedEventArgs e)
    {
        _popup.SetButtonIcon(null, 0);
        LoadValues();
    }

    // ---------------------------------------------------------------- data and updates

    void Export_Click(object sender, RoutedEventArgs e) => _popup.ExportList();

    void Import_Click(object sender, RoutedEventArgs e)
    {
        _popup.ImportList();
        LoadValues();
    }

    void MoveData_Click(object sender, RoutedEventArgs e)
    {
        _popup.MoveDataFolder();
        LoadValues();
    }

    void OpenData_Click(object sender, RoutedEventArgs e) => _popup.OpenDataFolder(hide: false);

    void AutoUpdateBox_Click(object sender, RoutedEventArgs e) =>
        AppConfig.Update(c => c.CheckForUpdates = AutoUpdateBox.IsChecked == true);

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        VersionText.Text = "Checking\x2026";
        await _popup.CheckForUpdate(manual: true);
        LoadValues();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
