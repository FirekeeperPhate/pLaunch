#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

// The notification area icon (Settings > Icon of this list): with "In the notification area" there is no
// taskbar button, a click on the icon opens the list beside it and another one closes it, a right click
// shows a menu that goes away when clicking elsewhere. This starts the given pLaunch.exe with its own data
// folder and moves and clicks the REAL mouse on the notification area for a few seconds (it opens the
// hidden icons when the icon is there), then puts the pointer back. Run it with no other pLaunch going:
//   dotnet run tests/ui/TrayIcon.cs -- <pLaunch.exe> <output folder>
SetProcessDpiAwarenessContext(-4);
var data = Path.Combine(args[1], "data");
if (Directory.Exists(data)) Directory.Delete(data, true);
Directory.CreateDirectory(data);
// The list's only item: a hidden command that leaves a file behind when it is launched
string launched = Path.Combine(Path.GetFullPath(args[1]), "launched.txt");
File.Delete(launched);
string command = System.Text.Json.JsonSerializer.Serialize("echo done> \"" + launched + "\"");
File.WriteAllText(Path.Combine(data, "items.json"),
    "{ \"Settings\": { \"IconPlace\": \"Tray\" }, \"Items\": [ { \"Id\": \"0123456789abcdef0123456789abcdef\", \"Kind\": \"Command\", \"Name\": \"Marker\", \"Target\": " + command + ", \"StartWindow\": \"Hidden\" } ] }");
Environment.SetEnvironmentVariable("PLAUNCH_DATA_DIR", data);
using var app = Process.Start(new ProcessStartInfo(args[0], "--minimized") { UseShellExecute = true })!;
Thread.Sleep(3500);

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name} {detail}");
    if (!ok) failures++;
}

IntPtr hwnd = IntPtr.Zero;
EnumWindows((h, _) =>
{
    GetWindowThreadProcessId(h, out var pid);
    var title = new StringBuilder(64);
    GetWindowText(h, title, 64);
    if (pid != app.Id || title.ToString() != "pLaunch") return true;
    hwnd = h;
    return false;
}, IntPtr.Zero);
GetCursorPos(out var userCursor);

bool Shown() => !IsIconic(hwnd) && !(DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0);
int VisibleWindows()
{
    int count = 0;
    EnumWindows((h, _) =>
    {
        GetWindowThreadProcessId(h, out var pid);
        if (pid == app.Id && IsWindowVisible(h) && !IsIconic(h)) count++;
        return true;
    }, IntPtr.Zero);
    return count;
}
// A visible window of pLaunch that is not the list: a menu, a dialog
IntPtr OtherWindow()
{
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, _) =>
    {
        GetWindowThreadProcessId(h, out var pid);
        if (pid != app.Id || h == hwnd || !IsWindowVisible(h) || IsIconic(h)) return true;
        found = h;
        return false;
    }, IntPtr.Zero);
    return found;
}
void MoveTo(int x, int y) { SetCursorPos(x, y); Thread.Sleep(250); }
void Click(int x, int y, bool right = false)
{
    MoveTo(x, y);
    mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(60);
    mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(900);
}

var taskbar = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
bool HasTaskbarButton()
{
    foreach (AutomationElement b in taskbar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer")))
        if (b.Current.Name.StartsWith("pLaunch")) return true;
    return false;
}
Check("notification area only: no taskbar button", !HasTaskbarButton());

var id = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = 1 };
bool registered = Shell_NotifyIconGetRect(ref id, out var rect) == 0;
Check("the icon is in the notification area", registered);
if (hwnd == IntPtr.Zero || !registered)
{
    if (!app.HasExited) app.Kill();
    return 1;
}

