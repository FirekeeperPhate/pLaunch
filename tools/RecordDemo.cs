#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// Records docs/demo.gif, the animation at the top of the README: a demo list (its own data folder, never
// the user's) opened over a plain backdrop with captions, so nothing of the desktop is in the picture.
// It moves the REAL mouse and types for about 20 seconds, and needs ffmpeg on the PATH.
//   dotnet run tools/RecordDemo.cs -- <pLaunch.exe> [output.gif]
SetProcessDpiAwarenessContext(-4);
string exe = args[0];
string gif = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine("docs", "demo.gif"));
string work = Path.Combine(Path.GetTempPath(), "pLaunchDemo");
if (Directory.Exists(work)) Directory.Delete(work, true);
string data = Path.Combine(work, "data");
Directory.CreateDirectory(data);
Directory.CreateDirectory(Path.GetDirectoryName(gif)!);

// ---- the demo list
string system = Environment.SystemDirectory, programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
int nextId = 0;
Dictionary<string, object?> Item(string kind, string name, string? target = null, object[]? children = null)
{
    var item = new Dictionary<string, object?> { ["Id"] = (++nextId).ToString("x32"), ["Kind"] = kind, ["Name"] = name };
    if (target != null) item["Target"] = target;
    if (children != null) item["Children"] = children;
    return item;
}
var list = new List<object>();
void AddProgram(string name, string path) { if (File.Exists(path)) list.Add(Item("File", name, path)); }
AddProgram("Firefox", Path.Combine(programs, @"Mozilla Firefox\firefox.exe"));
AddProgram("7-Zip", Path.Combine(programs, @"7-Zip\7zFM.exe"));
list.Add(Item("Shell", "Calculator", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
list.Add(Item("Shell", "Paint", @"shell:AppsFolder\Microsoft.Paint_8wekyb3d8bbwe!App"));
list.Add(Item("Separator", ""));
list.Add(Item("Group", "Tools", children:
[
    Item("File", "Command Prompt", Path.Combine(system, "cmd.exe")),
    Item("File", "Character Map", Path.Combine(system, "charmap.exe")),
    Item("File", "Event Viewer", Path.Combine(system, "eventvwr.msc")),
    Item("File", "Registry Editor", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe")),
]));
list.Add(Item("Group", "Web", children:
[
    Item("Url", "GitHub", "https://github.com/"),
    Item("Url", "Wikipedia", "https://www.wikipedia.org/"),
]));
list.Add(Item("Folder", "Windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
File.WriteAllText(Path.Combine(data, "items.json"), JsonSerializer.Serialize(new { Settings = new { WebIcons = false }, Items = list }));

Environment.SetEnvironmentVariable("PLAUNCH_DATA_DIR", data);
using var app = Process.Start(new ProcessStartInfo(exe, "--minimized") { UseShellExecute = true })!;
Thread.Sleep(3500);

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
AutomationElement? button = null;
var taskbar = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
foreach (AutomationElement b in taskbar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer")))
    if (b.Current.Name.StartsWith("pLaunch")) button = b;
if (hwnd == IntPtr.Zero || button == null)
{
    Console.WriteLine("pLaunch did not start (window or taskbar button missing)");
    if (!app.HasExited) app.Kill();
    return 1;
}
var buttonRect = button.Current.BoundingRectangle;
int bx = (int)(buttonRect.Left + buttonRect.Width / 2), by = (int)(buttonRect.Top + buttonRect.Height / 2);
GetWindowRect(FindWindow("Shell_TrayWnd", null), out var bar);

// ---- what is recorded: a rectangle above the taskbar around where the list opens (even sizes for the encoder)
const int Width = 1020, Height = 720;
int left = Math.Clamp(bx - 280, 0, bar.R - Width), top = bar.T - Height;

// ---- the backdrop: a plain window under the list, with the caption of the scene
Window? backdrop = null;
TextBlock? caption = null;
var ready = new ManualResetEventSlim();
var ui = new Thread(() =>
{
    double scale = GetDpiForSystem() / 96.0;
    caption = new TextBlock
    {
        // The backdrop starts 40 px outside the picture: the caption sits at its top left, on one line
        Margin = new Thickness((40 + 34) / scale, (40 + 30) / scale, 0, 0), FontSize = 23, FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
    };
    backdrop = new Window
    {
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
        Left = (left - 40) / scale, Top = (top - 40) / scale, Width = (Width + 80) / scale, Height = (Height + 40) / scale,
        Background = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x4A), Color.FromRgb(0x3A, 0x1F, 0x5C), 35),
        Content = caption,
    };
    backdrop.Show();
    ready.Set();
    Dispatcher.Run();
});
ui.SetApartmentState(ApartmentState.STA);
ui.IsBackground = true;
ui.Start();
ready.Wait();
void Caption(string text) => caption!.Dispatcher.Invoke(() => caption.Text = text);

GetCursorPos(out var userCursor);
int cx = left + 200, cy = top + 420; // where the pointer rests, in the picture
void Glide(int x, int y, int ms = 500)
{
    GetCursorPos(out var from);
    int steps = Math.Max(1, ms / 15);
    for (int i = 1; i <= steps; i++)
    {
        double t = (double)i / steps;
        t = t * t * (3 - 2 * t); // ease in and out
        SetCursorPos((int)(from.X + (x - from.X) * t), (int)(from.Y + (y - from.Y) * t));
        mouse_event(0x0001, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(15);
    }
}
void Click() { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(70); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
void Type(string keys, int pause = 220)
{
    foreach (char key in keys)
    {
        keybd_event((byte)char.ToUpperInvariant(key), 0, 0, IntPtr.Zero);
        keybd_event((byte)char.ToUpperInvariant(key), 0, 2, IntPtr.Zero);
        Thread.Sleep(pause);
    }
}
void Key(byte vk) { keybd_event(vk, 0, 0, IntPtr.Zero); keybd_event(vk, 0, 2, IntPtr.Zero); }
(int X, int Y)? Row(string name)
{
    foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id)))
        foreach (AutomationElement text in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)))
        {
            var r = text.Current.BoundingRectangle;
            if (!r.IsEmpty) return ((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        }
    return null;
}

SetCursorPos(cx, cy);
const string Title = "pLaunch \xB7 Quick Launch for Windows 11";
Caption(Title);
Thread.Sleep(600);

// ---- recording
string video = Path.Combine(work, "demo.mkv");
var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
    $"-y -loglevel error -f gdigrab -framerate 15 -draw_mouse 1 -offset_x {left} -offset_y {top} -video_size {Width}x{Height} -i desktop -c:v libx264 -preset ultrafast -crf 12 \"{video}\"")
{ UseShellExecute = false, RedirectStandardInput = true })!;
Thread.Sleep(1500);

try
{
    // 1. One button on the taskbar (just below the picture): a click opens the list
    Caption("One taskbar button opens your shortcuts");
    Thread.Sleep(900);
    Glide(bx, by, 700);
    Click();
    Thread.Sleep(500);
    Glide(bx + 40, bar.T - 120, 500);
    Thread.Sleep(1300);

    // 2. Sub-folders open in a menu beside the list, just by pointing at them
    Caption("Sub-folders open beside the list");
    if (Row("Tools") is { } tools)
    {
        Glide(tools.X, tools.Y, 600);
        Thread.Sleep(1000);
        if (Row("Event Viewer") is { } inMenu)
        {
            Glide(inMenu.X, inMenu.Y, 700);
            Thread.Sleep(600);
            if (Row("Command Prompt") is { } first)
                Glide(first.X, first.Y, 500);
        }
        Thread.Sleep(900);
    }
    if (Row("Web") is { } web)
    {
        Glide(web.X, web.Y, 600);
        Thread.Sleep(1500);
    }

    // 3. Typing searches every sub-folder (and runs anything, like Win+R)
    Caption("Just type to search");
    if (Row("Paint") is { } paint)
        Glide(paint.X + 60, paint.Y, 500);
    Thread.Sleep(500);
    Type("ev");
    Thread.Sleep(1700);
    Key(0x1B); // clears the search
    Thread.Sleep(120);
    Key(0x1B); // closes the list
    Caption(Title);
    Thread.Sleep(1600);
}
finally
{
    ffmpeg.StandardInput.Write('q');
    ffmpeg.StandardInput.Flush();
    ffmpeg.WaitForExit(10000);
    SetCursorPos(userCursor.X, userCursor.Y);
    backdrop!.Dispatcher.Invoke(backdrop.Close);
    PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
    if (!app.WaitForExit(5000)) app.Kill();
}

// ---- the GIF: the start-up second cut off, 12 frames a second, 720 wide, its own palette
// Every frame drawn whole with one palette: updating only the changed rectangles left out text that appeared late
string filters = "fps=12,scale=720:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=160[p];[b][p]paletteuse=dither=bayer:bayer_scale=5";
var convert = Process.Start(new ProcessStartInfo("ffmpeg", $"-y -loglevel error -ss 1.2 -i \"{video}\" -vf \"{filters}\" \"{gif}\"") { UseShellExecute = false })!;
convert.WaitForExit();
Console.WriteLine(File.Exists(gif) ? $"Wrote {gif} ({new FileInfo(gif).Length / 1024} KB)" : "ffmpeg did not write the GIF");
return File.Exists(gif) ? 0 : 1;

[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
[DllImport("user32.dll")] static extern uint GetDpiForSystem();
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? title);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
[DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
[DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
struct POINT { public int X, Y; }
struct RECT { public int L, T, R, B; }
