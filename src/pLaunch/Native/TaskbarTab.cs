using System.Runtime.InteropServices;

namespace pLaunch.Native;

/// <summary>
/// A taskbar button without an entry in Alt+Tab and Win+Tab. Windows lists the same windows in both:
/// a tool window is left out of the switchers (and of the taskbar), and ITaskbarList::AddTab gives it
/// its button back. The shell drops such buttons when it restarts: they are added again on the
/// "TaskbarCreated" message.
/// </summary>
internal static class TaskbarTab
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    /// <summary>Sent to every top-level window when the taskbar is (re)created, e.g. after Explorer restarts.</summary>
    public static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    /// <summary>Out of Alt+Tab and Win+Tab (and of the taskbar, until <see cref="Add"/>).</summary>
    public static void HideFromSwitchers(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    const int WM_WINDOWPOSCHANGING = 0x0046;
    const int Offscreen = -32000; // where Windows parks minimized windows that have a button

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
        if ((pos.flags & SWP_NOMOVE) != 0 || (pos.x == Offscreen && pos.y == Offscreen))
            return;
        pos.x = Offscreen;
        pos.y = Offscreen;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    /// <summary>The window's minimized position, off screen from the start.</summary>
    public static void SetOffscreenMinimizedPosition(IntPtr hwnd)
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
            return;
        placement.flags |= WPF_SETMINPOSITION;
        placement.minX = Offscreen;
        placement.minY = Offscreen;
        SetWindowPlacement(hwnd, ref placement);
    }

    const int WPF_SETMINPOSITION = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd, minX, minY, maxX, maxY, left, top, right, bottom;
    }

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    /// <summary>Gives the window its taskbar button. False when the taskbar is not there (yet).</summary>
    public static bool Add(IntPtr hwnd)
    {
        try
        {
            var list = (ITaskbarList)new TaskbarList();
            try
            {
                list.HrInit();
                list.AddTab(hwnd);
                // For a tool window the button only shows up once "activated"; the window that is really
                // in front gets its highlight back right away
                list.ActivateTab(hwnd);
                var foreground = GetForegroundWindow();
                if (foreground != IntPtr.Zero && foreground != hwnd)
                    list.ActivateTab(foreground);
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

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

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
