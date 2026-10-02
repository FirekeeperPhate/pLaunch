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
    readonly ClosingClick _closingClick = new();
    /// <summary>Holding a drag over a sub-folder opens it; over the back button, goes up.</summary>
    static readonly TimeSpan SpringDelay = TimeSpan.FromMilliseconds(800);

    readonly ListProfile _profile;
    // Replaced when the list is reloaded (synced from another PC, data folder moved)
    ItemStore _store;
    List<LaunchItem> _root;
    LauncherSettings _settings;
    /// <summary>The sub-folders opened, from the top level down to the one shown.</summary>
    readonly List<LaunchItem> _path = [];
    /// <summary>What the list shows: the current level in display order.</summary>
    readonly ObservableCollection<ItemViewModel> _items = [];
    /// <summary>One view model per item across refreshes (keeps the icons).</summary>
    readonly Dictionary<string, ItemViewModel> _viewModels = [];
    readonly DispatcherTimer _dragLeaveTimer;
    readonly DispatcherTimer _hoverWatch;
    readonly DispatcherTimer _springTimer;
    readonly JumpListBuilder _jumpList;
    ViewMetrics _metrics = ViewMetrics.For(ViewMode.List, ItemSize.Medium);
    IntPtr _hwnd;
    int _suppressHide;
    NativeMethods.POINT _anchor;
    int _outsideTicks;
    Point _pressPoint;
    ItemViewModel? _pressed;
    string? _draggingId;
    bool _dragAcceptable;
    bool _acrylic;
    object? _springTarget; // an ItemViewModel (sub-folder to open) or the Header (go back)

    public PopupWindow(ItemStore store, ListProfile? profile = null)
    {
        InitializeComponent();
        _profile = profile ?? ListProfile.Default;
        _jumpList = new JumpListBuilder(_profile);
        Title = _profile.Title;
        _store = store;
        var data = store.Load();
        _root = data.Items;
        _settings = data.Settings;
        IconProvider.WebIconsEnabled = _settings.WebIcons;
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
        PreviewTextInput += OnPreviewTextInput;
        PreviewMouseMove += (_, _) => PointerStillSinceOpen(); // anywhere in the popup (footer, search...)
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => _dragLeaveTimer.Start();
        Drop += OnDrop;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) =>
        {
            _dragWatch?.Stop();
            CloseMenus();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _hotkeys?.Dispose();
        };
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrolled));
        HookMiddleClick(List);

        ApplyView();
        ApplyAppearance();
        // Ids repaired while loading: write them now, before the jump list refers to them
        if (store.IdsRepaired && store.LoadError == null)
            Save();
        WatchStoreFile();
        InitUpdates();
    }

    bool IsOpen => IsVisible && WindowState == WindowState.Normal;

    TaskbarButton? _taskbarButton; // out of Alt+Tab and Win+Tab, with a taskbar button all the same

    /// <summary>False only in UI test harnesses, so an opened popup never takes the focus from the user.</summary>
    internal bool ActivateOnOpen { get; set; } = true;

    /// <summary>The level shown: the top, a sub-folder, or the (read-only) content of a live folder.</summary>
    List<LaunchItem> CurrentLevel =>
        _path.Count == 0 ? _root
        : _path[^1].Kind == ItemKind.Group ? (_path[^1].Children ??= [])
        : LiveEntries(_path[^1]);

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
        ApplyIconPlace(); // the taskbar button, the notification area icon, or both
        StartDragWatch();
        QueueIconLoad();
        ScheduleJumpList();
        if (!minimized)
            ShowPopup();
    }

    public void ShowPopup()
    {
        _closingClick.Forget();
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
            _closingClick.PopupHidden(DateTime.UtcNow, PressedOnTaskbar());
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
                // The restore of a closing click that got through all the same (not asked with SC_RESTORE,
                // which the window procedure holds back): closed again at once
                if (_closingClick.Take(DateTime.UtcNow))
                {
                    Dispatcher.BeginInvoke(() => WindowState = WindowState.Minimized);
                    return;
                }
                OnPopupOpened();
                break;
            case WindowState.Minimized:
                _stayOpen = false;
                Topmost = false;
                _hoverWatch.Stop();
                CloseMenus();
                CommitRename();
                ClearDropMarkers();
                SetSpringTarget(null);
                // Back to the top level (and no search) while hidden: the next opening starts there, like a
                // menu, and items forwarded meanwhile ("Send to", command line) do not land in a sub-folder
                // nobody sees
                bool reset = _path.Count > 0 || IsSearching || _resortPending;
                _resortPending = false;
                _path.Clear();
                ClearSearchText();
                if (reset)
                    Refresh();
                break;
        }
    }

    void OnPopupOpened()
    {
        if (_openAt is { } place)
            _anchor = place;
        else
            NativeMethods.GetCursorPos(out _anchor);
        _openAt = null;
        RememberPointerAtOpen();
        Place();
        if (ActivateOnOpen)
            Activate();
        List.SelectedIndex = -1;
        List.Focus();
        RefreshMissing();
        RefreshRunning();
        QueueIconLoad();

        // Opened by hovering the taskbar button during a drag: close again if the drop happens elsewhere
        if (IsLeftButtonDown())
        {
            _outsideTicks = 0;
            _hoverWatch.Start();
        }
    }

    /// <summary>The left button is held on a taskbar: on pLaunch's button, its notification area icon, or anything else there.</summary>
    static bool PressedOnTaskbar() =>
        IsLeftButtonDown() && NativeMethods.GetCursorPos(out var at) && TaskbarHitTest.IsTaskbarAt(at);

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressHide > 0 || WindowState != WindowState.Normal)
            return;
        // Kept open after a middle click: the focus going to what was launched does not close the list. A
        // press on the taskbar does: on pLaunch's button or icon it is the click that closes the list, on
        // anything else there the user has moved on.
        if (_stayOpen && !PressedOnTaskbar())
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsActive || _suppressHide > 0)
                return;
            // One of its own menus got activated after all: the popup takes the focus back instead of closing
            if (_menus.Any(m => m.IsActive))
                Activate();
            else
                HidePopup(auto: true);
        });
    }

    void HoverWatch_Tick(object? sender, EventArgs e)
    {
        if (!IsOpen || IsActive && !_stayOpen)
        {
            _hoverWatch.Stop();
            return;
        }
        // Still in use: the list kept open is the active window (typing a search with the pointer aside), or
        // the pointer is on the popup or a menu, in one of its dialogs, or in a context menu (it holds the mouse)
        if (IsActive || IsLeftButtonDown() || IsCursorOverWindow() || _suppressHide > 0 || Mouse.Captured != null)
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
        && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom
        || IsCursorOverMenu();

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

        _taskbarButton = TaskbarButton.For(_hwnd); // a launcher, not a window to switch to; the button comes in Start
        NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, 1); // no minimize/restore animation
        NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        // No acrylic before Windows 11 22H2: a solid background then
        _acrylic = NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_TRANSIENTWINDOW) == 0;
        ApplyAppearance();
        ApplyListIdentity();
        _hotkeys = new GlobalHotkeys(_hwnd);
        RegisterHotkeys(force: true);
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

    static bool IsSystemDark() => IsDarkMode("AppsUseLightTheme");

    /// <summary>The Windows mode (taskbar, Start): it can be dark while the apps are light, and the other way round.</summary>
    static bool IsTaskbarDark() => IsDarkMode("SystemUsesLightTheme");

    static bool IsDarkMode(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(valueName) is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
            return;
        Dispatcher.BeginInvoke(ApplyAppearance);
        if (_settings.TrayIcon == TrayIconStyle.Automatic)
            Dispatcher.BeginInvoke(ShowTrayIcon); // the taskbar may have turned light or dark
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Win+Up or a double click on the taskbar thumbnail must not maximize the flyout
        if (_hotkeys?.HandleMessage(msg, wParam) == true)
        {
            handled = true;
            return IntPtr.Zero;
        }
        int command = msg == NativeMethods.WM_SYSCOMMAND ? (int)wParam & 0xFFF0 : 0;
        if (command == NativeMethods.SC_MAXIMIZE)
        {
            handled = true;
        }
        else if (command == NativeMethods.SC_RESTORE && _closingClick.Take(DateTime.UtcNow))
        {
            // The second half of a click on the taskbar button of the open popup (see ClosingClick): the
            // popup stays closed, without showing up again for a moment first.
            handled = true;
            Dispatcher.BeginInvoke(PassFocusOn, DispatcherPriority.Background);
        }
        else if (msg == TrayIcon.CallbackMessage)
        {
            OnTrayMessage(wParam, lParam);
        }
        else if (msg == TaskbarTab.TaskbarCreatedMessage)
        {
            _tray?.Forget(); // Explorer restarted: the notification area is new
            ShowTrayIcon();
        }
        else if (msg == NativeMethods.WM_DESTROY)
        {
            RemoveTrayIcon();
        }
        _taskbarButton?.HandleMessage(msg, lParam);
        return IntPtr.Zero;
    }

    /// <summary>
    /// With the restore held back, the taskbar has still made the minimized popup the active window: the
    /// keys would go to a window nobody sees. The focus goes to the window in front, as it does when a
    /// window is minimized.
    /// </summary>
    void PassFocusOn()
    {
        if (WindowState != WindowState.Minimized || WindowInterop.GetForegroundWindow() != _hwnd)
            return;
        var next = WindowInterop.AppWindows().Select(w => w.Handle).FirstOrDefault(h => !WindowInterop.IsIconic(h));
        if (next != IntPtr.Zero)
            WindowInterop.SetForegroundWindow(next);
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
        _items.Clear(); // other templates: every container is rebuilt anyway
        Refresh();
    }

    void SettingsChanged()
    {
        Save();
        ApplyView();
    }

    /// <summary>
    /// Brings the shown list in line with the model, keeping (or moving) the selection. The collection is
    /// updated in place (moves, inserts, removals), so the scroll position and the existing rows survive.
    /// </summary>
    void Refresh(string? selectId = null)
    {
        selectId ??= (List.SelectedItem as ItemViewModel)?.Model.Id;
        List<ItemViewModel> target;
        if (IsSearching)
        {
            // Results from every sub-folder, best first; the tooltip tells where each one lives. Around
            // them what the text itself can open: a path or an address first, a command or a web search last.
            // What the text opens comes from a background look-up (it touches the disk): until it arrives
            // only the web search is there
            var (first, last) = _suggestions.Text == _search ? (_suggestions.First, _suggestions.Last) : ([], []);
            target = first.Select(ViewModelFor).ToList();
            foreach (var (item, location) in ItemSearch.Find(_root, _search))
            {
                var vm = ViewModelFor(item);
                vm.Location = location; // the sub-folder it is in, shown beside the name
                target.Add(vm);
            }
            target.AddRange(last.Select(ViewModelFor));
            if (RunSuggestions.WebSearchFor(_search, _settings.WebSearch) is { } webSearch)
                target.Add(ViewModelFor(webSearch));
        }
        else
        {
            // Live folders come sorted from the disk (folders first); saved levels follow the chosen order
            var level = CurrentLevel;
            target = (InLiveFolder ? level : ItemTree.DisplayOrder(level, _settings.Sort)).Select(ViewModelFor).ToList();
            // No search: no row says where it is, wherever it shows (the rows found are shared with the menus)
            foreach (var vm in _viewModels.Values)
                vm.Location = null;
        }
        foreach (var vm in target)
        {
            vm.DropMarker = DropMarker.None;
            if (vm.IsGroup)
                vm.RefreshChildInfo();
        }
        SyncItems(_items, target);

        Header.Visibility = _path.Count > 0 && !IsSearching ? Visibility.Visible : Visibility.Collapsed;
        SearchBar.Visibility = IsSearching || SearchBox.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
        HeaderTitle.Text = string.Join(" \x203A ", _path.Select(g => g.Name))
            + (InLiveFolder && _liveTruncated.Contains(_path[^1].Target) ? $"  (first {LiveFolder.MaxEntries})" : "");
        EmptyHintText.Text = IsSearching ? $"Nothing matches “{_search}”."
            : InLiveFolder ? LiveFolderHint()
            : _path.Count > 0 ? "This folder is empty. Drag shortcuts here."
            : "Drag programs, files, folders or links here. You can also drop them on the pLaunch taskbar button.";
        UpdateEmptyHint();
        AddButton.IsEnabled = !InLiveFolder; // the content of a live folder comes from the disk

        if (selectId != null && _items.FirstOrDefault(i => i.Model.Id == selectId) is { } selected)
        {
            List.SelectedItem = selected;
            List.ScrollIntoView(selected);
        }
        QueueIconLoad();
        if (IsOpen)
        {
            Place();
            RefreshRunning();
        }
        RefreshMenus();
    }

    ItemViewModel ViewModelFor(LaunchItem item)
    {
        // Items made on the fly (live folder entries, search suggestions) come as new objects on every
        // refresh: the same id is the same thing, so the row (and its icon) stays
        if (_viewModels.TryGetValue(item.Id, out var vm)
            && (vm.Model == item || (item.IsLive && vm.Model.IsLive && vm.Model.Target == item.Target && vm.Model.Name == item.Name)))
            return vm;
        _viewModels[item.Id] = vm = new ItemViewModel(item);
        return vm;
    }

    void UpdateEmptyHint() => EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- sub-folders

    /// <summary>Opens a sub-folder or a live folder inside the popup (from a search result: where it lives).</summary>
    void OpenGroup(LaunchItem group)
    {
        CommitRename();
        if (IsSearching)
        {
            _path.Clear();
            _path.AddRange(ItemTree.PathTo(_root, group.Id) ?? []);
            ClearSearchText();
        }
        if (group.Kind == ItemKind.Folder)
            _liveCache.Remove(group.Target); // read the disk again: the content may have changed
        _path.Add(group);
        Refresh();
        List.SelectedIndex = -1;
        ScrollToTop(); // a new level: the list is updated in place, so it would keep the old offset
        List.Focus();
    }

    void ScrollToTop() => FindChild<ScrollViewer>(List)?.ScrollToHome();

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
        InsertAfter(CurrentLevel, (List.SelectedItem as ItemViewModel)?.Model, item);
        Save();
        Refresh(item.Id);
    }

    /// <summary>Puts <paramref name="item"/> right after <paramref name="after"/> in <paramref name="level"/> (at the end when it is not there).</summary>
    static void InsertAfter(List<LaunchItem> level, LaunchItem? after, LaunchItem item)
    {
        int index = after != null && level.IndexOf(after) is >= 0 and var at ? at + 1 : level.Count;
        level.Insert(index, item);
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
        // A folder with something in it (separators alone do not count) asks first
        if (item.Kind == ItemKind.Group && ItemTree.DescribeContent(item) is { } content)
        {
            var answer = ShowModal(() => MessageBox.Show(this,
                $"Remove the folder \"{item.Name}\" and everything in it ({content})?",
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
        // The next item takes the selection; separators are skipped (a second Del must not remove one)
        // (not when it was removed from a menu)
        int next = displayIndex >= 0 ? NextSelectable(displayIndex) : -1;
        if (next >= 0)
        {
            List.SelectedIndex = next;
            List.ScrollIntoView(List.SelectedItem);
            FocusSelected();
        }
    }

    /// <summary>The first non-separator at or after <paramref name="index"/>, else before it; -1 if none.</summary>
    int NextSelectable(int index)
    {
        for (int i = Math.Max(index, 0); i < _items.Count; i++)
        {
            if (!_items[i].IsSeparator)
                return i;
        }
        for (int i = Math.Min(index, _items.Count) - 1; i >= 0; i--)
        {
            if (!_items[i].IsSeparator)
                return i;
        }
        return -1;
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
        var anchor = displayIndex >= 0 && displayIndex < _items.Count ? _items[displayIndex].Model : null;
        if (MoveBefore(item, _path.LastOrDefault(), CurrentLevel, anchor))
            Refresh(item.Id);
    }

    /// <summary>
    /// Moves an item of any level into <paramref name="level"/> (the content of <paramref name="owner"/>,
    /// null = the top), before <paramref name="anchor"/> or at the end, and saves. False when it cannot go
    /// there: a folder never goes inside itself or one of its own sub-folders.
    /// </summary>
    bool MoveBefore(LaunchItem item, LaunchItem? owner, List<LaunchItem> level, LaunchItem? anchor)
    {
        if (anchor == item || (owner != null && ItemTree.IsSelfOrInside(owner, item))
            || ItemTree.FindContainer(_root, item.Id) is not { } source)
            return false;
        source.Remove(item);
        level.Insert(anchor == null || !level.Contains(anchor) ? level.Count : level.IndexOf(anchor), item);
        Save();
        return true;
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
        // Inside a live folder's own sub-folder the level above comes from the disk too: nothing to drop into
        if (_path.Count == 0 || (_path.Count >= 2 && _path[^2].Kind != ItemKind.Group))
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
        RegisterHotkeys(); // only does something when a shortcut was added, changed or removed
    }

    void ScheduleJumpList() => _jumpList.Schedule(() => _root);

    /// <summary>A click or Enter: sub-folders and live folders open inside, shortcuts launch, separators do nothing.</summary>
    void Open(ItemViewModel item, bool asAdmin = false)
    {
        if (item.IsNavigable && MenuMode)
            ToggleMenu(item, level: 0); // a folder in a menu opens through the menu's own handlers
        else if (item.IsNavigable)
            OpenGroup(item.Model);
        else if (!item.IsSeparator)
            Launch(item, asAdmin);
    }

    /// <summary>A launch counts for the "most used" order (saved items only: live folder entries are not kept).</summary>
    void CountLaunches(IEnumerable<LaunchItem> launched, bool alreadyRecorded = false, bool resort = true)
    {
        bool any = false;
        foreach (var item in launched.Where(i => !i.IsLive))
        {
            if (!alreadyRecorded)
                Launcher.RecordLaunch(item);
            any = true;
        }
        if (!any)
            return;
        Save();
        // Not under a pointer that goes on launching (the list kept open): sorted again when it closes
        if (_settings.Sort == SortMode.MostUsed && !resort)
            _resortPending = true;
        else if (_settings.Sort == SortMode.MostUsed)
            Refresh();
    }

    /// <summary>"--launched id" from a jump list entry.</summary>
    public void CountLaunch(string id)
    {
        if (ItemTree.Find(_root, id) is { } item)
            CountLaunches([item]);
    }

    /// <summary>The selected items in display order (Ctrl/Shift+click select several).</summary>
    List<ItemViewModel> SelectedItems() =>
        _items.Where(i => List.SelectedItems.Contains(i) && !i.IsSeparator).ToList();

    /// <summary>Launches several shortcuts at once and closes the popup; a list of what failed, if anything.</summary>
    void LaunchMany(IReadOnlyCollection<LaunchItem> items, string what)
    {
        const int askAbove = 10;
        if (items.Count > askAbove && ShowModal(() => MessageBox.Show(this, $"Open {items.Count} {what}?", "pLaunch",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) != MessageBoxResult.Yes)
            return;
        Cloak(true);
        var (started, errors) = Launcher.LaunchAll(items);
        CountLaunches(items.Where(i => i.LastLaunched != null), alreadyRecorded: true);
        if (errors.Count == 0 && started > 0)
            HidePopup();
        Cloak(false);
        if (errors.Count > 0)
            ShowError(started > 0 ? $"{started} opened, {errors.Count} could not be opened:" : "Nothing could be opened:",
                string.Join("\n", errors.Take(10)));
    }

    /// <summary>"Open all": the shortcuts directly inside a sub-folder (not those of its own sub-folders).</summary>
    void OpenAll(LaunchItem group) =>
        LaunchMany((group.Children ?? []).Where(c => c.IsLaunchable).ToList(), $"shortcuts from \"{group.Name}\"");

    void ShowProperties(ItemViewModel vm)
    {
        if (vm.IsSeparator || vm.Model.IsLive)
            return;
        CommitRename();
        var dialog = new Views.PropertiesWindow(vm.Model) { Owner = this };
        if (ShowModal(() => dialog.ShowDialog()) != true)
            return;
        vm.NotifyModelChanged(); // name, tooltip, glyph
        vm.Icon = null;          // target or icon changed: load again
        vm.IconPixels = 0;
        vm.IsMissing = Launcher.IsMissing(vm.Model);
        Save(); // also registers a changed shortcut
        DropTakenHotkey(vm.Model);
        Refresh(vm.Model.Id);
    }

    /// <summary>
    /// Launches the item and closes the list; with <paramref name="keepOpen"/> (a middle click) the list stays
    /// for the next one, until the pointer leaves it.
    /// </summary>
    void Launch(ItemViewModel item, bool asAdmin = false, bool newWindow = false, bool keepOpen = false)
    {
        if (Launcher.IsMissing(item.Model))
        {
            item.IsMissing = true;
            ShowError($"\"{item.Name}\" was not found.", item.Model.Target);
            return;
        }
        // Gone at once, while the shell starts the program (a Start menu app keeps it busy a moment)
        if (!keepOpen)
            Cloak(true);
        try
        {
            // Launch first: while pLaunch is still the foreground app the new window may take the focus.
            // A snippet is only copied when the list stays: there is no window to paste it into yet.
            if (Launcher.Launch(item.Model, asAdmin, newWindow, paste: !keepOpen))
            {
                CountLaunches([item.Model], resort: !keepOpen); // saved before the popup goes: an error is shown with it
                if (keepOpen)
                    StayOpen();
                else
                    HidePopup();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError($"Cannot open \"{item.Name}\".", ex.Message);
        }
        finally
        {
            Cloak(false); // minimized by now, or back as it was (a declined elevation)
        }
    }

    bool _stayOpen; // after a middle click: losing the focus to what was launched does not close the list
    bool _resortPending; // launches counted while the list was kept open: "most used" is sorted again once it closes

    /// <summary>
    /// The list stays open, above the window that just opened, for launching something else. While it is
    /// the active window it stays like any open list (Esc or a normal launch close it). Once the focus is
    /// elsewhere, it closes when the pointer has left it for a moment (HoverWatch_Tick), or at a press on
    /// the taskbar (OnDeactivated).
    /// </summary>
    void StayOpen()
    {
        _stayOpen = true;
        Topmost = true;
        _outsideTicks = 0;
        _hoverWatch.Start();
    }

    /// <summary>A middle click on a shortcut launches it and keeps the list open (a list, or a menu's).</summary>
    void HookMiddleClick(ListBox list)
    {
        ItemViewModel? pressed = null;
        list.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
                pressed = ItemAt(e.OriginalSource);
        };
        list.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle)
                return;
            var item = pressed;
            pressed = null;
            if (item is { IsSeparator: false, IsNavigable: false, IsEditing: false } && ItemAt(e.OriginalSource) == item)
            {
                e.Handled = true;
                Launch(item, keepOpen: true);
            }
        };
    }

    /// <summary>
    /// Hides the popup and its menus without minimizing them: pLaunch stays the foreground app, so what
    /// it starts meanwhile may still take the focus.
    /// </summary>
    void Cloak(bool cloaked)
    {
        foreach (var hwnd in _menus.Select(m => m.Handle).Prepend(_hwnd).Where(h => h != IntPtr.Zero))
            NativeMethods.SetDwmInt(hwnd, NativeMethods.DWMWA_CLOAK, cloaked ? 1 : 0);
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
    void QueueIconLoad(IEnumerable<ItemViewModel>? items = null)
    {
        int pixels = IconPixels;
        foreach (var item in items ?? _items)
        {
            if (RunSuggestions.IsNetworkSuggestion(item.Model))
                continue; // a glyph: the path is still being typed, and asking a server can take long
            if (item.Model.HasShellIcon && !item.IconPending && (item.Icon == null || item.IconPixels != pixels))
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
            if (!item.IsEditing || List.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                return;
            var box = _settings.View == ViewMode.Icons ? ShowOverlayRenameBox(container, item) : FindChild<TextBox>(container);
            if (box != null)
            {
                box.Focus();
                box.SelectAll();
            }
        });
    }

    /// <summary>Icons-only view: the window's rename box, under the tile (above it on the last row).</summary>
    TextBox ShowOverlayRenameBox(ListBoxItem container, ItemViewModel item)
    {
        var host = OverlayRenameHost;
        host.DataContext = item;
        host.Width = Math.Min(200, Math.Max(80, RenameOverlay.ActualWidth - 8));
        host.Visibility = Visibility.Visible;
        host.UpdateLayout();
        PlaceOverlayRenameBox(container);
        return OverlayRenameBox;
    }

    void PlaceOverlayRenameBox(ListBoxItem container)
    {
        var host = OverlayRenameHost;
        var tile = container.TranslatePoint(new Point(0, 0), RenameOverlay);
        double left = tile.X + container.ActualWidth / 2 - host.Width / 2;
        double top = tile.Y + container.ActualHeight + 2;
        if (top + host.ActualHeight > RenameOverlay.ActualHeight)
            top = tile.Y - host.ActualHeight - 2;
        Canvas.SetLeft(host, Math.Clamp(left, 4, Math.Max(4, RenameOverlay.ActualWidth - host.Width - 4)));
        Canvas.SetTop(host, Math.Max(0, top));
    }

    /// <summary>The icons-only rename box follows its tile; once the tile scrolls out of view the rename is done.</summary>
    void OnListScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
            RepositionMenus(0); // they follow their folder's row
        if (e.VerticalChange == 0 || OverlayRenameHost.Visibility != Visibility.Visible
            || OverlayRenameHost.DataContext is not ItemViewModel item
            || List.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
            return;
        double top = container.TranslatePoint(new Point(0, 0), List).Y;
        if (top + container.ActualHeight <= 0 || top >= List.ActualHeight)
        {
            CommitRename();
            List.Focus();
        }
        else
        {
            PlaceOverlayRenameBox(container);
        }
    }

    void HideOverlayRenameBox()
    {
        OverlayRenameHost.Visibility = Visibility.Collapsed;
        OverlayRenameHost.DataContext = null;
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
        // Hidden after the loop: collapsing the focused box raises LostKeyboardFocus, which lands back
        // here and finds nothing left to commit
        if (OverlayRenameHost.Visibility == Visibility.Visible)
            HideOverlayRenameBox();
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
        // An open menu (it never has the focus) gets the arrows, Enter, Esc...
        if (_menus.Count > 0 && HandleMenuKey(e))
        {
            e.Handled = true;
            return;
        }
        var selected = List.SelectedItem as ItemViewModel;
        switch (e.Key)
        {
            case Key.Escape:
                if (IsSearching)
                    ClearSearch();
                else if (_path.Count > 0)
                    GoBack();
                else
                    HidePopup();
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control: // Ctrl+F: the search box
                SearchBar.Visibility = Visibility.Visible;
                SearchBox.Focus();
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
            case Key.Right when _settings.View == ViewMode.List && selected is { IsNavigable: true }:
                if (MenuMode)
                    OpenMenu(selected, 0, selectFirst: true);
                else
                    OpenGroup(selected.Model);
                break;
            case Key.Enter when List.SelectedItems.Count > 1:
                LaunchMany(SelectedItems().Select(i => i.Model).Where(m => m.IsLaunchable).ToList(), "shortcuts");
                break;
            case Key.Enter when selected != null:
                // Ctrl+Shift+Enter runs as administrator, like in the Start menu
                Open(selected, asAdmin: Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                                        && Launcher.CanRunAsAdmin(selected.Model));
                break;
            // Live folder entries come from the disk: they cannot be edited here
            case Key.System when e.SystemKey == Key.Enter && selected is { IsSeparator: false } && !selected.Model.IsLive: // Alt+Enter
                ShowProperties(selected);
                break;
            case Key.F2 when selected is { IsSeparator: false } && !selected.Model.IsLive:
                StartRename(selected);
                break;
            case Key.Delete when List.SelectedItems.Count > 1 && !InLiveFolder:
                RemoveMany(SelectedItems());
                break;
            case Key.Delete when selected != null && !selected.Model.IsLive:
                Remove(selected);
                break;
            case Key.V when Keyboard.Modifiers == ModifierKeys.Control && !InLiveFolder:
                PasteFromClipboard();
                break;
            // 1-9 open the n-th item; while searching they are part of the search text
            case >= Key.D1 and <= Key.D9 when Keyboard.Modifiers == ModifierKeys.None && !IsSearching:
                OpenByNumber(e.Key - Key.D1);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9 when Keyboard.Modifiers == ModifierKeys.None && !IsSearching:
                OpenByNumber(e.Key - Key.NumPad1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Keys 1-9: the n-th item of the level shown (separators do not count).</summary>
    void OpenByNumber(int index)
    {
        var item = _items.Where(i => !i.IsSeparator).ElementAtOrDefault(index);
        if (item != null)
            Open(item);
    }

    void RemoveMany(List<ItemViewModel> items)
    {
        if (items.Count == 1)
        {
            Remove(items[0]);
            return;
        }
        var folders = items.Count(i => i.IsGroup && ItemTree.DescribeContent(i.Model) != null);
        var message = folders > 0
            ? $"Remove {items.Count} items, including {folders} folder{(folders == 1 ? "" : "s")} and everything in {(folders == 1 ? "it" : "them")}?"
            : $"Remove {items.Count} items?";
        if (ShowModal(() => MessageBox.Show(this, message, "pLaunch", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No)) != MessageBoxResult.Yes)
            return;
        int first = items.Min(i => _items.IndexOf(i));
        foreach (var vm in items)
        {
            if (ItemTree.FindContainer(_root, vm.Model.Id) is { } container)
            {
                container.Remove(vm.Model);
                Forget(vm.Model);
            }
        }
        Save();
        List.SelectedItems.Clear();
        Refresh();
        if (NextSelectable(first) is var next and >= 0)
        {
            List.SelectedIndex = next;
            FocusSelected();
        }
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = ItemAt(e.OriginalSource);
        if (_pressed?.IsEditing == true)
            _pressed = null;
        if (_pressed == null && !IsInScrollBar(e.OriginalSource))
            CloseMenus(); // a click on nothing in particular closes them, like any menu
        _pressPoint = e.GetPosition(List);
        if (_pressed?.IsSeparator == true)
            e.Handled = true; // draggable, but never selected
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed)
        {
            ScheduleMenuHover(ItemAt(e.OriginalSource), 0); // switches an open menu to another sub-folder
            return;
        }
        var delta = e.GetPosition(List) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _pressed;
        _pressed = null;
        _draggingId = item.Model.Id;
        try
        {
            var (data, effects) = CreateDragData(item.Model);
            DragDrop.DoDragDrop(List, data, effects);
        }
        finally
        {
            _draggingId = null;
            ClearDropMarkers();
            SetSpringTarget(null);
            ResetHeaderHighlight();
        }
    }

    // A single click launches, like the old Quick Launch (or opens a sub-folder); with Ctrl or Shift it
    // only selects, to open several items at once with Enter
    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = _pressed;
        _pressed = null;
        if (item != null && ItemAt(e.OriginalSource) == item
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            e.Handled = true;
            Open(item);
        }
    }

    void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInScrollBar(e.OriginalSource))
            return; // the scroll bar is not a blank area: the selection stays
        var item = ItemAt(e.OriginalSource);
        if (item == null)
            List.SelectedItem = null; // blank area: what the menu adds goes at the end
        else if (!item.IsSeparator && !List.SelectedItems.Contains(item))
            List.SelectedItem = item; // a right click inside a multiple selection keeps it
        if (item != null && !item.IsSeparator)
            e.Handled = List.SelectedItems.Count > 1; // the ListBox would reduce the selection to this item
    }

    /// <summary>
    /// What a drag carries: the item id (moving it inside pLaunch) and, for launchable items, something
    /// Explorer and other apps understand. Real files and folders are offered as a link only, so a drop on
    /// the desktop creates a shortcut and can never move or copy the file itself.
    /// </summary>
    (DataObject Data, DragDropEffects Effects) CreateDragData(LaunchItem item)
    {
        var data = new DataObject();
        data.SetData(InternalDragFormat, item.Id);
        var effects = DragDropEffects.Link;
        switch (item.Kind)
        {
            case ItemKind.File or ItemKind.Folder when !Launcher.IsMissing(item):
                data.SetFileDropList([item.Target]);
                break;
            case ItemKind.Url:
                data.SetText(item.Target);
                if (CreateUrlFile(item) is { } urlFile)
                {
                    // A temporary "Name.url": copying it is harmless, and it gives the shortcut its name
                    data.SetFileDropList([urlFile]);
                    effects |= DragDropEffects.Copy;
                }
                break;
            case ItemKind.Shell when ShellInterop.CreateIdListArray(item.Target) is { } idList:
                data.SetData(DropReader.ShellIdListFormat, idList);
                break;
            case ItemKind.Text or ItemKind.Command:
                // Dropped into an editor: the snippet (or the command line) is inserted there
                data.SetText(item.Target.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                effects |= DragDropEffects.Copy;
                break;
        }
        return (data, effects);
    }

    static string? CreateUrlFile(LaunchItem item)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "pLaunch-links");
            Directory.CreateDirectory(folder);
            var name = string.Concat(item.Name.Split(Path.GetInvalidFileNameChars())).Trim();
            var file = Path.Combine(folder, (name.Length > 0 ? name : "Link") + ".url");
            File.WriteAllText(file, $"[InternetShortcut]\r\nURL={item.Target}\r\n");
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (IsInScrollBar(e.OriginalSource))
        {
            // Its own menu (if the theme gives it one) opens by itself; ours has nothing to offer there
            if (FindAncestor<ScrollBar>(e.OriginalSource)?.ContextMenu == null)
                e.Handled = true;
            return;
        }
        // The item clicked; from the keyboard (Menu key, Shift+F10: no cursor position) the selected one
        // even when the focus is on the list rather than on its row. Never an older selection for a click.
        bool fromKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        var item = ItemAt(e.OriginalSource) ?? (fromKeyboard ? List.SelectedItem as ItemViewModel : null);
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        if (item == null)
        {
            FillAddMenu(menu); // blank area
            return;
        }
        if (item.IsEditing)
        {
            e.Handled = true;
            return;
        }
        if (item.IsSeparator)
        {
            menu.Items.Add(CreateMenuItem("Remove separator", () => Remove(item), "\xE74D"));
            return;
        }

        // Several items selected (Ctrl/Shift+click) and the click was on one of them: commands for all
        var selection = SelectedItems();
        if (selection.Count > 1 && selection.Contains(item))
        {
            var launchable = selection.Where(i => i.Model.IsLaunchable).Select(i => i.Model).ToList();
            var openSelected = CreateMenuItem($"Open {launchable.Count} selected", () => LaunchMany(launchable, "shortcuts"), "\xE8A7", "Enter");
            openSelected.FontWeight = FontWeights.SemiBold;
            openSelected.IsEnabled = launchable.Count > 0;
            menu.Items.Add(openSelected);
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem($"Remove {selection.Count} items", () => RemoveMany(selection), "\xE74D", "Del"));
            return;
        }

        AddOpenCommands(menu, item);
        if (item.Model.IsLive)
        {
            // An entry of a live folder or a search suggestion is not saved: it can only be copied into the list
            if (item.Model.Id != RunSuggestions.WebSearchId)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Add to pLaunch", () => AddLiveCopy(item.Model), "\xE710"));
            }
            return;
        }
        // Inserted right after this item (it is the selected one)
        AddEditCommands(menu, item, () => StartRename(item), NewFolder, NewSeparator);
    }

    /// <summary>
    /// The second part of a saved item's menu: editing it and adding next to it (also used by the side
    /// menus, which rename in a dialog and add into their own folder).
    /// </summary>
    void AddEditCommands(ContextMenu menu, ItemViewModel item, Action rename, Action newFolder, Action newSeparator)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Rename", rename, "\xE8AC", "F2"));
        menu.Items.Add(CreateMenuItem("Remove", () => Remove(item), "\xE74D", "Del"));
        menu.Items.Add(CreateMenuItem("Properties\x2026", () => ShowProperties(item), "\xE946", "Alt+Enter"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("New folder here", newFolder, "\xE8F4"));
        menu.Items.Add(CreateMenuItem("Separator here", newSeparator, "\xE76F"));
    }

    /// <summary>
    /// The first part of an item's menu: the ways to open it. Also used by the side menus;
    /// <paramref name="level"/> says where the item is shown (0 = the popup's list, n = the n-th menu).
    /// </summary>
    void AddOpenCommands(ContextMenu menu, ItemViewModel item, int level = 0)
    {
        var open = item.Model.Kind == ItemKind.Text
            ? CreateMenuItem(item.Model.PasteText ? "Paste" : "Copy", () => Open(item), item.Model.PasteText ? "\xE77F" : "\xE8C8")
            : item.IsNavigable
                ? CreateMenuItem("Open", () => OpenFolder(item, level), "\xE838")
                : CreateMenuItem("Open", () => Open(item), "\xE8A7");
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        if (item.IsGroup && (item.Model.Children ?? []).Count(c => c.IsLaunchable) is var count and > 0)
            menu.Items.Add(CreateMenuItem($"Open all ({count})", () => OpenAll(item.Model), "\xE8A7"));
        // A live folder opens inside pLaunch on click: Explorer is one step away
        if (item.Model.Kind == ItemKind.Folder && item.Model.ShowContents)
            menu.Items.Add(CreateMenuItem("Open in File Explorer", () => Launch(item), "\xEC50"));
        // "Open" brings the running program to the front: a second window is one step away
        if (item.Model.SwitchToRunning && item.IsRunning)
            menu.Items.Add(CreateMenuItem("Open a new window", () => Launch(item, newWindow: true), "\xE8A7"));
        if (item.Model.Kind == ItemKind.Text && item.Model.PasteText)
            menu.Items.Add(CreateMenuItem("Copy only", () => CopySnippet(item), "\xE8C8"));
        if (Launcher.CanRunAsAdmin(item.Model))
            menu.Items.Add(CreateMenuItem("Run as administrator", () => Launch(item, asAdmin: true), "\xEA18", "Ctrl+Shift+Enter"));
        if (Launcher.HasLocation(item.Model))
            menu.Items.Add(CreateMenuItem("Open file location", () => OpenLocation(item), "\xE838"));
    }

    void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Top };
        FillAddMenu(menu);
        menu.IsOpen = true;
    }

    /// <summary>The Add commands: the footer button, and a right click on a blank part of the list.</summary>
    void FillAddMenu(ContextMenu menu)
    {
        menu.Items.Add(CreateMenuItem("Files\x2026", AddFiles, "\xE8E5"));
        menu.Items.Add(CreateMenuItem("Folder\x2026", AddFolder, "\xE8B7"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Command\x2026", () => NewByProperties(ItemKind.Command), "\xE756"));
        menu.Items.Add(CreateMenuItem("Text snippet\x2026", () => NewByProperties(ItemKind.Text), "\xE77F"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("New sub-folder", NewFolder, "\xE8F4"));
        menu.Items.Add(CreateMenuItem("Separator", NewSeparator, "\xE76F"));
        menu.Items.Add(new Separator());
        var paste = CreateMenuItem("Paste", PasteFromClipboard, "\xE77F", "Ctrl+V");
        paste.IsEnabled = ClipboardHasItems();
        menu.Items.Add(paste);
    }

    void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = PlacementMode.Top };

        // The quick ones stay here; everything else is in the Settings window
        var view = CreateSubmenu("View", "\xE8FD");
        foreach (var (mode, label) in ViewChoices)
            view.Items.Add(CreateChoice(label, _settings.View == mode, () => SetView(mode)));
        menu.Items.Add(view);
        var sort = CreateSubmenu("Sort", "\xE8CB");
        foreach (var (mode, label) in SortChoices)
            sort.Items.Add(CreateChoice(label, _settings.Sort == mode, () => SetSort(mode)));
        menu.Items.Add(sort);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Settings\x2026", OpenSettings, "\xE713"));
        menu.Items.Add(CreateListsMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit", Close, "\xE711"));
        menu.IsOpen = true;
    }

    internal static readonly (ViewMode Mode, string Label)[] ViewChoices =
        [(ViewMode.List, "List"), (ViewMode.Grid, "Tiles"), (ViewMode.Icons, "Icons only")];
    internal static readonly (ItemSize Size, string Label)[] SizeChoices =
        [(ItemSize.Small, "Small"), (ItemSize.Medium, "Medium"), (ItemSize.Large, "Large")];
    internal static readonly (SortMode Mode, string Label)[] SortChoices =
        [(SortMode.Custom, "Custom (drag to arrange)"), (SortMode.Alphabetical, "Alphabetical"), (SortMode.MostUsed, "Most used")];
    internal static readonly (ThemeChoice Theme, string Label)[] ThemeChoices =
        [(ThemeChoice.System, "System"), (ThemeChoice.Light, "Light"), (ThemeChoice.Dark, "Dark")];

    internal LauncherSettings Settings => _settings;
    internal ListProfile Profile => _profile;
    internal bool AcrylicAvailable => _acrylic;

    internal void SetView(ViewMode mode) { _settings.View = mode; SettingsChanged(); }
    internal void SetSize(ItemSize size) { _settings.Size = size; SettingsChanged(); }
    internal void SetSort(SortMode mode) { _settings.Sort = mode; SettingsChanged(); }
    internal void SetTheme(ThemeChoice theme) { _settings.Theme = theme; AppearanceChanged(); }
    internal void SetBackground(string? color) { _settings.Background = color; AppearanceChanged(); }
    internal void SetTranslucent(bool translucent) { _settings.Translucent = translucent; AppearanceChanged(); }

    void OpenSettings()
    {
        CommitRename();
        var window = new Views.SettingsWindow(this) { Owner = this };
        ShowModal(() => window.ShowDialog());
        Activate();
    }

    internal void SetWebIcons(bool enabled)
    {
        _settings.WebIcons = enabled;
        IconProvider.WebIconsEnabled = enabled;
        Save();
        // Web links show the other kind of icon now
        foreach (var vm in _viewModels.Values.Where(v => v.Model.Kind == ItemKind.Url && string.IsNullOrWhiteSpace(v.Model.IconPath)))
        {
            vm.Icon = null;
            vm.IconPixels = 0;
        }
        QueueIconLoad();
    }

    internal void SetWebSearch(WebSearch engine)
    {
        _settings.WebSearch = engine;
        Save();
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

    bool IsAcceptable(IDataObject data)
    {
        try
        {
            // An item dragged from another list counts only for what it carries (file, link, app)
            return IsOwnDrag(data) || DropReader.CanRead(data);
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>Our own item being moved (the drag started in this window), not one from another list.</summary>
    bool IsOwnDrag(IDataObject data) => _draggingId != null && data.GetDataPresent(InternalDragFormat);

    DragDropEffects EffectFor(DragEventArgs e)
    {
        // Our own drags offer Link (and Copy for web links) so that Explorer can never move a file:
        // inside pLaunch any allowed effect means "move the item"
        if (IsOwnDrag(e.Data))
            return e.AllowedEffects.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move
                : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
                : e.AllowedEffects & DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Link))
            return DragDropEffects.Link; // a shortcut to the file, never a copy
        return e.AllowedEffects & DragDropEffects.Copy;
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragAcceptable = !InLiveFolder && IsAcceptable(e.Data); // a live folder shows the disk: nothing is dropped into it
        _dragOffersFiles = OffersFiles(e.Data);
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
            SetSpringTarget(into.IsGroup ? into : null); // a program does not open like a folder
        }
        else
        {
            ShowInsertMarker(index);
            SetSpringTarget(null);
        }
    }

    bool _dragOffersFiles; // what is dragged over the list are documents from elsewhere: a program can open them

    bool OffersFiles(IDataObject data) => FilesToOpen(data) != null;

    /// <summary>
    /// The dragged files a program of the list could open; null when there are none, or when programs or
    /// shortcuts are among them: those are being added to the list, wherever they are dropped.
    /// </summary>
    string[]? FilesToOpen(IDataObject data)
    {
        if (IsOwnDrag(data) || !data.GetDataPresent(DataFormats.FileDrop))
            return null;
        try
        {
            return data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && Launcher.AreDocuments(files) ? files : null;
        }
        catch (COMException)
        {
            return null; // the source went away
        }
    }

    /// <summary>
    /// Files dropped on a program (the middle of it, like into a sub-folder) are opened with it, as on the
    /// old Quick Launch; the list then closes like after a launch. True when that is what the drop was.
    /// </summary>
    bool DropOpensWith(ItemViewModel? target, IDataObject data)
    {
        if (target == null || target.IsGroup)
            return false;
        if (FilesToOpen(data) is not { } files)
            return true; // on a program, with nothing it could open: nothing happens
        Cloak(true);
        try
        {
            if (Launcher.OpenWith(target.Model, files))
            {
                CountLaunches([target.Model]);
                HidePopup();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError($"Cannot open \"{target.Name}\".", ex.Message);
        }
        finally
        {
            Cloak(false);
        }
        return true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        ClearDropMarkers();
        SetSpringTarget(null);
        var (index, into) = HitTest(e.GetPosition(List));

        // Search results are not a level: a drop there only goes into a sub-folder, or at the end
        if (IsSearching)
            index = -1;
        // A saved item of this list moves; a live folder entry (not saved) is added like any file
        if (IsOwnDrag(e.Data) && e.Data.GetData(InternalDragFormat) is string id && ItemTree.Find(_root, id) is { } item)
        {
            if (into is { IsGroup: true })
                MoveInto(item, into.Model);
            else if (!IsSearching)
                MoveTo(item, index);
            return;
        }

        try
        {
            if (DropOpensWith(into, e.Data))
                return;
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
    (int Index, ItemViewModel? Into) HitTest(Point position) =>
        HitTest(List, _items, position, rows: _settings.View == ViewMode.List);

    /// <summary>The same for any list: the popup's, or a menu's (always rows).</summary>
    (int Index, ItemViewModel? Into) HitTest(ListBox list, IList<ItemViewModel> items, Point position, bool rows)
    {
        // Only rows on screen: in a scrolled list the others still have containers (IsVisible is true for
        // them too), and a drop must never land next to an item the user cannot see
        var boxes = new List<(ItemViewModel Item, Rect Rect)>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible)
                continue;
            var rect = new Rect(container.TranslatePoint(new Point(0, 0), list), new Size(container.ActualWidth, container.ActualHeight));
            if (rect.Bottom > 0 && rect.Top < list.ActualHeight)
                boxes.Add((items[i], rect));
        }
        // Below the visible rows (the footer included) = at the end
        if (boxes.Count == 0 || position.Y >= list.ActualHeight || position.Y >= boxes.Max(b => b.Rect.Bottom))
            return (items.Count, null);

        // Tiles have 2 px margins: count the gap around each one as part of it
        var (hit, hitRect) = boxes.FirstOrDefault(b => (rows ? b.Rect : Rect.Inflate(b.Rect, 2, 2)).Contains(position));
        if (hit == null)
        {
            // In a gap, e.g. the empty end of an unfinished row of tiles: the closest item on that row
            // decides (right of the last tile = after it, so still in the same section)
            var sameRow = boxes.Where(b => position.Y >= b.Rect.Top - 2 && position.Y <= b.Rect.Bottom + 2).ToList();
            var candidates = sameRow.Count > 0 ? sameRow : boxes;
            (hit, hitRect) = candidates.MinBy(b => DistanceTo(b.Rect, position));
        }

        int index = items.IndexOf(hit);
        bool draggingThis = hit.Model.Id == _draggingId;
        // Into a sub-folder, or (files from elsewhere) onto a program that opens them
        if (!draggingThis && (hit.IsGroup || _dragOffersFiles && Launcher.OpensFiles(hit.Model)))
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

    static double DistanceTo(Rect rect, Point p)
    {
        double dx = Math.Max(Math.Max(rect.Left - p.X, 0), p.X - rect.Right);
        double dy = Math.Max(Math.Max(rect.Top - p.Y, 0), p.Y - rect.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The line where a drop goes, in the popup's list or in a menu's (<paramref name="items"/>).</summary>
    void ShowInsertMarker(int index, IList<ItemViewModel>? items = null)
    {
        items ??= _items;
        // In alphabetical order the position is not kept: no line would be honest
        if (_settings.Sort == SortMode.Alphabetical)
        {
            ClearDropMarkers();
            return;
        }
        for (int i = 0; i < items.Count; i++)
        {
            items[i].DropMarker = i == index ? DropMarker.Before
                : index == items.Count && i == items.Count - 1 ? DropMarker.After
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
        foreach (var item in _menus.SelectMany(m => m.Items))
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
        {
            if (MenuMode)
                OpenMenu(group, 0); // the drag goes on into the menu
            else
                OpenGroup(group.Model);
        }
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
            _dragAcceptable = ParentSlot() != null && IsAcceptable(e.Data);
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
        if (IsOwnDrag(e.Data) && e.Data.GetData(InternalDragFormat) is string id && ItemTree.Find(_root, id) is { } item)
        {
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

    void ShowError(string message, string detail)
    {
        Cloak(false); // an error while launching: the popup it belongs to is shown again
        ShowModal(() => MessageBox.Show(this, $"{message}\n\n{detail}", "pLaunch", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    internal bool SafeAutostart()
    {
        try { return Autostart.IsEnabled(_profile); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    internal void SetAutostart(bool enabled)
    {
        try { Autostart.SetEnabled(_profile, enabled); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            ShowError("Cannot change the startup setting.", ex.Message);
        }
    }

    /// <param name="hide">False from the Settings window: minimizing the popup would take its dialog along.</param>
    internal void OpenDataFolder(bool hide = true)
    {
        var folder = AppConfig.DataDirectoryPath;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        if (hide)
            HidePopup();
    }

    void FocusSelected()
    {
        if (List.SelectedItem != null && List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListBoxItem container)
            container.Focus();
        else
            List.Focus();
    }

    static ItemViewModel? ItemAt(object source) => FindAncestor<ListBoxItem>(source)?.DataContext as ItemViewModel;

    static bool IsInScrollBar(object source) => FindAncestor<ScrollBar>(source) != null;

    /// <summary>The element itself or its closest ancestor of type T (visual tree, logical for text runs).</summary>
    static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node != null && node is not T)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
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
