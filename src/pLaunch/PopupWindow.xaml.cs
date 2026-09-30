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
    const double PopupWidth = 320; // DIPs
    const double GapDip = 12;      // distance from the taskbar, like the Start menu
    const string InternalDragFormat = "pLaunch.ItemId";
    static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    readonly ItemStore _store;
    readonly ObservableCollection<ItemViewModel> _items = [];
    readonly DispatcherTimer _dragLeaveTimer;
    readonly DispatcherTimer _hoverWatch;
    IntPtr _hwnd;
    int _suppressHide;
    DateTime _lastAutoHide;
    NativeMethods.POINT _anchor;
    int _outsideTicks;
    Point _pressPoint;
    ItemViewModel? _pressed;
    bool _dragAcceptable;
    bool _iconsQueued;

    public PopupWindow(ItemStore store)
    {
        InitializeComponent();
        _store = store;
        foreach (var item in store.Load())
            _items.Add(new ItemViewModel(item));
        List.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _dragLeaveTimer.Tick += (_, _) => { _dragLeaveTimer.Stop(); ClearDropMarkers(); };
        _hoverWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hoverWatch.Tick += HoverWatch_Tick;

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
    }

    bool IsOpen => IsVisible && WindowState == WindowState.Normal;

    /// <summary>False only in UI test harnesses, so an opened popup never takes the focus from the user.</summary>
    internal bool ActivateOnOpen { get; set; } = true;

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
        JumpListBuilder.Update(_items.Select(i => i.Model));
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
                break;
        }
    }

    void OnPopupOpened()
    {
        NativeMethods.GetCursorPos(out _anchor);
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
        var monitor = NativeMethods.MonitorFromPoint(_anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return;
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var (area, edge) = PopupPlacement.AvailableArea(
            info.rcMonitor.ToPixelRect(), info.rcWork.ToPixelRect(), TaskbarRect(), (int)Math.Round(48 * scale));
        int gap = (int)Math.Round(GapDip * scale);
        double maxHeight = Math.Max(120, (area.Height - 2 * gap) / scale);
        int width = (int)Math.Round(PopupWidth * scale);
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
        Root.Measure(new Size(PopupWidth, double.PositiveInfinity));
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
        ApplyTheme();
        if (NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_TRANSIENTWINDOW) != 0)
            Root.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush"); // no acrylic before 22H2
    }

    /// <summary>The acrylic backdrop follows the window's dark-mode flag, not the WPF theme.</summary>
    void ApplyTheme() =>
        NativeMethods.SetDwmInt(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, IsDarkTheme() ? 1 : 0);

    static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(ApplyTheme);
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
        IconProvider.ClearCache();
        foreach (var item in _items)
            item.IconLoaded = false;
        QueueIconLoad();
    }

    // ---------------------------------------------------------------- items

    public void AddFromArguments(IEnumerable<string> arguments)
    {
        AddItems(arguments.Select(ItemFactory.FromText));
        ShowPopup();
    }

    void AddItems(IEnumerable<LaunchItem?> items, int index = -1)
    {
        if (index < 0 || index > _items.Count)
            index = _items.Count;
        ItemViewModel? last = null;
        foreach (var item in items)
        {
            if (item == null || _items.Any(i => i.Model.IsSameTarget(item)))
                continue;
            last = new ItemViewModel(item);
            _items.Insert(index++, last);
        }
        if (last == null)
            return;
        Save();
        QueueIconLoad();
        List.SelectedItem = last;
        List.ScrollIntoView(last);
        if (IsOpen)
            Place();
    }

    void Remove(ItemViewModel item)
    {
        int index = _items.IndexOf(item);
        if (index < 0)
            return;
        _items.RemoveAt(index);
        Save();
        if (_items.Count > 0)
        {
            List.SelectedIndex = Math.Min(index, _items.Count - 1);
            FocusSelected();
        }
        if (IsOpen)
            Place();
    }

    void Save()
    {
        var models = _items.Select(i => i.Model).ToList();
        try
        {
            _store.Save(models);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Cannot save the list.", ex.Message);
        }
        JumpListBuilder.Update(models);
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
        var snapshot = _items.ToList();
        var missing = await Task.Run(() => snapshot.Select(i => Launcher.IsMissing(i.Model)).ToArray());
        for (int i = 0; i < snapshot.Count; i++)
        {
            var item = snapshot[i];
            if (item.IsMissing && !missing[i] && !item.HasIcon)
                item.IconLoaded = false; // it came back (e.g. a drive was plugged in): retry the icon
            item.IsMissing = missing[i];
        }
        QueueIconLoad();
    }

    void QueueIconLoad()
    {
        if (_iconsQueued)
            return;
        _iconsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, LoadNextIcon);
    }

    // One icon per dispatcher pass keeps the popup responsive while many icons load
    void LoadNextIcon()
    {
        var next = _items.FirstOrDefault(i => !i.IconLoaded);
        if (next == null)
        {
            _iconsQueued = false;
            return;
        }
        next.IconLoaded = true;
        int pixels = (int)Math.Round(24 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        next.Icon = IconProvider.Get(next.Model, pixels);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, LoadNextIcon);
    }

    void UpdateEmptyHint() => EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- rename

    void StartRename(ItemViewModel item)
    {
        CommitRename();
        item.EditName = item.Name;
        item.IsEditing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container && FindChild<TextBox>(container) is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        });
    }

    void CommitRename(bool cancel = false)
    {
        foreach (var item in _items.Where(i => i.IsEditing).ToList())
        {
            item.IsEditing = false;
            var name = item.EditName.Trim();
            if (!cancel && name.Length > 0 && name != item.Name)
            {
                item.Name = name;
                Save();
            }
        }
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
        var selected = List.SelectedItem as ItemViewModel;
        switch (e.Key)
        {
            case Key.Escape:
                HidePopup();
                break;
            case Key.Enter when selected != null:
                // Ctrl+Shift+Enter runs as administrator, like in the Start menu
                Launch(selected, asAdmin: Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                                          && Launcher.CanRunAsAdmin(selected.Model));
                break;
            case Key.F2 when selected != null:
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
        DragDrop.DoDragDrop(List, new DataObject(InternalDragFormat, item.Model.Id), DragDropEffects.Move);
        ClearDropMarkers();
    }

    // A single click launches, like the old Quick Launch
    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = _pressed;
        _pressed = null;
        if (item != null && ItemAt(e.OriginalSource) == item)
        {
            e.Handled = true;
            Launch(item);
        }
    }

    void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is { } item)
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
        var open = CreateMenuItem("Open", () => Launch(item), "\xE8A7");
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        if (Launcher.CanRunAsAdmin(item.Model))
            menu.Items.Add(CreateMenuItem("Run as administrator", () => Launch(item, asAdmin: true), "\xEA18", "Ctrl+Shift+Enter"));
        if (Launcher.HasLocation(item.Model))
            menu.Items.Add(CreateMenuItem("Open file location", () => OpenLocation(item), "\xE838"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Rename", () => StartRename(item), "\xE8AC", "F2"));
        menu.Items.Add(CreateMenuItem("Remove", () => Remove(item), "\xE74D", "Del"));
    }

    void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Top };
        menu.Items.Add(CreateMenuItem("Files\x2026", AddFiles, "\xE8E5"));
        menu.Items.Add(CreateMenuItem("Folder\x2026", AddFolder, "\xE8B7"));
        var paste = CreateMenuItem("Paste", PasteFromClipboard, "\xE77F", "Ctrl+V");
        paste.IsEnabled = ClipboardHasItems();
        menu.Items.Add(paste);
        menu.IsOpen = true;
    }

    void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = PlacementMode.Top };
        var autostart = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = SafeAutostart() };
        autostart.Click += (_, _) => SetAutostart(autostart.IsChecked);
        menu.Items.Add(autostart);
        menu.Items.Add(CreateMenuItem("Open data folder", OpenDataFolder, "\xE838"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit", Close, "\xE711"));
        menu.IsOpen = true;
    }

    static MenuItem CreateMenuItem(string header, Action action, string? glyph = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (glyph != null)
        {
            item.Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
                FontSize = 14,
            };
        }
        item.Click += (_, _) => action();
        return item;
    }

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

    void OnDragEnter(object sender, DragEventArgs e)
    {
        try
        {
            _dragAcceptable = e.Data.GetDataPresent(InternalDragFormat) || DropReader.CanRead(e.Data);
        }
        catch (COMException)
        {
            _dragAcceptable = false;
        }
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        e.Handled = true;
        if (!_dragAcceptable)
        {
            e.Effects = DragDropEffects.None;
            ClearDropMarkers();
            return;
        }
        if (e.Data.GetDataPresent(InternalDragFormat))
            e.Effects = DragDropEffects.Move;
        else if (e.AllowedEffects.HasFlag(DragDropEffects.Link))
            e.Effects = DragDropEffects.Link; // a shortcut to the file, never a copy
        else
            e.Effects = e.AllowedEffects & DragDropEffects.Copy;
        ShowDropMarker(InsertionIndex(e.GetPosition(List)));
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        ClearDropMarkers();
        int index = InsertionIndex(e.GetPosition(List));

        if (e.Data.GetData(InternalDragFormat) is string id)
        {
            var item = _items.FirstOrDefault(i => i.Model.Id == id);
            int from = item == null ? -1 : _items.IndexOf(item);
            if (from < 0)
                return;
            if (index > from)
                index--;
            if (index != from)
            {
                _items.Move(from, index);
                Save();
            }
            List.SelectedItem = item;
            return;
        }

        try
        {
            AddItems(DropReader.Read(e.Data), index);
        }
        catch (COMException)
        {
            // The source went away mid-drop
        }
        Activate();
    }

    int InsertionIndex(Point position)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible)
                continue;
            double top = container.TranslatePoint(new Point(0, 0), List).Y;
            if (position.Y < top + container.ActualHeight / 2)
                return i;
        }
        return _items.Count;
    }

    void ShowDropMarker(int index)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].DropMarker = i == index ? DropMarker.Before
                : index == _items.Count && i == _items.Count - 1 ? DropMarker.After
                : DropMarker.None;
        }
    }

    void ClearDropMarkers()
    {
        foreach (var item in _items)
            item.DropMarker = DropMarker.None;
    }

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
        if (List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListBoxItem container)
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
