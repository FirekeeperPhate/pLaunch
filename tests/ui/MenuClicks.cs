#:project ../../src/pLaunch/pLaunch.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property UseWindowsForms=true
#:property PublishAot=false
#:property NoWarn=WPF0001
#:property ImplicitUsings=disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using pLaunch;
using pLaunch.Models;
using pLaunch.Services;
using pLaunch.ViewModels;

// Real clicks in the side menus (the 0.5.1 bug: a click activated the menu window, the popup closed and
// took the menus with it before anything launched). This moves and clicks the REAL mouse for a few
// seconds, only over this test's own windows, then puts the pointer back. Run it with nothing else going:
//   dotnet run tests/ui/MenuClicks.cs -- <output folder>
// The list lives in <output folder>\data (PLAUNCH_DATA_DIR), never in the user's own.
Environment.SetEnvironmentVariable("PLAUNCH_DATA_DIR", Path.Combine(args[0], "data"));
SetProcessDpiAwarenessContext(-4);
var outDir = args[0];
Directory.CreateDirectory(outDir);
int failures = 0;
var thread = new Thread(() => failures = Run(outDir));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures;

static int Run(string outDir)
{
    // Like the real app, where everything runs inside dispatcher operations: awaits come back to this thread
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    var app = new System.Windows.Application { ThemeMode = ThemeMode.System, ShutdownMode = ShutdownMode.OnExplicitShutdown };
    var res = app.Resources;
    res["IconFont"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
    res["BoolToVisibility"] = new BooleanToVisibilityConverter();
    var footer = new Style(typeof(Button), (Style)app.FindResource(typeof(Button)));
    footer.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
    footer.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
    footer.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
    var dim = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
    dim.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
    footer.Triggers.Add(dim);
    res["FooterButton"] = footer;
    var glyph = new Style(typeof(TextBlock));
    glyph.Setters.Add(new Setter(TextBlock.FontFamilyProperty, res["IconFont"]));
    glyph.Setters.Add(new Setter(TextBlock.FontSizeProperty, 14.0));
    glyph.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
    res["Glyph"] = glyph;

    int failures = 0;
    void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name} {detail}");
        if (!ok) failures++;
    }
    void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    void Wait(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(15); }
    }

    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;
    object? Call(object o, string name, params object?[] a) => o.GetType().GetMethods(Any).First(m => m.Name == name && m.DeclaringType == o.GetType() && m.GetParameters().Length == a.Length).Invoke(o, a);

    (PopupWindow Window, IntPtr Hwnd) Open(List<LaunchItem> items, LauncherSettings settings, string file)
    {
        var store = new ItemStore(Path.Combine(outDir, file));
        store.Save(items, settings);
        var w = new PopupWindow(store);
        typeof(PopupWindow).GetProperty("ActivateOnOpen", Any)!.SetValue(w, false);
        w.Topmost = true;
        w.Start(minimized: true);
        var hwnd = new WindowInteropHelper(w).Handle;
        ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE: the taskbar-click path, without focus
        Wait(700);
        return (w, hwnd);
    }
    var vmsOf = (PopupWindow w) => Field<System.Collections.ObjectModel.ObservableCollection<ItemViewModel>>(w, "_items");
    void Shot(IntPtr hwnd, string name)
    {
        GetWindowRect(hwnd, out var r);
        using var bmp = new System.Drawing.Bitmap(r.Right - r.Left + 40, r.Bottom - r.Top + 40);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left - 20, r.Top - 20, 0, 0, bmp.Size);
        bmp.Save(Path.Combine(outDir, name));
    }

    void Print(Window win, string name)
    {
        var h = new WindowInteropHelper(win).Handle;
        GetWindowRect(h, out var r);
        using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var dc = g.GetHdc();
            PrintWindow(h, dc, 2);
            g.ReleaseHdc(dc);
        }
        bmp.Save(Path.Combine(outDir, name));
    }

    var menusOf = (PopupWindow w) => Field<List<pLaunch.Views.FolderMenu>>(w, "_menus");
    // Real input: the mouse really moves and clicks (only over this test's own windows)
    GetCursorPos(out var userCursor);
    void MoveTo(Point p) { SetCursorPos((int)p.X, (int)p.Y); Wait(30); MouseEvent(0x0001, 1, 0); Wait(30); MouseEvent(0x0001, -1, 0); }
    void Click(Point p, bool right = false)
    {
        MoveTo(p);
        Wait(120);
        MouseEvent(right ? 0x0008u : 0x0002u, 0, 0);
        Wait(60);
        MouseEvent(right ? 0x0010u : 0x0004u, 0, 0);
    }
    void Press(byte vk)
    {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, 2, IntPtr.Zero);
        Wait(250);
    }
    // Top-level windows, front to back
    List<IntPtr> ZOrder()
    {
        var order = new List<IntPtr>();
        EnumWindows((h, _) => { order.Add(h); return true; }, IntPtr.Zero);
        return order;
    }
    Point Center(FrameworkElement e)
    {
        var tl = e.PointToScreen(new Point(0, 0));
        var br = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new Point((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
    }
    try
    {
        var marker = Path.Combine(outDir, "clicked.txt");
        if (File.Exists(marker)) File.Delete(marker);
        var command = new LaunchItem { Kind = ItemKind.Command, Name = "Write the marker", Target = $"echo clicked> \"{marker}\"", StartWindow = StartWindow.Hidden };
        var other = new LaunchItem { Kind = ItemKind.Text, Name = "A snippet", Target = "x", PasteText = false };
        var folder = new LaunchItem { Kind = ItemKind.Group, Name = "Tools", Children = [command, other] };
        var (w, hwnd) = Open([folder, new LaunchItem { Kind = ItemKind.Text, Name = "Plain row", Target = "y", PasteText = false }], new LauncherSettings(), "r.json");
        var list = (ListBox)w.FindName("List");

        // 1. A click on an empty part of the footer makes the popup the active window, as it is for the user
        var menuButton = (FrameworkElement)w.FindName("MenuButton");
        var mb = Center(menuButton);
        Click(new Point(mb.X - 120, mb.Y));
        Wait(300);
        Check("real: the popup is the active window", w.IsActive);

        // 2. Pointing at the folder opens its menu
        var folderRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vmsOf(w)[0]);
        MoveTo(Center(folderRow));
        Wait(400);
        Check("real: pointing at the folder opens its menu", menusOf(w).Count == 1);

        // 2a. The folder is highlighted while its menu is open; pointing at another row closes the menu
        // and takes the highlight away
        Check("real: the folder is highlighted while its menu is open", list.SelectedItem == vmsOf(w)[0]);
        MoveTo(Center((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vmsOf(w)[1])));
        Wait(600);
        Check("real: pointing at another row closes the menu", menusOf(w).Count == 0);
        Check("real: and the folder is no longer highlighted", list.SelectedItem == null, $"(selected {(list.SelectedItem as ItemViewModel)?.Name})");
        MoveTo(Center(folderRow));
        Wait(500);
        Check("real: pointing at the folder again opens its menu", menusOf(w).Count == 1);

        // 2b. A menu opened by pointing leaves the keyboard to the list: Down moves there, not in the menu
        for (int i = 0; i < 2; i++) // the first Down only brings the focus to the list's first row
        {
            keybd_event(0x28, 0, 0, IntPtr.Zero);
            keybd_event(0x28, 0, 2, IntPtr.Zero);
            Wait(150);
        }
        Wait(300);
        Check("real: a menu opened by pointing leaves the arrows to the list",
            list.SelectedIndex == 1 && ((ListBox)menusOf(w)[0].FindName("List")).SelectedItem == null, $"(list row {list.SelectedIndex}, menu selection {((ListBox)menusOf(w)[0].FindName("List")).SelectedItem != null})");

        // 3. A left click on an item of the menu launches it (and the popup stays until then)
        var menu = menusOf(w)[0];
        var commandRow = menu.RowOf(menu.Items.First(i => i.Model.Id == command.Id))!;
        var target = Center(commandRow);
        MoveTo(new Point(target.X - 60, target.Y)); // into the menu along the row
        Wait(200);
        Click(target);
        Wait(2500);
        Check("real: a left click in the menu launches the item", File.Exists(marker));
        Check("real: then the popup closes", !w.IsVisible || w.WindowState == WindowState.Minimized);

        // 4. A right click on an item of the menu opens its context menu
        ShowWindow(hwnd, 9);
        Wait(600);
        Click(new Point(mb.X - 120, mb.Y));
        Wait(300);
        MoveTo(Center((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vmsOf(w)[0])));
        for (int i = 0; i < 20 && menusOf(w).Count == 0; i++)
            Wait(100);
        if (menusOf(w).Count == 0)
        {
            Check("real: pointing at the folder opens its menu again", false, $"(popup {w.WindowState}, active {w.IsActive})");
            return failures;
        }
        menu = menusOf(w)[0];
        var snippetRow = menu.RowOf(menu.Items.First(i => i.Model.Id == other.Id))!;
        var s = Center(snippetRow);
        MoveTo(new Point(s.X - 60, s.Y));
        Wait(200);
        Click(s, right: true);
        Wait(600);
        var context = ((ListBox)menu.FindName("List")).ContextMenu!;
        Check("real: a right click in the menu opens the context menu", context.IsOpen, $"(popup open {w.WindowState}, menus {menusOf(w).Count})");
        Check("real: the popup and the menu stay open meanwhile", w.WindowState == WindowState.Normal && menusOf(w).Count == 1);
        context.IsOpen = false;
        Wait(300);

        // 5. A dialog opened from a menu (Rename) comes above the menu. The popup is not topmost in the app.
        w.Topmost = false;
        // The dialog is modal: it is looked for (and closed) from a timer, until it shows up however late
        int dialogIndex = -1, menuIndex = -1;
        var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var dialogSeen = DateTime.MaxValue;
        probe.Tick += (_, _) =>
        {
            var dialog = System.Windows.Application.Current.Windows.OfType<pLaunch.Views.TextPromptWindow>().FirstOrDefault(d => d.IsVisible);
            if (dialog == null)
                return;
            if (dialogSeen == DateTime.MaxValue)
                dialogSeen = DateTime.Now;
            if (DateTime.Now - dialogSeen < TimeSpan.FromMilliseconds(500))
                return; // shown and settled in the z-order first
            probe.Stop();
            var order = ZOrder();
            dialogIndex = order.IndexOf(new WindowInteropHelper(dialog).Handle);
            menuIndex = order.IndexOf(menu.Handle);
            dialog.Close();
        };
        probe.Start();
        Call(w, "RenameByPrompt", menu.Items.First(i => i.Model.Id == other.Id));
        probe.Stop(); // no dialog at all: the call is over anyway
        Check("real: a dialog opened from a menu is above the menu", dialogIndex >= 0 && menuIndex >= 0 && dialogIndex < menuIndex,
            $"(z-order: dialog {dialogIndex}, menu {menuIndex})");
        Wait(400);

        // 6. Keys with a menu opened by pointing: Ctrl alone leaves it open, Esc closes it (the popup stays)
        Press(0x11);
        Check("real: Ctrl pressed alone leaves the menu open", menusOf(w).Count == 1);
        Press(0x1B);
        Check("real: Esc closes the menu opened by pointing, not the popup", menusOf(w).Count == 0 && w.WindowState == WindowState.Normal);

        // 7. Right on the folder opens its menu for the keyboard; Right on a program row does nothing; Down moves
        list.SelectedItem = vmsOf(w)[0];
        Press(0x27);
        var keyMenu = menusOf(w).FirstOrDefault();
        var keyList = keyMenu == null ? null : (ListBox)keyMenu.FindName("List");
        Check("real: Right on the folder opens its menu, first row selected", keyMenu is { KeyboardActive: true } && keyList!.SelectedIndex == 0);
        Press(0x27);
        Check("real: Right on a program row does nothing", menusOf(w).Count == 1 && menusOf(w)[0] == keyMenu && keyList!.SelectedIndex == 0);
        Press(0x28);
        Check("real: Down moves in the keyboard's menu", keyList!.SelectedIndex == 1);
        Press(0x1B);

        // 8. A middle click launches and keeps the list (and the menu) open, until the pointer leaves them
        File.Delete(marker);
        list.SelectedItem = null;
        MoveTo(Center((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vmsOf(w)[1])));
        Wait(300);
        MoveTo(Center(folderRow));
        Wait(600);
        var stayMenu = menusOf(w).FirstOrDefault();
        Check("real: the menu is open for the middle click", stayMenu != null);
        if (stayMenu != null)
        {
            var row = stayMenu.RowOf(stayMenu.Items.First(i => i.Model.Id == command.Id))!;
            var at = Center(row);
            MoveTo(new Point(at.X - 60, at.Y));
            Wait(200);
            MoveTo(at);
            Wait(120);
            MouseEvent(0x0020, 0, 0); // middle button
            Wait(60);
            MouseEvent(0x0040, 0, 0);
            Wait(2500);
            Check("real: a middle click launches the item", File.Exists(marker));
            Check("real: and the list stays open", w.WindowState == WindowState.Normal && w.IsVisible);
            // The command showed no window: the list is still the active one, and stays while it is (the
            // pointer may be aside while typing the next search)
            MoveTo(new Point(at.X - 900, at.Y - 500));
            Wait(1800);
            Check("real: also with the pointer away, while it is the active window", w.WindowState == WindowState.Normal, $"(active {w.IsActive})");
            // Another window takes the focus (as a launched program does): now the pointer being away closes it
            var elsewhere = new Window { Title = "elsewhere", Width = 240, Height = 120, Left = 40, Top = 40, Topmost = true };
            elsewhere.Show();
            elsewhere.Activate();
            Wait(600);
            Wait(1800);
            Check("real: and closes once the pointer is away and the focus elsewhere", w.WindowState == WindowState.Minimized, $"(active {w.IsActive})");
            elsewhere.Close();
            Wait(300);
        }

        // 9. A search result says which sub-folder it is in; after the search no row does (the rows are
        // shared with the menus, which would show it too)
        ShowWindow(hwnd, 9);
        Wait(600);
        Click(new Point(mb.X - 120, mb.Y));
        Wait(300);
        foreach (byte key in "WRITE"u8.ToArray())
        {
            keybd_event(key, 0, 0, IntPtr.Zero);
            keybd_event(key, 0, 2, IntPtr.Zero);
            Wait(80);
        }
        Wait(700);
        var found = vmsOf(w).FirstOrDefault(v => v.Model.Id == command.Id);
        Check("real: a search result says where it is", found?.Location == "Tools", $"(location '{found?.Location}')");
        Press(0x1B); // clears the search
        Wait(400);
        Check("real: and no longer after the search", found != null && found.Location == null && !found.HasLocation);
        Press(0x1B);
        w.Close();
    }
    finally
    {
        SetCursorPos(userCursor.X, userCursor.Y);
    }

    Dispatcher.CurrentDispatcher.InvokeShutdown();
    return failures;
}

static void MouseEvent(uint flags, int dx, int dy)
{
    var input = new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } };
    SendInput(1, [input], Marshal.SizeOf<INPUT>());
}

[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
[DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
[DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT rect);
[DllImport("user32.dll")] static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
[DllImport("user32.dll")] static extern bool GetCursorPos(out POINTI p);
[DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
struct RECT { public int Left, Top, Right, Bottom; }
delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
struct POINTI { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
[StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
