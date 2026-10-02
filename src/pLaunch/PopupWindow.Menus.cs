using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using pLaunch.Models;
using pLaunch.Native;
using pLaunch.Services;
using pLaunch.ViewModels;
using pLaunch.Views;

namespace pLaunch;

// Sub-folders opened as menus beside the list (the default), sized to their own content, their own
// sub-folders cascading on hover like the menus of the old Quick Launch
public partial class PopupWindow
{
    // Resting this long on a row is enough: sub-folders open (and other rows close them) almost at once
    static readonly TimeSpan MenuHoverDelay = TimeSpan.FromMilliseconds(50);
    // While the mouse heads for an open menu, the rows it crosses wait: at most this many delays
    const int MaxHoverPostpones = 8;
    const double MenuGapDip = 2;
    const double MenuListTopDip = 6; // the menu list's top margin: its first row lines up with the folder's row

    /// <summary>The open menus, the one beside the popup first.</summary>
    readonly List<FolderMenu> _menus = [];
    DispatcherTimer? _menuHoverTimer;
    // The row under the mouse and where it is: 0 = the popup's list, n = the n-th menu
    (ItemViewModel? Item, int Level) _menuHover;
    TimeSpan _menuHoverWait; // how long the current row has to be rested on (a drag waits longer)
    NativeMethods.POINT _hoverFrom; // where the mouse was when it reached that row (or at the last delay)
    NativeMethods.POINT? _pointerAtOpen; // where the pointer was when the popup opened, until it moves
    int _hoverPostpones;
    (ItemViewModel Item, FolderMenu Menu)? _menuPressed;
    Point _menuPressPoint;
    bool _menuDragAcceptable;

    bool MenuMode => _settings.SubFolders == SubFolderMode.Menu;

    internal SubFolderMode SubFolders => _settings.SubFolders;

    internal void SetSubFolders(SubFolderMode mode)
    {
        _settings.SubFolders = mode;
        CloseMenus();
        _path.Clear();
        Save();
        Refresh();
    }

    // Levels: 0 = the popup's list, n = the n-th menu. A level is always passed along, never looked up:
    // rows are shared by id, so one can be in the search results and in a menu at the same time.

    ObservableCollection<ItemViewModel> ItemsAt(int level) => level == 0 ? _items : _menus[level - 1].Items;

    bool IsShownAt(ItemViewModel item, int level) => level >= 0 && level <= _menus.Count && ItemsAt(level).Contains(item);

    ListBox ListAt(int level) => level == 0 ? List : _menus[level - 1].List;

    ListBoxItem? RowAt(int level, ItemViewModel item) => ListAt(level).ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;

    bool IsMenuOpen(ItemViewModel folder, int level) => _menus.Count > level && _menus[level].Opener == folder;

    /// <summary>
    /// A click (or Enter) on a sub-folder or live folder: its menu opens beside its row. Resting the
    /// mouse on it has usually opened it already: a click keeps it open. From the keyboard the menu's
    /// first item is selected, ready for the arrows.
    /// </summary>
    void ToggleMenu(ItemViewModel folder, int level)
    {
        if (!IsShownAt(folder, level))
            return;
        bool keyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        if (IsMenuOpen(folder, level))
        {
            if (keyboard)
            {
                _menus[level].KeyboardActive = true; // opened by the mouse: now the keys go into it
                MoveMenuSelection(_menus[level], +1);
            }
            return;
        }
        OpenMenu(folder, level, selectFirst: keyboard);
    }

    /// <summary>"Open" from a right click: the folder's menu opens, or stays open (inside the list: the folder opens there).</summary>
    void OpenFolder(ItemViewModel folder, int level)
    {
        if (!MenuMode)
            OpenGroup(folder.Model);
        else if (!IsMenuOpen(folder, level) && IsShownAt(folder, level))
            OpenMenu(folder, level);
    }

