#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

// Real clicks on pLaunch's taskbar button: a click opens the list, the next one closes it for good (the
// list must not show up again for a moment when the button is released, and the focus goes back to the
// window in front), also when the click is a slow one. This starts the given pLaunch.exe with its own data
// folder and moves and clicks the REAL mouse on its taskbar button for a few seconds, then puts the pointer
// back. Run it with no other pLaunch going:
//   dotnet run tests/ui/TaskbarButton.cs -- <pLaunch.exe> <output folder>
SetProcessDpiAwarenessContext(-4);
Directory.CreateDirectory(args[1]);
Environment.SetEnvironmentVariable("PLAUNCH_DATA_DIR", Path.Combine(args[1], "data"));
using var app = Process.Start(new ProcessStartInfo(args[0], "--minimized") { UseShellExecute = true })!;
Thread.Sleep(3000);

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
    if (pid != app.Id || !IsWindowVisible(h)) return true;
    hwnd = h;
    return false;
}, IntPtr.Zero);
AutomationElement? button = null;
var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
foreach (AutomationElement b in tray.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer")))
    if (b.Current.Name.StartsWith("pLaunch")) button = b;
if (hwnd == IntPtr.Zero || button == null)
{
    Console.WriteLine("FAIL setup: no pLaunch window or taskbar button");
    if (!app.HasExited) app.Kill();
    return 1;
}
var rect = button.Current.BoundingRectangle;
int x = (int)(rect.Left + rect.Width / 2), y = (int)(rect.Top + rect.Height / 2);
GetCursorPos(out var userCursor);

// Shown = restored and not cloaked
bool Shown() => !IsIconic(hwnd) && !(DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0);

// A click held for holdMs; returns how often the list showed up again between the press and a second later
int Click(int holdMs)
{
    SetCursorPos(x, y);
    Thread.Sleep(300);
    bool last = Shown();
    int shownAgain = 0;
    var watch = Stopwatch.StartNew();
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    bool released = false;
    while (watch.ElapsedMilliseconds < holdMs + 1000)
    {
        if (!released && watch.ElapsedMilliseconds >= holdMs)
        {
            mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
            released = true;
        }
        bool now = Shown();
        if (now && !last) shownAgain++;
        last = now;
        Thread.Sleep(1);
    }
    return shownAgain;
}

Click(60);
Check("a click on the taskbar button opens the list", Shown());
foreach (var (kind, hold) in new[] { ("quick", 70), ("slow", 700) })
{
    if (!Shown())
        Click(60);
    int again = Click(hold);
    Check($"a {kind} click on the button closes the open list", !Shown());
    Check($"  and the list never shows up again meanwhile", again == 0, $"(shown again {again} times)");
    Check($"  and the focus is not left on the hidden list", GetForegroundWindow() != hwnd);
    Click(60);
    Check($"  and the next click opens it again", Shown());
}
Click(60);

SetCursorPos(userCursor.X, userCursor.Y);
PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
if (!app.WaitForExit(5000)) app.Kill();
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures;

[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
[DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
[DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
struct POINT { public int X, Y; }