// The icon itself: on the taskbar, or among the hidden ones (the rectangle is then the arrow that shows them)
(int X, int Y)? FindIcon()
{
    foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
    {
        string cls = window.Current.ClassName;
        if (cls != "Shell_TrayWnd" && !cls.Contains("Overflow")) continue;
        foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            if (e.Current.Name != "pLaunch" || e.Current.ClassName.Contains("TaskListButton")) continue;
            var r = e.Current.BoundingRectangle;
            if (!r.IsEmpty) return ((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        }
    }
    return null;
}
(int X, int Y) Icon()
{
    if (FindIcon() is { } visible) return visible;
    // Among the hidden icons: the arrow shows them. The flyout does not always open at the first click
    // (e.g. right after another window had the mouse), and takes a moment to be there: tried again.
    for (int attempt = 0; attempt < 3; attempt++)
    {
        Click((rect.L + rect.R) / 2, (rect.T + rect.B) / 2);
        for (int wait = 0; wait < 5; wait++)
        {
            if (FindIcon() is { } found) return found;
            Thread.Sleep(300);
        }
    }
    return (-1, -1);
}

var icon = Icon();
Check("the icon can be found on screen", icon.X >= 0, $"({icon.X},{icon.Y})");
if (icon.X >= 0)
{
    Click(icon.X, icon.Y);
    Check("a click on the icon opens the list", Shown());
    Thread.Sleep(1500); // a taskbar button would have come by now
    Check("  and no taskbar button comes with the open list", !HasTaskbarButton());
    GetWindowRect(hwnd, out var popup);
    GetWindowRect(FindWindow("Shell_TrayWnd", null), out var bar);
    Check("  against the taskbar, near the icon", Math.Abs(bar.T - popup.B) < 60 && popup.L <= icon.X + 40 && popup.R >= icon.X - 500,
        $"(popup {popup.L},{popup.T},{popup.R},{popup.B}; icon x {icon.X}; taskbar top {bar.T})");

    // A second click: pressing takes the focus from the list (it hides), releasing must not open it again
    if (FindIcon() is { } still)
    {
        Click(still.X, still.Y);
        Check("another click on the icon closes the list", !Shown());
    }
    else
    {
        // Among the hidden icons: showing them already closes the list (it lost the focus)
        var again = Icon();
        Check("the list closes when the focus goes elsewhere", !Shown());
        icon = again;
    }

    // A right click: the menu, which closes when clicking somewhere else
    icon = Icon();
    int before = VisibleWindows();
    Click(icon.X, icon.Y, right: true);
    Check("a right click on the icon shows a menu", VisibleWindows() > before && !Shown(), $"(windows {before} -> {VisibleWindows()})");
    var menu = OtherWindow();
    GetWindowRect(menu, out var menuRect);
    Check("  at the icon", Math.Abs((menuRect.L + menuRect.R) / 2 - icon.X) < 400 && menuRect.B <= icon.Y + 40 && menuRect.B > icon.Y - 400,
        $"(menu {menuRect.L},{menuRect.T},{menuRect.R},{menuRect.B}; icon {icon.X},{icon.Y})");
    Click((bar.L + rect.L) / 2 + 200, (bar.T + bar.B) / 2); // an empty part of the taskbar, left of the notification area
    Check("  which closes when clicking elsewhere", VisibleWindows() <= before, $"(windows {VisibleWindows()})");
    Check("  and leaves the focus to another window", GetForegroundWindow() != hwnd);

    // From the keyboard the shell says "selected" twice for Enter: the list opens, and stays open
    var place = new IntPtr(((icon.Y & 0xFFFF) << 16) | (icon.X & 0xFFFF));
    const int Callback = 0x8051, Select = 0x400, KeySelect = 0x401, ContextMenu = 0x7B;
    PostMessage(hwnd, Callback, place, new IntPtr(KeySelect));
    PostMessage(hwnd, Callback, place, new IntPtr(KeySelect));
    Thread.Sleep(1200);
    Check("Enter on the icon (two messages) opens the list once", Shown());

    // The menu lists the shortcuts: one is launched from there without opening the list
    PostMessage(hwnd, Callback, place, new IntPtr(Select)); // closes the list
    Thread.Sleep(900);
    Click(icon.X = Icon().X, icon.Y, right: true);
    AutomationElement? shortcut = null;
    foreach (AutomationElement item in AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                 new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))))
        if (item.Current.Name == "Marker") shortcut = item;
    Check("the menu lists the list's shortcuts", shortcut != null);
    if (shortcut != null)
    {
        var r = shortcut.Current.BoundingRectangle;
        Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        for (int i = 0; i < 30 && !File.Exists(launched); i++)
            Thread.Sleep(100);
        Check("  a click on one launches it, without opening the list", File.Exists(launched) && !Shown());
        Thread.Sleep(2500);
        Check("  and the focus is not left on the hidden list (the command shows no window)", GetForegroundWindow() != hwnd);
    }

    // With one of pLaunch's dialogs open the icon brings that to the front: nothing closes, nothing else opens
    Click(icon.X = Icon().X, icon.Y, right: true);
    AutomationElement? settingsItem = null;
    foreach (AutomationElement item in AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                 new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))))
        if (item.Current.Name.StartsWith("Settings")) settingsItem = item;
    Check("the menu has Settings", settingsItem != null);
    if (settingsItem != null)
    {
        var r = settingsItem.Current.BoundingRectangle;
        Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        Thread.Sleep(1500);
        var dialog = OtherWindow();
        int withDialog = VisibleWindows();
        Check("Settings opens from the menu, over the list", dialog != IntPtr.Zero && Shown(), $"(windows {withDialog})");
        PostMessage(hwnd, Callback, place, new IntPtr(Select));
        Thread.Sleep(900);
        Check("a click on the icon then keeps the list and the dialog", Shown() && VisibleWindows() == withDialog && IsWindowVisible(dialog), $"(windows {VisibleWindows()})");
        PostMessage(hwnd, Callback, place, new IntPtr(ContextMenu));
        Thread.Sleep(900);
        Check("  and a right click shows no second menu or dialog", VisibleWindows() == withDialog, $"(windows {VisibleWindows()})");
        PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE: Settings
        Thread.Sleep(800);
    }
}

SetCursorPos(userCursor.X, userCursor.Y);
PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
if (!app.WaitForExit(5000)) app.Kill();
Thread.Sleep(500);
Check("the icon goes away with pLaunch", Shell_NotifyIconGetRect(ref id, out _) != 0);
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures;

[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? title);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
[DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
[DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
[DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);
delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
struct POINT { public int X, Y; }
struct RECT { public int L, T, R, B; }
struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