    void OpenMenu(ItemViewModel folder, int level, bool selectFirst = false)
    {
        CloseMenus(level);
        CommitRename();
        if (RowAt(level, folder) is not { } row)
            return;
        var model = folder.Model;
        if (model.Kind == ItemKind.Folder)
            _liveCache.Remove(model.Target); // read the disk again: the content may have changed

        // Always rows: a menu, whatever the popup's view
        var metrics = ViewMetrics.For(ViewMode.List, _settings.Size);
        var menu = new FolderMenu(model, folder) { Owner = this, Width = metrics.Width };
        menu.Resources["IconSize"] = metrics.IconSize;
        menu.Resources["GlyphSize"] = Math.Round(metrics.IconSize * 0.85);
        menu.Resources["RowHeight"] = metrics.RowHeight;
        menu.List.ItemsPanel = (ItemsPanelTemplate)Resources["StackPanelTemplate"];
        menu.UseRowStyle((Style)Resources["ListContainer"]); // the popup's rows, never focusable
        menu.List.ItemTemplateSelector = new ItemTemplateChooser(Resources, ViewMode.List);
        HookMenu(menu);
        _menus.Add(menu);
        FillMenu(menu);

        var hwnd = menu.EnsureHandle();
        NativeMethods.SetDwmInt(hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        NativeMethods.SetDwmInt(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        if (_acrylic)
            NativeMethods.SetDwmInt(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_TRANSIENTWINDOW);
        NativeMethods.SetDwmInt(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, Appearance.IsDark(_settings, IsSystemDark()) ? 1 : 0);
        PlaceMenu(menu, row, level);
        menu.Show();
        PlaceMenu(menu, row, level); // Show applies the window's own size first

        // The folder it came from stays highlighted while its menu is open
        menu.SelectionBefore = ListAt(level).SelectedItem;
        ListAt(level).SelectedItem = folder;
        if (selectFirst)
        {
            menu.KeyboardActive = true;
            MoveMenuSelection(menu, +1);
        }
    }

    /// <summary>Closes the menus from the <paramref name="from"/>-th one on (0 = all of them).</summary>
    void CloseMenus(int from = 0)
    {
        for (int i = _menus.Count - 1; i >= from && i >= 0; i--)
        {
            var menu = _menus[i];
            _menus.RemoveAt(i);
            foreach (var item in menu.Items)
                item.DropMarker = DropMarker.None;
            // The folder was highlighted for its open menu: with the menu gone, so is the highlight. The
            // list's selection goes back to what it was (the row the keyboard was on, the folder itself
            // included); a menu opened with the keys leaves the keyboard on its folder.
            var parent = ListAt(i);
            if (!menu.KeyboardActive && parent.SelectedItem is ItemViewModel selected && selected.Model == menu.Folder)
                parent.SelectedItem = menu.SelectionBefore != null && parent.Items.Contains(menu.SelectionBefore) ? menu.SelectionBefore : null;
            menu.Close();
        }
        if (_menuHover.Level > _menus.Count)
            _menuHover = (null, 0);
    }

    /// <summary>The menu's rows from the model: the sub-folder's content in the chosen order, or the disk's.</summary>
    void FillMenu(FolderMenu menu)
    {
        var folder = menu.Folder;
        var entries = folder.Kind == ItemKind.Group ? ItemTree.DisplayOrder(folder.Children ?? [], _settings.Sort) : LiveEntries(folder);
        var target = entries.Select(ViewModelFor).ToList();
        foreach (var vm in target.Where(v => v.IsGroup))
            vm.RefreshChildInfo();
        SyncItems(menu.Items, target);
        menu.EmptyText.Text = folder.Kind == ItemKind.Folder ? LiveFolderHint(folder) : "This folder is empty.\nDrag shortcuts here.";
        menu.EmptyText.Visibility = menu.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        menu.Root.Background = Root.Background;
        QueueIconLoad(menu.Items);
        // Rows that came in (a drop, a live folder read) get their running line too; menus opened one after
        // another while the pointer moves share a recent look at the windows
        MarkRunning(menu.Items.ToList(), reuse: TimeSpan.FromMilliseconds(500));
    }

    /// <summary>After any change: every menu shows its folder again, or closes when the folder is gone.</summary>
    void RefreshMenus()
    {
        if (_menus.Count == 0)
            return;
        UpdateLayout();
        for (int i = 0; i < _menus.Count; i++)
        {
            var menu = _menus[i];
            // Gone: removed, or no longer shown where it was opened (a search changed, a folder closed)
            bool exists = menu.Folder.Kind == ItemKind.Folder || ItemTree.Find(_root, menu.Folder.Id) == menu.Folder;
            if (!exists || !IsShownAt(menu.Opener, i) || RowAt(i, menu.Opener) is not { } row)
            {
                CloseMenus(i);
                return;
            }
            FillMenu(menu);
            menu.UpdateLayout();
            PlaceMenu(menu, row, i);
        }
    }

    /// <summary>After a scroll: the menus from the <paramref name="from"/>-th one on move with their folder's row.</summary>
    void RepositionMenus(int from)
    {
        for (int i = Math.Max(from, 0); i < _menus.Count; i++)
        {
            if (RowAt(i, _menus[i].Opener) is { } row)
                PlaceMenu(_menus[i], row, i);
        }
    }

    /// <summary>
    /// Beside its parent (the popup or the menu before it), on the side with room, its first row level
    /// with the folder's row; as tall as its content, at most the screen's height (it scrolls then).
    /// </summary>
    void PlaceMenu(FolderMenu menu, ListBoxItem row, int level)
    {
        var parentHwnd = level == 0 ? _hwnd : _menus[level - 1].Handle;
        if (menu.Handle == IntPtr.Zero || !NativeMethods.GetWindowRect(parentHwnd, out var parent))
            return;
        var rowTop = row.PointToScreen(new Point(0, 0)); // device pixels
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = (int)rowTop.X, Y = (int)rowTop.Y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return;
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var work = info.rcWork;
        int gap = (int)Math.Round(MenuGapDip * scale);

        menu.Root.Measure(new Size(menu.Width, double.PositiveInfinity));
        double maxHeight = Math.Max(80, (work.Bottom - work.Top - 2 * gap) / scale);
        double heightDip = Math.Min(menu.Root.DesiredSize.Height, maxHeight);
        int width = (int)Math.Round(menu.Width * scale);
        int height = (int)Math.Round(heightDip * scale);

        int x = parent.Right + gap;
        if (x + width > work.Right)
            x = parent.Left - gap - width; // no room on the right: on the left
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        int y = (int)Math.Round(rowTop.Y - MenuListTopDip * scale);
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - gap - height));

        // WPF sizes the window itself (so it lays out and redraws the new area); Win32 only moves it,
        // in device pixels (the popup's coordinates)
        bool grows = menu.IsVisible && heightDip > menu.ActualHeight + 0.5;
        menu.Height = heightDip;
        const uint NoSize = 0x0001;
        NativeMethods.SetWindowPos(menu.Handle, IntPtr.Zero, x, y, 0, 0, NoSize | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        if (grows)
            menu.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RedrawAll(menu)); // after layout
    }

