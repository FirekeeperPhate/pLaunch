using System.Runtime.InteropServices;

namespace pLaunch.Native;

/// <summary>
/// A taskbar button without an entry in Alt+Tab and Win+Tab. Windows lists the same windows in both:
/// a tool window is left out of the switchers (and of the taskbar), and ITaskbarList::AddTab gives it
/// its button back. The shell drops such buttons when it restarts: they are added again on the
/// "TaskbarCreated" message. <see cref="TaskbarButton"/> puts these calls together for a window.
/// </summary>
internal static class TaskbarTab
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    /// <summary>Sent to every top-level window when the taskbar is (re)created, e.g. after Explorer restarts.</summary>
    public static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    /// <summary>Sent to a window once its taskbar button exists (also for a button given with <see cref="Add"/>).</summary>
    public static readonly int TaskbarButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");

    /// <summary>Out of Alt+Tab and Win+Tab (and of the taskbar, until <see cref="Add"/>).</summary>
    public static void HideFromSwitchers(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    const int WM_WINDOWPOSCHANGING = 0x0046;
    const int Offscreen = -32000; // where Windows parks minimized windows that have a button
    const int SM_CXMINIMIZED = 57, SM_CYMINIMIZED = 58;

    /// <summary>
    /// Minimized tool windows are not parked off screen like other windows: Windows lines them up as small
    /// title bars at the bottom left, above the taskbar (a white bar may show there). Call from the
    /// window procedure: the minimized position is moved off screen.
    /// </summary>
    public static void KeepMinimizedOffscreen(IntPtr hwnd, int msg, IntPtr lParam)
    {
        if (msg != WM_WINDOWPOSCHANGING || !IsIconic(hwnd))
            return;
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if (!ShouldPark(pos.flags, pos.x, pos.y, pos.cx, pos.cy, MinimizedSizes(hwnd, pos.x, pos.y)))
            return;
        pos.x = Offscreen;
        pos.y = Offscreen;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    /// <summary>
    /// For a window that is (still) minimized: whether this position change puts its little title bar
    /// somewhere on screen. That bar has exactly the minimized size Windows reports (SM_CXMINIMIZED x
    /// SM_CYMINIMIZED, e.g. 237 x 39 at 150 %); any other size is the restore, which must go where it is
    /// sent. Not either when it keeps its place or is off screen already.
    /// </summary>
    internal static bool ShouldPark(uint flags, int x, int y, int cx, int cy, IEnumerable<(int Width, int Height)> minimizedSizes)
    {
        if ((flags & SWP_NOMOVE) != 0 || (x == Offscreen && y == Offscreen))
            return false;
        return (flags & SWP_NOSIZE) != 0 || minimizedSizes.Any(s => Math.Abs(cx - s.Width) <= 1 && Math.Abs(cy - s.Height) <= 1);
    }

    /// <summary>
    /// The minimized size at every DPI the bar may have been sized for: the session's (GetSystemMetrics,
    /// fixed at sign-in), the window's, and the monitor's where it is being put. They differ after a
    /// scaling change without signing out, or with monitors of different scaling.
    /// </summary>
    static IEnumerable<(int Width, int Height)> MinimizedSizes(IntPtr hwnd, int x, int y)
    {
        yield return (GetSystemMetrics(SM_CXMINIMIZED), GetSystemMetrics(SM_CYMINIMIZED));
        foreach (uint dpi in new[] { GetDpiForWindow(hwnd), MonitorDpi(x, y) })
        {
            if (dpi != 0)
                yield return (GetSystemMetricsForDpi(SM_CXMINIMIZED, dpi), GetSystemMetricsForDpi(SM_CYMINIMIZED, dpi));
        }
    }

    static uint MonitorDpi(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 0;
    }

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Takes the button away again. A button given with <see cref="Add"/> is not always removed by the
    /// shell when its window goes (it stayed on the taskbar after pLaunch closed): called while the
    /// window is being destroyed.
    /// </summary>
    public static void Remove(IntPtr hwnd)
    {
        try
        {
            var list = (ITaskbarList)new TaskbarList();
            try
            {
                list.HrInit();
                list.DeleteTab(hwnd);
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No taskbar: nothing to remove
        }
    }

    /// <summary>
    /// Asks for the window's taskbar button. Whether it came is only known from the
    /// "TaskbarButtonCreated" message (the shell may drop the request, or the taskbar is not there yet).
    /// </summary>
    public static void Add(IntPtr hwnd)
    {
        try
        {
            var list = (ITaskbarList)new TaskbarList();
            try
            {
                list.HrInit();
                list.AddTab(hwnd);
                // For a tool window the button only shows up once "activated". Only our own button: marking
                // another program's window (whatever is in front) confuses the taskbar's grouping of the
                // windows that open afterwards. Our button looks active until the next window change.
                list.ActivateTab(hwnd);
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No taskbar (yet): asked again later
        }
    }

    const uint MSGFLT_ALLOW = 1;

    /// <summary>
    /// Lets the shell's messages through when pLaunch runs as administrator: Windows blocks registered
    /// messages from the (not elevated) Explorer to an elevated window, and the button would never be
    /// confirmed nor asked for again after Explorer restarts.
    /// </summary>
    public static void AllowShellMessages(IntPtr hwnd)
    {
        ChangeWindowMessageFilterEx(hwnd, (uint)TaskbarButtonCreatedMessage, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, (uint)TaskbarCreatedMessage, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, (uint)TrayIcon.CallbackMessage, MSGFLT_ALLOW, IntPtr.Zero); // clicks on the notification area icon
    }

    [DllImport("user32.dll")]
    static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    class TaskbarList { }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
