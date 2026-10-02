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

// Real drags from a File Explorer window: a file held on pLaunch's taskbar button opens the list; dropped on
// an empty part of the list it is added, dropped on a program it is opened with that program. This starts
// the given pLaunch.exe with its own data folder, opens an Explorer window on a folder it creates and
// drags with the REAL mouse for a few seconds, then puts the pointer back. A drag that does not open the
// list is cancelled with Esc: nothing is ever dropped on the taskbar. Run it with no other pLaunch going:
//   dotnet run tests/ui/DragAndDrop.cs -- <pLaunch.exe> <output folder>
SetProcessDpiAwarenessContext(-4);
string output = Path.GetFullPath(args[1]);
string data = Path.Combine(output, "data"), files = Path.Combine(output, "pLaunchDragTest");
if (Directory.Exists(data)) Directory.Delete(data, true);
Directory.CreateDirectory(data);
Directory.CreateDirectory(files);
string note = Path.Combine(files, "note.txt"), script = Path.Combine(output, "mark.bat"), opened = Path.Combine(output, "opened.txt");
File.WriteAllText(note, "hello");
File.Delete(opened);
// A shortcut to a program, as dragged from the desktop to be added to the list
dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
dynamic link = shell.CreateShortcut(Path.Combine(files, "tool.lnk"));
link.TargetPath = Path.Combine(Environment.SystemDirectory, "charmap.exe");
link.Save();
// The "program": it writes down what it was asked to open
File.WriteAllText(script, "@echo %~1> \"" + opened + "\"\r\n");
File.WriteAllText(Path.Combine(data, "items.json"),
    "{ \"Items\": [ { \"Id\": \"0123456789abcdef0123456789abcdef\", \"Kind\": \"File\", \"Name\": \"Marker\", \"Target\": \"" + script.Replace("\\", "\\\\") + "\", \"StartWindow\": \"Minimized\" } ] }");
Environment.SetEnvironmentVariable("PLAUNCH_DATA_DIR", data);
using var app = Process.Start(new ProcessStartInfo(args[0], "--minimized") { UseShellExecute = true })!;
Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + files + "\"")!.Dispose();
Thread.Sleep(4000);

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
AutomationElement? explorer = null;
foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass")))
    if (w.Current.Name.StartsWith("pLaunchDragTest")) explorer = w;
AutomationElement? source = null, shortcut = null;
if (explorer != null)
    foreach (AutomationElement e in explorer.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
    {
        if (e.Current.Name is "note.txt" or "note") source = e;
        if (e.Current.Name is "tool.lnk" or "tool") shortcut = e;
    }
AutomationElement? button = null;
var taskbar = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
foreach (AutomationElement b in taskbar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer")))
    if (b.Current.Name.StartsWith("pLaunch")) button = b;
GetCursorPos(out var userCursor);

void Cleanup()
{
    SetCursorPos(userCursor.X, userCursor.Y);
    if (explorer != null)
        try { ((WindowPattern)explorer.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch (Exception) { }
    if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
    if (!app.WaitForExit(5000)) app.Kill();
}

if (hwnd == IntPtr.Zero || source == null || button == null)
{
    Console.WriteLine($"FAIL setup: pLaunch window {hwnd != IntPtr.Zero}, file in Explorer {source != null}, taskbar button {button != null}");
    Cleanup();
    return 1;
}

void Move(int x, int y) { SetCursorPos(x, y); mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); Thread.Sleep(15); }
void Glide(int fromX, int fromY, int toX, int toY, int steps)
{
    for (int i = 1; i <= steps; i++)
        Move(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps);
}
bool Shown() => !IsIconic(hwnd) && !(DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0);

// Drags the file from Explorer onto the taskbar button and holds it there. True when the list opened (the
// button is still held then); otherwise the drag was cancelled.
bool DragOntoButton(AutomationElement? what = null)
{
    var from = (what ?? source).Current.BoundingRectangle;
    var to = button.Current.BoundingRectangle;
    int sx = (int)(from.Left + Math.Min(40, from.Width / 2)), sy = (int)(from.Top + from.Height / 2);
    int bx = (int)(to.Left + to.Width / 2), by = (int)(to.Top + to.Height / 2);
    Move(sx, sy);
    Thread.Sleep(400);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(150);
    Glide(sx, sy, sx + 30, sy + 30, 10); // past the drag threshold: Explorer starts the drag
    Glide(sx + 30, sy + 30, bx, by, 40);
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < 3000 && !Shown())
    {
        mouse_event(0x0001, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
    }
    if (Shown())
    {
        Thread.Sleep(400);
        return true;
    }
    keybd_event(0x1B, 0, 0, IntPtr.Zero);
    keybd_event(0x1B, 0, 2, IntPtr.Zero);
    Thread.Sleep(300);
    Move(sx, sy);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    return false;
}
void DropAt(int x, int y)
{
    GetCursorPos(out var now);
    Glide(now.X, now.Y, x, y, 25);
    Thread.Sleep(600);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(1800);
}
string List() => File.ReadAllText(Path.Combine(data, "items.json"));

// 1. Dropped on the empty part of the list (below its only row): added
bool open = DragOntoButton();
Check("a file held on the taskbar button opens the list", open);
if (open)
{
    GetWindowRect(hwnd, out var popup);
    var row = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
    var rowRect = row.Current.BoundingRectangle;
    DropAt((int)(rowRect.Left + rowRect.Width / 2), (int)rowRect.Bottom + 12);
    Check("dropped below the items, the file is added to the list", List().Contains("note.txt"));
    Check("  and not opened", !File.Exists(opened));

    // 2. Dropped on the program: opened with it, nothing added
    string before = List();
    open = DragOntoButton();
    Check("the next drag opens the list again", open);
    if (open)
    {
        row = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        rowRect = row.Current.BoundingRectangle;
        DropAt((int)(rowRect.Left + rowRect.Width / 2), (int)(rowRect.Top + rowRect.Height / 2));
        for (int i = 0; i < 30 && !File.Exists(opened); i++)
            Thread.Sleep(100);
        Check("dropped on a program, the file is opened with it", File.Exists(opened) && File.ReadAllText(opened).Trim().Equals(note, StringComparison.OrdinalIgnoreCase),
            $"({(File.Exists(opened) ? File.ReadAllText(opened).Trim() : "nothing opened")})");
        Check("  the list closes, as after a launch", !Shown());
        int Count(string text) => text.Split("note.txt").Length - 1;
        Check("  and the file is not added a second time", Count(List()) == Count(before));
    }

    // 3. A shortcut to a program dropped on a program: it is being added to the list, not opened with it
    File.Delete(opened);
    open = shortcut != null && DragOntoButton(shortcut);
    Check("a shortcut held on the button opens the list too", open, shortcut == null ? "(shortcut not found in Explorer)" : "");
    if (open)
    {
        row = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        rowRect = row.Current.BoundingRectangle;
        DropAt((int)(rowRect.Left + rowRect.Width / 2), (int)(rowRect.Top + rowRect.Height / 2));
        Check("dropped on a program, a shortcut is added to the list", List().Contains("tool.lnk"));
        Check("  and the program is not started with it", !File.Exists(opened));
    }
}

Cleanup();
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures;

[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
[DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
[DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
[DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
struct POINT { public int X, Y; }
struct RECT { public int L, T, R, B; }