    /// <summary>
    /// A shown menu that grew (a live folder read, an item dropped in) keeps its first frame in the new
    /// area: only what changes later gets drawn there. A change of the whole content's opacity, too
    /// small to see, makes all of it drawn again.
    /// </summary>
    void RedrawAll(FolderMenu menu)
    {
        if (!_menus.Contains(menu))
            return;
        menu.Root.Opacity = 0.99;
        menu.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => menu.Root.Opacity = 1);
    }

    /// <summary>The same in-place update as the popup's list: moves, inserts, removals.</summary>
    static void SyncItems(ObservableCollection<ItemViewModel> items, List<ItemViewModel> target)
    {
        for (int i = 0; i < target.Count; i++)
        {
            var vm = target[i];
            if (i < items.Count && items[i] == vm)
                continue;
            // Positions before i already match, so a view model still present is further down
            int at = items.IndexOf(vm);
            if (at > i)
                items.Move(at, i);
            else
                items.Insert(i, vm);
        }
        while (items.Count > target.Count)
            items.RemoveAt(items.Count - 1);
    }

    bool IsCursorOverMenu() =>
        NativeMethods.GetCursorPos(out var p)
        && _menus.Any(m => NativeMethods.GetWindowRect(m.Handle, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom);

    // ---------------------------------------------------------------- mouse

    void HookMenu(FolderMenu menu)
    {
        menu.List.PreviewMouseLeftButtonDown += (_, e) => MenuMouseDown(menu, e);
        HookMiddleClick(menu.List);
        menu.List.PreviewMouseMove += (_, e) => MenuMouseMove(menu, e);
        menu.List.PreviewMouseLeftButtonUp += (_, e) => MenuMouseUp(menu, e);
        menu.List.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemAt(e.OriginalSource) is { IsSeparator: false } item)
                menu.List.SelectedItem = item;
        };
        menu.List.ContextMenuOpening += (_, e) => MenuContextMenuOpening(menu, e);
        menu.List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0)
                RepositionMenus(_menus.IndexOf(menu) + 1);
        }));
        menu.DragEnter += (_, e) => MenuDragOver(menu, e);
        menu.DragOver += (_, e) => MenuDragOver(menu, e);
        menu.DragLeave += (_, _) => _dragLeaveTimer.Start();
        menu.Drop += (_, e) => MenuDrop(menu, e);
    }

    /// <summary>
    /// The row under the mouse, in the popup's list or in a menu: resting on a sub-folder opens its menu,
    /// resting on anything else closes the menus after that list.
    /// </summary>
    void ScheduleMenuHover(ItemViewModel? item, int level, TimeSpan? delay = null)
    {
        var wait = delay ?? MenuHoverDelay;
        // The same row again: nothing new, unless the wait changed (a drag's long one, then plain pointing)
        if (!MenuMode || (item == _menuHover.Item && level == _menuHover.Level && wait == _menuHoverWait))
            return;
        // The popup opened under a pointer that has not moved yet (the shortcut places it at the pointer):
        // the row that happens to be there was not pointed at
        if (level == 0 && PointerStillSinceOpen())
            return;
        _menuHover = (item, level);
        _menuHoverWait = wait;
        NativeMethods.GetCursorPos(out _hoverFrom);
        _hoverPostpones = 0;
        if (_menuHoverTimer == null)
        {
            _menuHoverTimer = new DispatcherTimer { Interval = MenuHoverDelay };
            _menuHoverTimer.Tick += (_, _) =>
            {
                _menuHoverTimer.Stop();
                MenuHoverElapsed();
            };
        }
        _menuHoverTimer.Stop();
        _menuHoverTimer.Interval = wait;
        if (item != null)
            _menuHoverTimer.Start();
    }

    /// <summary>Called when the popup opens: rows under the still pointer do not count as pointed at.</summary>
    void RememberPointerAtOpen() => _pointerAtOpen = NativeMethods.GetCursorPos(out var p) ? p : null;

    /// <summary>
    /// True while the pointer is where it was when the popup opened. Any real movement ends it for good
    /// (the popup and every menu call this on mouse moves), even if the pointer later comes back there.
    /// </summary>
    bool PointerStillSinceOpen()
    {
        if (_pointerAtOpen is not { } still)
            return false;
        if (NativeMethods.GetCursorPos(out var now) && now.X == still.X && now.Y == still.Y)
            return true;
        _pointerAtOpen = null;
        return false;
    }

    void MenuHoverElapsed()
    {
        var (item, level) = _menuHover;
        if (item == null || !IsShownAt(item, level))
            return;
        // On the way to the open menu (diagonally, over other rows): those rows wait a little longer
        if (_menus.Count > level && !IsMenuOpen(item, level) && _hoverPostpones < MaxHoverPostpones && IsHeadingFor(_menus[level]))
        {
            _hoverPostpones++;
            _menuHoverTimer!.Start();
            return;
        }
        if (item.IsNavigable)
        {
            if (!IsMenuOpen(item, level))
                OpenMenu(item, level);
        }
        else if (!item.IsSeparator)
        {
            CloseMenus(level);
        }
    }

    /// <summary>Whether the mouse moved towards <paramref name="menu"/> since the row was reached (or since the last check).</summary>
    bool IsHeadingFor(FolderMenu menu)
    {
        if (!NativeMethods.GetCursorPos(out var now) || !NativeMethods.GetWindowRect(menu.Handle, out var rect))
            return false;
        var from = _hoverFrom;
        _hoverFrom = now;
        return PopupPlacement.IsHeadingFor((from.X, from.Y), (now.X, now.Y), rect.ToPixelRect());
    }

    void MenuMouseDown(FolderMenu menu, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource);
        _menuPressed = item == null ? null : (item, menu);
        _menuPressPoint = e.GetPosition(menu.List);
        if (item?.IsSeparator == true)
            e.Handled = true; // draggable, never selected
    }

    void MenuMouseMove(FolderMenu menu, MouseEventArgs e)
    {
        PointerStillSinceOpen(); // a move in a menu is a real move
        if (e.LeftButton != MouseButtonState.Pressed || _menuPressed is not { } pressed || pressed.Menu != menu)
        {
            ScheduleMenuHover(ItemAt(e.OriginalSource), _menus.IndexOf(menu) + 1);
            return;
        }
        var delta = e.GetPosition(menu.List) - _menuPressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _menuPressed = null;
        _draggingId = pressed.Item.Model.Id;
        try
        {
            var (data, effects) = CreateDragData(pressed.Item.Model);
            DragDrop.DoDragDrop(menu.List, data, effects);
        }
        finally
        {
            _draggingId = null;
            ClearDropMarkers();
            SetSpringTarget(null);
        }
    }

    void MenuMouseUp(FolderMenu menu, MouseButtonEventArgs e)
    {
        var pressed = _menuPressed;
        _menuPressed = null;
        if (pressed is not { } p || p.Menu != menu || ItemAt(e.OriginalSource) != p.Item || p.Item.IsSeparator)
            return;
        e.Handled = true;
        int level = _menus.IndexOf(menu) + 1;
        if (p.Item.IsNavigable)
        {
            // Resting on it may have opened it already: a click keeps it open
            if (!IsMenuOpen(p.Item, level))
                OpenMenu(p.Item, level);
        }
        else
        {
            Open(p.Item);
        }
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>
    /// Keys while a menu is open (the popup keeps the keyboard: menus never take the focus): the arrows
    /// move in the last menu, Right or Enter open a sub-folder, Left or Esc close the last menu.
    /// </summary>
    bool HandleMenuKey(KeyEventArgs e)
    {
        bool alt = e.Key == Key.System;
        var key = alt ? e.SystemKey : e.Key;
        // The deepest menu the keyboard works in; menus the mouse opened leave the keys to their list
        int target = _menus.FindLastIndex(m => m.KeyboardActive);
        if (target < 0)
        {
            if (key != Key.Escape)
                return false;
            CloseMenus(); // Esc closes what the mouse opened, before closing the popup
            return true;
        }
        // Typing in the search box: only the keys that move in the menu go to it (the others edit the text)
        if (SearchBox.IsKeyboardFocusWithin && key is not (Key.Down or Key.Up or Key.Enter or Key.Escape))
            return false;
        var menu = _menus[target];
        int level = target + 1;
        var selected = menu.List.SelectedItem as ItemViewModel;
        bool editable = selected is { IsSeparator: false } && !selected.Model.IsLive;
        // What the key does in that menu; null = not a menu key (modifiers pressed alone, Tab, letters...)
        Action? action = key switch
        {
            Key.Enter when alt => () => { if (editable) ShowProperties(selected!); }, // Alt+Enter, before plain Enter
            Key.Down when !alt => () => MoveMenuSelection(menu, +1),
            Key.Up when !alt => () => MoveMenuSelection(menu, -1),
            Key.Right when !alt && selected is { IsNavigable: true } => () => OpenMenu(selected, level, selectFirst: true),
            Key.Right when !alt => () => { }, // on a program or file: nothing (the list must not reopen the menu)
            Key.Enter when selected is { IsNavigable: true } => () => OpenMenu(selected, level, selectFirst: true),
            Key.Enter when selected is { IsSeparator: false } => () =>
                Open(selected, asAdmin: Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && Launcher.CanRunAsAdmin(selected.Model)),
            Key.Left when !alt => () => CloseMenus(target),
            Key.Escape => () => CloseMenus(target),
            Key.F2 when editable => () => RenameByPrompt(selected!),
            Key.Delete when selected != null && !selected.Model.IsLive => () => Remove(selected),
            _ => null,
        };
        if (action == null)
            return false;
        // The keyboard takes over: a hover still pending must not close what the keys open, and menus the
        // mouse opened past the keyboard's one go away
        _menuHoverTimer?.Stop();
        CloseMenus(target + 1);
        action();
        return true;
    }

    /// <summary>The next (or previous) row of a menu that is not a separator.</summary>
    static void MoveMenuSelection(FolderMenu menu, int step)
    {
        var rows = menu.Items.Where(i => !i.IsSeparator).ToList();
        if (rows.Count == 0)
            return;
        int at = menu.List.SelectedItem is ItemViewModel current ? rows.IndexOf(current) : -1;
        int next = at < 0 ? (step > 0 ? 0 : rows.Count - 1) : Math.Clamp(at + step, 0, rows.Count - 1);
        menu.List.SelectedItem = rows[next];
        menu.List.ScrollIntoView(rows[next]);
    }

    // ---------------------------------------------------------------- right click

    void MenuContextMenuOpening(FolderMenu menu, ContextMenuEventArgs e)
    {
        bool fromKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        var item = ItemAt(e.OriginalSource) ?? (fromKeyboard ? menu.List.SelectedItem as ItemViewModel : null);
        var context = menu.List.ContextMenu!;
        context.Items.Clear();
        bool live = menu.Folder.Kind == ItemKind.Folder; // the disk's content: read-only
        if (item == null || item.IsSeparator)
        {
            if (live)
            {
                e.Handled = true;
                return;
            }
            if (item != null)
            {
                context.Items.Add(CreateMenuItem("Remove separator", () => Remove(item), "\xE74D"));
                return;
            }
            context.Items.Add(CreateMenuItem("New sub-folder", () => NewInMenu(menu, null, NewGroup()), "\xE8F4"));
            context.Items.Add(CreateMenuItem("Separator", () => NewInMenu(menu, null, new LaunchItem { Kind = ItemKind.Separator }), "\xE76F"));
            var paste = CreateMenuItem("Paste", () => PasteInto(menu.Folder), "\xE77F");
            paste.IsEnabled = ClipboardHasItems();
            context.Items.Add(paste);
            return;
        }
        AddOpenCommands(context, item, _menus.IndexOf(menu) + 1);
        if (item.Model.IsLive)
        {
            context.Items.Add(new Separator());
            context.Items.Add(CreateMenuItem("Add to pLaunch", () => AddLiveCopy(item.Model, LiveCopyLevel(menu)), "\xE710"));
            return;
        }
        AddEditCommands(context, item, () => RenameByPrompt(item),
            () => NewInMenu(menu, item, NewGroup()), () => NewInMenu(menu, item, new LaunchItem { Kind = ItemKind.Separator }));
    }

    static LaunchItem NewGroup() => new() { Kind = ItemKind.Group, Name = "New folder", Children = [] };

    /// <summary>A new sub-folder or separator in a menu's folder, after <paramref name="after"/> (null = at the end).</summary>
    void NewInMenu(FolderMenu menu, ItemViewModel? after, LaunchItem item)
    {
        InsertAfter(menu.Folder.Children ??= [], after?.Model, item);
        Save();
        Refresh();
        if (item.Kind == ItemKind.Group)
            RenameByPrompt(ViewModelFor(item));
    }

    void PasteInto(LaunchItem folder)
    {
        try
        {
            if (Clipboard.GetDataObject() is { } data && DropReader.CanRead(data)
                && Insert(folder.Children ??= [], folder.Children.Count, DropReader.Read(data)).Count > 0)
            {
                Save();
                Refresh();
            }
        }
        catch (COMException)
        {
            // Clipboard locked by another app
        }
    }

    /// <summary>Renaming without a rename box in the row (menus): a small dialog.</summary>
    void RenameByPrompt(ItemViewModel item)
    {
        var dialog = new TextPromptWindow("Rename", "Name:", item.Name, text => text.Length == 0 ? "The name cannot be empty." : null) { Owner = this };
        if (ShowModal(() => dialog.ShowDialog()) != true || dialog.Value == item.Name)
            return;
        item.Name = dialog.Value;
        Save();
        Refresh();
    }

    /// <summary>"Add to pLaunch" in a live folder's menu: into the list that holds the outermost live folder.</summary>
    List<LaunchItem> LiveCopyLevel(FolderMenu menu)
    {
        var outermost = _menus.Take(_menus.IndexOf(menu) + 1).First(m => m.Folder.Kind == ItemKind.Folder).Folder;
        return ItemTree.FindContainer(_root, outermost.Id) ?? _root;
    }

    // ---------------------------------------------------------------- drag and drop

    void MenuDragOver(FolderMenu menu, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        if (e.RoutedEvent == DragEnterEvent)
        {
            _menuDragAcceptable = menu.Folder.Kind == ItemKind.Group && IsAcceptable(e.Data);
            _dragOffersFiles = OffersFiles(e.Data);
        }
        ClearDropMarkers();
        if (!_menuDragAcceptable)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = EffectFor(e);
        var (index, into) = HitTest(menu.List, menu.Items, e.GetPosition(menu.List), rows: true);
        if (into != null)
            into.DropMarker = DropMarker.Into;
        else
            ShowInsertMarker(index, menu.Items);
        // Holding the drag over a sub-folder opens it, after the same wait as in the popup's list (a drag
        // crosses folders on its way to where it drops)
        ScheduleMenuHover(into is { IsGroup: true } ? into : null, _menus.IndexOf(menu) + 1, SpringDelay);
    }

    void MenuDrop(FolderMenu menu, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        ClearDropMarkers();
        if (menu.Folder.Kind != ItemKind.Group)
            return;
        var (index, into) = HitTest(menu.List, menu.Items, e.GetPosition(menu.List), rows: true);
        var level = menu.Folder.Children ??= [];
        var anchor = index < menu.Items.Count ? menu.Items[index].Model : null;
        if (IsOwnDrag(e.Data) && e.Data.GetData(InternalDragFormat) is string id && ItemTree.Find(_root, id) is { } item)
        {
            if (into is { IsGroup: true })
                MoveInto(item, into.Model);
            else
                MoveToLevel(item, menu.Folder, anchor);
            return;
        }
        try
        {
            if (DropOpensWith(into, e.Data))
                return;
            var dropped = DropReader.Read(e.Data);
            var target = into != null ? (into.Model.Children ??= []) : level;
            int at = into != null || anchor == null ? target.Count : target.IndexOf(anchor);
            if (Insert(target, at, dropped).Count > 0)
            {
                Save();
                Refresh();
            }
        }
        catch (COMException)
        {
            // The source went away mid-drop
        }
        Activate();
    }

    /// <summary>Moves an item of any level into a menu's <paramref name="folder"/>, before <paramref name="anchor"/> (null = at the end).</summary>
    void MoveToLevel(LaunchItem item, LaunchItem folder, LaunchItem? anchor)
    {
        if (MoveBefore(item, folder, folder.Children ??= [], anchor))
            Refresh();
    }
}
