using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using pLaunch.Models;
using pLaunch.Native;
using pLaunch.Services;
using pLaunch.ViewModels;

namespace pLaunch;

/// <summary>
/// The launcher. It is an ordinary top-level window, so it owns a taskbar button; while idle it stays
/// minimized, and restoring it (a click on the button, or hovering it while dragging) opens it as a
/// flyout next to the taskbar. Losing the focus minimizes it again.
/// </summary>
public partial class PopupWindow : Window
{
    const double GapDip = 12; // distance from the taskbar, like the Start menu
    const string InternalDragFormat = "pLaunch.ItemId";
    static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);
    /// <summary>Holding a drag over a sub-folder opens it; over the back button, goes up.</summary>
    static readonly TimeSpan SpringDelay = TimeSpan.FromMilliseconds(800);

    readonly ItemStore _store;
    readonly List<LaunchItem> _root;
    readonly LauncherSettings _settings;
    /// <summary>The sub-folders opened, from the top level down to the one shown.</summary>
    readonly List<LaunchItem> _path = [];
    /// <summary>What the list shows: the current level in display order.</summary>
    readonly ObservableCollection<ItemViewModel> _items = [];
    /// <summary>One view model per item across refreshes (keeps the icons).</summary>
    readonly Dictionary<string, ItemViewModel> _viewModels = [];
    readonly DispatcherTimer _dragLeaveTimer;
    readonly DispatcherTimer _hoverWatch;
    readonly DispatcherTimer _springTimer;
    readonly JumpListBuilder _jumpList = new();
    ViewMetrics _metrics = ViewMetrics.For(ViewMode.List, ItemSize.Medium);
    IntPtr _hwnd;
    int _suppressHide;
    DateTime _lastAutoHide;
    NativeMethods.POINT _anchor;
    int _outsideTicks;
    Point _pressPoint;
    ItemViewModel? _pressed;
    string? _draggingId;
    bool _dragAcceptable;
    bool _acrylic;
    object? _springTarget; // an ItemViewModel (sub-folder to open) or the Header (go back)

    public PopupWindow(ItemStore store)
    {
        InitializeComponent();
        _store = store;
        var data = store.Load();
        _root = data.Items;
        _settings = data.Settings;
        List.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateEmptyHint();

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            ClearDropMarkers();
            SetSpringTarget(null);
        };
        _hoverWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hoverWatch.Tick += HoverWatch_Tick;
        _springTimer = new DispatcherTimer { Interval = SpringDelay };
        _springTimer.Tick += SpringTimer_Tick;

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => _dragLeaveTimer.Start();
        Drop += OnDrop;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        ApplyView();
        ApplyAppearance();
    }

    bool IsOpen => IsVisible && WindowState == WindowState.Normal;

    /// <summary>False only in UI test harnesses, so an opened popup never takes the focus from the user.</summary>
    internal bool ActivateOnOpen { get; set; } = true;

    List<LaunchItem> CurrentLevel => _path.Count == 0 ? _root : (_path[^1].Children ??= []);

    // ---------------------------------------------------------------- open / close

    /// <summary>Shows the taskbar button; <paramref name="minimized"/> = false also opens the popup.</summary>
    public void Start(bool minimized)
    {
        // Always appear minimized first: opening then takes the same path as a taskbar click.
        // Started with Windows it must not take the focus (the minimized window would get the keys).
        WindowState = WindowState.Minimized;
        ShowActivated = !minimized;
        Show();
        ShowActivated = true;
        QueueIconLoad();
        ScheduleJumpList();
        if (!minimized)
            ShowPopup();
    }

    public void ShowPopup()
    {
        _lastAutoHide = default;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal; // OnStateChanged places and activates it
        else
            OnPopupOpened();
    }

    void HidePopup(bool auto = false)
    {
        if (WindowState == WindowState.Minimized)
            return;
        if (auto)
            _lastAutoHide = DateTime.UtcNow;
        WindowState = WindowState.Minimized;
    }

    void OnStateChanged(object? sender, EventArgs e)
    {
        switch (WindowState)
        {
            case WindowState.Maximized:
                WindowState = WindowState.Normal;
                break;
            case WindowState.Normal:
                // Clicking the taskbar button of the open popup may deactivate it first (so it hides),
                // then the click restores it: treat that as the "close" half of the toggle
                if (DateTime.UtcNow - _lastAutoHide < ReopenGuard)
                {
                    _lastAutoHide = default;
                    Dispatcher.BeginInvoke(() => WindowState = WindowState.Minimized);
                    return;
                }
                OnPopupOpened();
                break;
            case WindowState.Minimized:
                _hoverWatch.Stop();
                CommitRename();
                ClearDropMarkers();
                SetSpringTarget(null);
                break;
        }
    }

    void OnPopupOpened()
    {
        NativeMethods.GetCursorPos(out _anchor);
        // Every opening starts from the top level, like a menu
        if (_path.Count > 0)
        {
            _path.Clear();
            Refresh();
        }
        Place();
        if (ActivateOnOpen)
            Activate();
        List.SelectedIndex = -1;
        List.Focus();
        RefreshMissing();
        QueueIconLoad();

        // Opened by hovering the taskbar button during a drag: close again if the drop happens elsewhere
        if (IsLeftButtonDown())
        {
            _outsideTicks = 0;
            _hoverWatch.Start();
        }
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressHide > 0 || WindowState != WindowState.Normal)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive && _suppressHide == 0)
                HidePopup(auto: true);
        });
    }

    void HoverWatch_Tick(object? sender, EventArgs e)
    {
        if (IsActive || !IsOpen)
        {
            _hoverWatch.Stop();
            return;
        }
        if (IsLeftButtonDown() || IsCursorOverWindow())
        {
            _outsideTicks = 0;
            return;
        }
        if (++_outsideTicks >= 3)
        {
            _hoverWatch.Stop();
            HidePopup();
        }
    }

    static bool IsLeftButtonDown() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

    bool IsCursorOverWindow() =>
        NativeMethods.GetCursorPos(out var p) && NativeMethods.GetWindowRect(_hwnd, out var r)
        && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>Sizes the popup to its content and puts it against the taskbar, near <see cref="_anchor"/>.</summary>
    void Place()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        var monitor = NativeMethods.MonitorFromPoint(_anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return;
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var (area, edge) = PopupPlacement.AvailableArea(
            info.rcMonitor.ToPixelRect(), info.rcWork.ToPixelRect(), TaskbarRect(), (int)Math.Round(48 * scale));
        int gap = (int)Math.Round(GapDip * scale);
        double maxHeight = Math.Max(120, (area.Height - 2 * gap) / scale);
        int width = (int)Math.Round(_metrics.Width * scale);
        int height = (int)Math.Round(Math.Min(DesiredHeight(), maxHeight) * scale);
        var (x, y) = PopupPlacement.Compute(area, edge, _anchor.X, _anchor.Y, width, height, gap);

        // Entering a monitor with another DPI makes WPF rescale the window: set the bounds again after that
        bool dpiChanges = Math.Abs(VisualTreeHelper.GetDpi(this).DpiScaleX - scale) > 0.001;
        const uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, flags);
        if (dpiChanges)
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, flags);
    }

    double DesiredHeight()
    {
        Root.Measure(new Size(_metrics.Width, double.PositiveInfinity));
        return Root.DesiredSize.Height;
    }

    static PixelRect? TaskbarRect()
    {
        var data = new NativeMethods.APPBARDATA { cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>() };
        return NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) != IntPtr.Zero ? data.rc.ToPixelRect() : null;
    }

    // ---------------------------------------------------------------- window setup

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source.AddHook(WndProc);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;

        NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, 1); // no minimize/restore animation
        NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        // No acrylic before Windows 11 22H2: a solid background then
        _acrylic = NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_TRANSIENTWINDOW) == 0;
        ApplyAppearance();
    }

    /// <summary>
    /// Theme and background: the WPF theme colors the text and controls, the window's dark-mode flag tints
    /// the acrylic, and a custom color is painted over it (a little translucent, or solid).
    /// </summary>
    void ApplyAppearance()
    {
        bool dark = Appearance.IsDark(_settings, IsSystemDark());
        // "System" with the acrylic follows Windows live; a custom color or an explicit choice fixes it
        var mode = _settings.Background == null && _settings.Theme == ThemeChoice.System
            ? ThemeMode.System
            : dark ? ThemeMode.Dark : ThemeMode.Light;
        if (!Application.Current.ThemeMode.Equals(mode))
            Application.Current.ThemeMode = mode;
        if (_hwnd != IntPtr.Zero)
            NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);

        if (Appearance.TryParse(_settings.Background, out var color))
        {
            color.A = _settings.Translucent && _acrylic ? Appearance.TranslucentAlpha : (byte)255;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Root.Background = brush;
        }
        else if (_acrylic || _hwnd == IntPtr.Zero)
        {
            Root.ClearValue(Border.BackgroundProperty);
        }
        else
        {
            Root.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        }
    }

    static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(ApplyAppearance);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Win+Up or a double click on the taskbar thumbnail must not maximize the flyout
        if (msg == NativeMethods.WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == NativeMethods.SC_MAXIMIZE)
            handled = true;
        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        IconProvider.ClearCache(); // the icons of the old size are not needed any more
        QueueIconLoad();
    }

    // ---------------------------------------------------------------- view

    /// <summary>Applies view mode and item size: sizes, panel (stack or wrap), container style, templates.</summary>
    void ApplyView()
    {
        _metrics = ViewMetrics.For(_settings.View, _settings.Size);
        Resources["IconSize"] = _metrics.IconSize;
        Resources["GlyphSize"] = Math.Round(_metrics.IconSize * 0.85);
        Resources["RowHeight"] = _metrics.RowHeight;
        Resources["TileWidth"] = _metrics.TileWidth;
        Resources["TileHeight"] = _metrics.TileHeight;
        Resources["SeparatorWidth"] = _metrics.SeparatorWidth;
        bool list = _settings.View == ViewMode.List;
        List.ItemsPanel = (ItemsPanelTemplate)Resources[list ? "StackPanelTemplate" : "WrapPanelTemplate"];
        List.ItemContainerStyle = (Style)Resources[list ? "ListContainer" : "TileContainer"];
        List.ItemTemplateSelector = new ItemTemplateChooser(Resources, _settings.View);
        Refresh();
    }

    void SettingsChanged()
    {
        Save();
        ApplyView();
    }

    /// <summary>Rebuilds the shown level from the model, keeping (or moving) the selection.</summary>
    void Refresh(string? selectId = null)
    {
        selectId ??= (List.SelectedItem as ItemViewModel)?.Model.Id;
        _items.Clear();
        foreach (var item in ItemTree.DisplayOrder(CurrentLevel, _settings.Sort))
        {
            var vm = ViewModelFor(item);
            vm.DropMarker = DropMarker.None;
            if (vm.IsGroup)
                vm.RefreshChildInfo();
            _items.Add(vm);
        }

        Header.Visibility = _path.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderTitle.Text = string.Join(" \x203A ", _path.Select(g => g.Name));
        EmptyHintText.Text = _path.Count > 0
            ? "This folder is empty. Drag shortcuts here."
            : "Drag programs, files, folders or links here. You can also drop them on the pLaunch taskbar button.";
        UpdateEmptyHint();

        if (selectId != null && _items.FirstOrDefault(i => i.Model.Id == selectId) is { } selected)
        {
            List.SelectedItem = selected;
            List.ScrollIntoView(selected);
        }
        QueueIconLoad();
        if (IsOpen)
            Place();
    }

    ItemViewModel ViewModelFor(LaunchItem item)
    {
        if (!_viewModels.TryGetValue(item.Id, out var vm) || vm.Model != item)
            _viewModels[item.Id] = vm = new ItemViewModel(item);
        return vm;
    }

    void UpdateEmptyHint() => EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- sub-folders

    void OpenGroup(LaunchItem group)
    {
        CommitRename();
        _path.Add(group);
        Refresh();
        List.SelectedIndex = -1;
        List.Focus();
    }

    void GoBack()
    {
        if (_path.Count == 0)
            return;
        CommitRename();
        var left = _path[^1];
        _path.RemoveAt(_path.Count - 1);
        Refresh(left.Id);
        FocusSelected();
    }

    void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();

    // ---------------------------------------------------------------- items

    public void AddFromArguments(IEnumerable<string> arguments)
    {
        AddItems(arguments.Select(ItemFactory.FromText));
        ShowPopup();
    }

    /// <summary>Adds to the shown level before the item at <paramref name="displayIndex"/> (-1 = at the end).</summary>
    void AddItems(IEnumerable<LaunchItem?> items, int displayIndex = -1)
    {
        var level = CurrentLevel;
        var added = Insert(level, ModelIndex(displayIndex), items);
        if (added.Count == 0)
            return;
        Save();
        Refresh(added[^1].Id);
    }

    /// <summary>Inserts, skipping what the level already has; returns what was added.</summary>
    static List<LaunchItem> Insert(List<LaunchItem> level, int index, IEnumerable<LaunchItem?> items)
    {
        var added = new List<LaunchItem>();
        foreach (var item in items)
        {
            if (item == null || level.Any(i => i.IsSameTarget(item)))
                continue;
            level.Insert(index++, item);
            added.Add(item);
        }
        return added;
    }

    /// <summary>
    /// Stored position for a position in the shown list: before the item shown there. In alphabetical
    /// order this keeps the new item in the same section (separators); the display re-sorts it.
    /// </summary>
    int ModelIndex(int displayIndex)
    {
        var level = CurrentLevel;
        return displayIndex >= 0 && displayIndex < _items.Count ? level.IndexOf(_items[displayIndex].Model) : level.Count;
    }

    /// <summary>A new folder or separator: after the selected item, or at the end.</summary>
    void InsertNew(LaunchItem item)
    {
        var level = CurrentLevel;
        int index = List.SelectedItem is ItemViewModel selected && level.IndexOf(selected.Model) is >= 0 and var at
            ? at + 1
            : level.Count;
        level.Insert(index, item);
        Save();
        Refresh(item.Id);
    }

    void NewFolder()
    {
        var group = new LaunchItem { Kind = ItemKind.Group, Name = "New folder", Children = [] };
        InsertNew(group);
        StartRename(ViewModelFor(group));
    }

    void NewSeparator() => InsertNew(new LaunchItem { Kind = ItemKind.Separator });

    void Remove(ItemViewModel vm)
    {
        var item = vm.Model;
        if (item.Kind == ItemKind.Group && item.Children is { Count: > 0 })
        {
            int count = ItemTree.CountLaunchables(item);
            var answer = ShowModal(() => MessageBox.Show(this,
                $"Remove the folder \"{item.Name}\" and the {count} shortcut{(count == 1 ? "" : "s")} in it?",
                "pLaunch", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No));
            if (answer != MessageBoxResult.Yes)
                return;
        }
        var container = ItemTree.FindContainer(_root, item.Id);
        if (container == null)
            return;
        int displayIndex = _items.IndexOf(vm);
        container.Remove(item);
        Forget(item);
        Save();
        Refresh();
        if (_items.Count > 0)
        {
            List.SelectedIndex = Math.Clamp(displayIndex, 0, _items.Count - 1);
            FocusSelected();
        }
    }

    void Forget(LaunchItem item)
    {
        _viewModels.Remove(item.Id);
        foreach (var child in item.Children ?? [])
            Forget(child);
    }

    /// <summary>Moves an item of any level to the shown level, before the item shown at <paramref name="displayIndex"/>.</summary>
    void MoveTo(LaunchItem item, int displayIndex)
    {
        if (_path.Contains(item))
            return; // a folder cannot go inside itself
        var anchor = displayIndex >= 0 && displayIndex < _items.Count ? _items[displayIndex].Model : null;
        if (anchor == item || ItemTree.FindContainer(_root, item.Id) is not { } source)
            return;
        var level = CurrentLevel;
        source.Remove(item);
        level.Insert(anchor == null ? level.Count : level.IndexOf(anchor), item);
        Save();
        Refresh(item.Id);
    }

    void MoveInto(LaunchItem item, LaunchItem group)
    {
        // Not into itself, nor into one of its own sub-folders
        if (ItemTree.IsSelfOrInside(group, item) || ItemTree.FindContainer(_root, item.Id) is not { } source)
            return;
        source.Remove(item);
        (group.Children ??= []).Add(item);
        Save();
        Refresh(group.Id);
    }

    /// <summary>The level above the shown sub-folder, and the position right after that sub-folder.</summary>
    (List<LaunchItem> Level, int Index)? ParentSlot()
    {
        if (_path.Count == 0)
            return null;
        var parent = _path.Count >= 2 ? (_path[^2].Children ??= []) : _root;
        return (parent, parent.IndexOf(_path[^1]) + 1);
    }

    void MoveToParent(LaunchItem item)
    {
        if (ParentSlot() is not ({ } parent, var index) || _path.Contains(item)
            || ItemTree.FindContainer(_root, item.Id) is not { } source)
            return;
        source.Remove(item);
        if (source == parent && parent.IndexOf(_path[^1]) + 1 < index)
            index--; // it was above the sub-folder in the same list
        parent.Insert(Math.Min(index, parent.Count), item);
        Save();
        Refresh();
    }

    void Save()
    {
        try
        {
            _store.Save(_root, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Cannot save the list.", ex.Message);
        }
        ScheduleJumpList();
    }

    void ScheduleJumpList() => _jumpList.Schedule(() => _root);

    /// <summary>A click or Enter: sub-folders open, shortcuts launch, separators do nothing.</summary>
    void Open(ItemViewModel item, bool asAdmin = false)
    {
        if (item.IsGroup)
            OpenGroup(item.Model);
        else if (!item.IsSeparator)
            Launch(item, asAdmin);
    }

    void Launch(ItemViewModel item, bool asAdmin = false)
    {
        if (Launcher.IsMissing(item.Model))
        {
            item.IsMissing = true;
            ShowError($"\"{item.Name}\" was not found.", item.Model.Target);
            return;
        }
        try
        {
            // Launch first: while pLaunch is still the foreground app the new window may take the focus
            if (Launcher.Launch(item.Model, asAdmin))
                HidePopup();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError($"Cannot open \"{item.Name}\".", ex.Message);
        }
    }

    void OpenLocation(ItemViewModel item)
    {
        try
        {
            Launcher.OpenLocation(item.Model);
            HidePopup();
        }
        catch (Win32Exception ex)
        {
            ShowError("Cannot open the location.", ex.Message);
        }
    }

    async void RefreshMissing()
    {
        var snapshot = _items.Where(i => i.Model.IsLaunchable).ToList();
        var missing = await Task.Run(() => snapshot.Select(i => Launcher.IsMissing(i.Model)).ToArray());
        for (int i = 0; i < snapshot.Count; i++)
            snapshot[i].IsMissing = missing[i];
        QueueIconLoad(); // an item that came back (drive plugged in) gets its icon now
    }

    int IconPixels => (int)Math.Round(_metrics.IconSize * VisualTreeHelper.GetDpi(this).DpiScaleX);

    /// <summary>
    /// Requests the icons still missing or of another size (DPI or item size change). Items without an
    /// icon are retried on every call, at most one request each at a time.
    /// </summary>
    void QueueIconLoad()
    {
        int pixels = IconPixels;
        foreach (var item in _items)
        {
            if (item.Model.IsLaunchable && !item.IconPending && (item.Icon == null || item.IconPixels != pixels))
                LoadIcon(item, pixels);
        }
    }

    async void LoadIcon(ItemViewModel item, int pixels)
    {
        item.IconPending = true;
        var icon = await IconProvider.GetAsync(item.Model, pixels);
        item.IconPending = false;
        if (icon != null && pixels == IconPixels)
        {
            item.Icon = icon;
            item.IconPixels = pixels;
        }
        else if (icon != null && item.Icon == null)
        {
            item.Icon = icon; // the size changed meanwhile: better than nothing until the right one arrives
            QueueIconLoad();
        }
    }

    // ---------------------------------------------------------------- rename

    void StartRename(ItemViewModel item)
    {
        if (item.IsSeparator)
            return;
        CommitRename();
        item.EditName = item.Name;
        item.IsEditing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container && FindRenameBox(container) is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        });
    }

    void CommitRename(bool cancel = false)
    {
        bool renamed = false;
        foreach (var item in _items.Where(i => i.IsEditing).ToList())
        {
            item.IsEditing = false;
            var name = item.EditName.Trim();
            if (!cancel && name.Length > 0 && name != item.Name)
            {
                item.Name = name;
                renamed = true;
            }
        }
        if (!renamed)
            return;
        Save();
        if (_settings.Sort == SortMode.Alphabetical)
            Dispatcher.BeginInvoke(() => Refresh()); // the new name may move it (not while its box is closing)
    }

    void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            CommitRename(cancel: e.Key == Key.Escape);
            FocusSelected();
            e.Handled = true;
        }
    }

    void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRename();

    /// <summary>The rename box of a row or tile; in the icons-only view it sits in a popup below the tile.</summary>
    static TextBox? FindRenameBox(ListBoxItem container) =>
        FindChild<TextBox>(container) ?? (FindChild<Popup>(container)?.Child as TextBox);

    // ---------------------------------------------------------------- keyboard and mouse

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_items.Any(i => i.IsEditing))
            return; // the rename box handles its own keys
        // Keys typed in an open context menu (its own popup window) belong to the menu: Enter must run
        // the highlighted command, Esc must close only the menu
        if (e.OriginalSource is DependencyObject source
            && PresentationSource.FromDependencyObject(source) is { } origin
            && origin != PresentationSource.FromVisual(this))
            return;
        var selected = List.SelectedItem as ItemViewModel;
        switch (e.Key)
        {
            case Key.Escape:
                if (_path.Count > 0)
                    GoBack();
                else
                    HidePopup();
                break;
            case Key.Back when _path.Count > 0:
                GoBack();
                break;
            case Key.System when e.SystemKey == Key.Left && _path.Count > 0: // Alt+Left
                GoBack();
                break;
            case Key.Left when _settings.View == ViewMode.List && _path.Count > 0:
                GoBack();
                break;
            case Key.Right when _settings.View == ViewMode.List && selected is { IsGroup: true }:
                OpenGroup(selected.Model);
                break;
            case Key.Enter when selected != null:
                // Ctrl+Shift+Enter runs as administrator, like in the Start menu
                Open(selected, asAdmin: Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                                        && Launcher.CanRunAsAdmin(selected.Model));
                break;
            case Key.F2 when selected is { IsSeparator: false }:
                StartRename(selected);
                break;
            case Key.Delete when selected != null:
                Remove(selected);
                break;
            case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                PasteFromClipboard();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = ItemAt(e.OriginalSource);
        if (_pressed?.IsEditing == true)
            _pressed = null;
        _pressPoint = e.GetPosition(List);
        if (_pressed?.IsSeparator == true)
            e.Handled = true; // draggable, but never selected
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var delta = e.GetPosition(List) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _pressed;
        _pressed = null;
        _draggingId = item.Model.Id;
        try
        {
            DragDrop.DoDragDrop(List, new DataObject(InternalDragFormat, item.Model.Id), DragDropEffects.Move);
        }
        finally
        {
            _draggingId = null;
            ClearDropMarkers();
            SetSpringTarget(null);
            ResetHeaderHighlight();
        }
    }

    // A single click launches, like the old Quick Launch (or opens a sub-folder)
    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = _pressed;
        _pressed = null;
        if (item != null && ItemAt(e.OriginalSource) == item)
        {
            e.Handled = true;
            Open(item);
        }
    }

    void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is { } item && !item.IsSeparator)
            List.SelectedItem = item;
    }

    void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = ItemAt(e.OriginalSource) ?? List.SelectedItem as ItemViewModel;
        if (item == null || item.IsEditing)
        {
            e.Handled = true;
            return;
        }
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        if (item.IsSeparator)
        {
            menu.Items.Add(CreateMenuItem("Remove separator", () => Remove(item), "\xE74D"));
            return;
        }

        var open = CreateMenuItem("Open", () => Open(item), item.IsGroup ? "\xE838" : "\xE8A7");
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        if (Launcher.CanRunAsAdmin(item.Model))
            menu.Items.Add(CreateMenuItem("Run as administrator", () => Launch(item, asAdmin: true), "\xEA18", "Ctrl+Shift+Enter"));
        if (Launcher.HasLocation(item.Model))
            menu.Items.Add(CreateMenuItem("Open file location", () => OpenLocation(item), "\xE838"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Rename", () => StartRename(item), "\xE8AC", "F2"));
        menu.Items.Add(CreateMenuItem("Remove", () => Remove(item), "\xE74D", "Del"));
        menu.Items.Add(new Separator());
        // Inserted right after this item (it is the selected one)
        menu.Items.Add(CreateMenuItem("New folder here", NewFolder, "\xE8F4"));
        menu.Items.Add(CreateMenuItem("Separator here", NewSeparator, "\xE76F"));
    }

    void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Top };
        menu.Items.Add(CreateMenuItem("Files\x2026", AddFiles, "\xE8E5"));
        menu.Items.Add(CreateMenuItem("Folder\x2026", AddFolder, "\xE8B7"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("New sub-folder", NewFolder, "\xE8F4"));
        menu.Items.Add(CreateMenuItem("Separator", NewSeparator, "\xE76F"));
        menu.Items.Add(new Separator());
        var paste = CreateMenuItem("Paste", PasteFromClipboard, "\xE77F", "Ctrl+V");
        paste.IsEnabled = ClipboardHasItems();
        menu.Items.Add(paste);
        menu.IsOpen = true;
    }

    void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = PlacementMode.Top };

        var view = CreateSubmenu("View", "\xE8FD");
        foreach (var (mode, label) in new[] { (ViewMode.List, "List"), (ViewMode.Grid, "Tiles"), (ViewMode.Icons, "Icons only") })
            view.Items.Add(CreateChoice(label, _settings.View == mode, () => { _settings.View = mode; SettingsChanged(); }));
        menu.Items.Add(view);

        var size = CreateSubmenu("Size", "\xE740");
        foreach (var (value, label) in new[] { (ItemSize.Small, "Small"), (ItemSize.Medium, "Medium"), (ItemSize.Large, "Large") })
            size.Items.Add(CreateChoice(label, _settings.Size == value, () => { _settings.Size = value; SettingsChanged(); }));
        menu.Items.Add(size);

        var sort = CreateSubmenu("Sort", "\xE8CB");
        sort.Items.Add(CreateChoice("Custom (drag to arrange)", _settings.Sort == SortMode.Custom,
            () => { _settings.Sort = SortMode.Custom; SettingsChanged(); }));
        sort.Items.Add(CreateChoice("Alphabetical", _settings.Sort == SortMode.Alphabetical,
            () => { _settings.Sort = SortMode.Alphabetical; SettingsChanged(); }));
        menu.Items.Add(sort);

        var theme = CreateSubmenu("Theme", "\xE790");
        foreach (var (value, label) in new[] { (ThemeChoice.System, "System"), (ThemeChoice.Light, "Light"), (ThemeChoice.Dark, "Dark") })
            theme.Items.Add(CreateChoice(label, _settings.Theme == value, () => { _settings.Theme = value; AppearanceChanged(); }));
        if (_settings.Background != null)
        {
            // With a custom color the text follows the color: say so instead of offering a choice that does nothing
            theme.Items.Add(new Separator());
            theme.Items.Add(new MenuItem { Header = "With a custom background the text color\nfollows the background", IsEnabled = false });
        }
        menu.Items.Add(theme);
        menu.Items.Add(CreateBackgroundMenu());

        menu.Items.Add(new Separator());
        var autostart = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = SafeAutostart() };
        autostart.Click += (_, _) => SetAutostart(autostart.IsChecked);
        menu.Items.Add(autostart);
        menu.Items.Add(CreateMenuItem("Open data folder", OpenDataFolder, "\xE838"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit", Close, "\xE711"));
        menu.IsOpen = true;
    }

    MenuItem CreateBackgroundMenu()
    {
        var background = CreateSubmenu("Background", "\xE771");
        background.Items.Add(CreateChoice("Acrylic (Windows)", _settings.Background == null,
            () => { _settings.Background = null; AppearanceChanged(); }));
        background.Items.Add(new Separator());
        bool isPreset = false;
        foreach (var (name, hex) in Appearance.Presets)
        {
            bool current = string.Equals(_settings.Background, hex, StringComparison.OrdinalIgnoreCase);
            isPreset |= current;
            background.Items.Add(CreateChoice(SwatchHeader(hex, name), current,
                () => { _settings.Background = hex; AppearanceChanged(); }));
        }
        background.Items.Add(new Separator());
        // A color picked in the dialog shows its swatch here, checked
        object customHeader = _settings.Background != null && !isPreset
            ? SwatchHeader(_settings.Background, "Custom color\x2026")
            : "Custom color\x2026";
        var custom = new MenuItem { Header = customHeader, IsChecked = _settings.Background != null && !isPreset };
        custom.Click += (_, _) => PickBackground();
        background.Items.Add(custom);
        background.Items.Add(new Separator());
        var translucent = new MenuItem
        {
            Header = "Translucent",
            IsCheckable = true,
            IsChecked = _settings.Translucent,
            IsEnabled = _settings.Background != null && _acrylic,
        };
        translucent.Click += (_, _) => { _settings.Translucent = translucent.IsChecked; AppearanceChanged(); };
        background.Items.Add(translucent);
        return background;
    }

    static StackPanel SwatchHeader(string hex, string name)
    {
        Appearance.TryParse(hex, out var color);
        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(color),
            Margin = new Thickness(0, 0, 10, 0),
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(swatch);
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    void PickBackground()
    {
        var start = Appearance.TryParse(_settings.Background, out var current) ? current : Color.FromRgb(0x2B, 0x34, 0x40);
        if (ShowModal(() => NativeMethods.PickColor(_hwnd, start)) is { } picked)
        {
            _settings.Background = Appearance.ToHex(picked);
            AppearanceChanged();
        }
        Activate(); // the dialog took the focus: without it the popup would not hide on the next click outside
    }

    void AppearanceChanged()
    {
        Save();
        ApplyAppearance();
    }

    static MenuItem CreateMenuItem(string header, Action action, string? glyph = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (glyph != null)
            item.Icon = GlyphIcon(glyph);
        item.Click += (_, _) => action();
        return item;
    }

    static MenuItem CreateSubmenu(string header, string glyph) => new() { Header = header, Icon = GlyphIcon(glyph) };

    /// <summary>One option of a group: the current one shows a check mark.</summary>
    static MenuItem CreateChoice(object header, bool current, Action choose)
    {
        var item = new MenuItem { Header = header, IsChecked = current };
        item.Click += (_, _) => { if (!current) choose(); };
        return item;
    }

    static TextBlock GlyphIcon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
        FontSize = 14,
    };

    // ---------------------------------------------------------------- adding

    void AddFiles()
    {
        var dialog = new OpenFileDialog { Title = "Add to pLaunch", Multiselect = true, DereferenceLinks = false };
        if (ShowModal(() => dialog.ShowDialog(this)) == true)
            AddItems(dialog.FileNames.Select(ItemFactory.FromPath));
    }

    void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Add to pLaunch", Multiselect = true };
        if (ShowModal(() => dialog.ShowDialog(this)) == true)
            AddItems(dialog.FolderNames.Select(ItemFactory.FromPath));
    }

    void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.GetDataObject() is { } data && DropReader.CanRead(data))
                AddItems(DropReader.Read(data));
        }
        catch (COMException)
        {
            // Clipboard locked by another app
        }
    }

    static bool ClipboardHasItems()
    {
        try { return Clipboard.GetDataObject() is { } data && DropReader.CanRead(data); }
        catch (COMException) { return false; }
    }

    // ---------------------------------------------------------------- drag and drop

    static bool IsAcceptable(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(InternalDragFormat) || DropReader.CanRead(data);
        }
        catch (COMException)
        {
            return false;
        }
    }

    static DragDropEffects EffectFor(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormat))
            return DragDropEffects.Move;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Link))
            return DragDropEffects.Link; // a shortcut to the file, never a copy
        return e.AllowedEffects & DragDropEffects.Copy;
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragAcceptable = IsAcceptable(e.Data);
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        e.Handled = true;
        ResetHeaderHighlight();
        if (!_dragAcceptable)
        {
            e.Effects = DragDropEffects.None;
            ClearDropMarkers();
            return;
        }
        e.Effects = EffectFor(e);
        var (index, into) = HitTest(e.GetPosition(List));
        if (into != null)
        {
            ShowDropMarker(into, DropMarker.Into);
            SetSpringTarget(into);
        }
        else
        {
            ShowInsertMarker(index);
            SetSpringTarget(null);
        }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        ClearDropMarkers();
        SetSpringTarget(null);
        var (index, into) = HitTest(e.GetPosition(List));

        if (e.Data.GetData(InternalDragFormat) is string id)
        {
            if (ItemTree.Find(_root, id) is { } item)
            {
                if (into != null)
                    MoveInto(item, into.Model);
                else
                    MoveTo(item, index);
            }
            return;
        }

        try
        {
            var dropped = DropReader.Read(e.Data);
            if (into != null)
            {
                if (Insert(into.Model.Children ??= [], into.Model.Children.Count, dropped).Count > 0)
                {
                    Save();
                    Refresh(into.Model.Id);
                }
            }
            else
            {
                AddItems(dropped, index);
            }
        }
        catch (COMException)
        {
            // The source went away mid-drop
        }
        Activate();
    }

    /// <summary>
    /// Where a drop at <paramref name="position"/> (list coordinates) goes: into a sub-folder (the middle
    /// of it), or before the item at the returned index (rows: upper/lower half, tiles: left/right half).
    /// </summary>
    (int Index, ItemViewModel? Into) HitTest(Point position)
    {
        bool rows = _settings.View == ViewMode.List;
        ItemViewModel? hit = null;
        Rect hitRect = default;
        double bottom = double.NegativeInfinity;
        for (int i = 0; i < _items.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible)
                continue;
            var rect = new Rect(container.TranslatePoint(new Point(0, 0), List), new Size(container.ActualWidth, container.ActualHeight));
            bottom = Math.Max(bottom, rect.Bottom);
            // Tiles have 2 px margins: count the gap around each one as part of it
            var zone = rows ? rect : Rect.Inflate(rect, 2, 2);
            if (zone.Contains(position))
            {
                hit = _items[i];
                hitRect = rect;
                break;
            }
        }
        if (hit == null)
        {
            // Below everything, or in a gap: rows compare with the item halves, tiles fall back to the end
            if (rows && position.Y < bottom)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (List.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c && c.IsVisible
                        && position.Y < c.TranslatePoint(new Point(0, 0), List).Y + c.ActualHeight / 2)
                        return (i, null);
                }
            }
            return (_items.Count, null);
        }

        int index = _items.IndexOf(hit);
        bool draggingThis = hit.Model.Id == _draggingId;
        if (hit.IsGroup && !draggingThis)
        {
            var inner = rows
                ? new Rect(hitRect.Left, hitRect.Top + hitRect.Height * 0.25, hitRect.Width, hitRect.Height * 0.5)
                : Rect.Inflate(hitRect, -hitRect.Width * 0.22, -hitRect.Height * 0.22);
            if (inner.Contains(position))
                return (index, hit);
        }
        bool after = rows || hit.IsSeparator
            ? position.Y > hitRect.Top + hitRect.Height / 2
            : position.X > hitRect.Left + hitRect.Width / 2;
        return (after ? index + 1 : index, null);
    }

    void ShowInsertMarker(int index)
    {
        // In alphabetical order the position is not kept: no line would be honest
        if (_settings.Sort == SortMode.Alphabetical)
        {
            ClearDropMarkers();
            return;
        }
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].DropMarker = i == index ? DropMarker.Before
                : index == _items.Count && i == _items.Count - 1 ? DropMarker.After
                : DropMarker.None;
        }
    }

    void ShowDropMarker(ItemViewModel target, DropMarker marker)
    {
        foreach (var item in _items)
            item.DropMarker = item == target ? marker : DropMarker.None;
    }

    void ClearDropMarkers()
    {
        foreach (var item in _items)
            item.DropMarker = DropMarker.None;
    }

    void SetSpringTarget(object? target)
    {
        if (ReferenceEquals(target, _springTarget))
            return;
        _springTarget = target;
        _springTimer.Stop();
        if (target != null)
            _springTimer.Start();
    }

    void SpringTimer_Tick(object? sender, EventArgs e)
    {
        _springTimer.Stop();
        var target = _springTarget;
        _springTarget = null;
        ClearDropMarkers();
        if (target is ItemViewModel { IsGroup: true } group && _items.Contains(group))
            OpenGroup(group.Model);
        else if (target == Header && _path.Count > 0)
            GoBack();
    }

    // The header (inside a sub-folder): a drop moves up one level, holding the drag there goes back

    void Header_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        ClearDropMarkers();
        if (e.RoutedEvent == DragEnterEvent)
            _dragAcceptable = IsAcceptable(e.Data);
        if (!_dragAcceptable)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = EffectFor(e);
        Header.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        SetSpringTarget(Header);
    }

    void Header_DragLeave(object sender, DragEventArgs e)
    {
        ResetHeaderHighlight();
        _dragLeaveTimer.Start();
    }

    void Header_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ResetHeaderHighlight();
        SetSpringTarget(null);
        if (e.Data.GetData(InternalDragFormat) is string id)
        {
            if (ItemTree.Find(_root, id) is { } item)
                MoveToParent(item);
            return;
        }
        try
        {
            if (ParentSlot() is ({ } parent, var index) && Insert(parent, index, DropReader.Read(e.Data)).Count > 0)
            {
                Save();
                Refresh();
            }
        }
        catch (COMException)
        {
        }
        Activate();
    }

    void ResetHeaderHighlight() => Header.Background = Brushes.Transparent;

    // ---------------------------------------------------------------- helpers

    /// <summary>Runs a modal dialog without the popup hiding itself when the dialog takes the focus.</summary>
    T ShowModal<T>(Func<T> show)
    {
        _suppressHide++;
        try { return show(); }
        finally { _suppressHide--; }
    }

    void ShowError(string message, string detail) =>
        ShowModal(() => MessageBox.Show(this, $"{message}\n\n{detail}", "pLaunch", MessageBoxButton.OK, MessageBoxImage.Warning));

    bool SafeAutostart()
    {
        try { return Autostart.IsEnabled; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    void SetAutostart(bool enabled)
    {
        try { Autostart.IsEnabled = enabled; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            ShowError("Cannot change the startup setting.", ex.Message);
        }
    }

    void OpenDataFolder()
    {
        Directory.CreateDirectory(ItemStore.DefaultDirectory);
        Process.Start(new ProcessStartInfo(ItemStore.DefaultDirectory) { UseShellExecute = true })?.Dispose();
        HidePopup();
    }

    void FocusSelected()
    {
        if (List.SelectedItem != null && List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListBoxItem container)
            container.Focus();
        else
            List.Focus();
    }

    static ItemViewModel? ItemAt(object source)
    {
        var node = source as DependencyObject;
        while (node != null && node is not ListBoxItem)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return (node as ListBoxItem)?.DataContext as ItemViewModel;
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } found)
                return found;
        }
        return null;
    }
}
