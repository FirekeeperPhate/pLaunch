using System.Runtime.InteropServices;
using System.Text;

namespace pLaunch.Native;

internal static class NativeMethods
{
    // ---- Window management ----

    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_MAXIMIZE = 0xF030;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int ASFW_ANY = -1;
    public const int VK_LBUTTON = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int key);

    // ---- Monitors and taskbar ----

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public const uint ABM_GETTASKBARPOS = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    public static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    // ---- DWM ----

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_ROUND = 2;
    public const int DWMSBT_TRANSIENTWINDOW = 3;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static int SetDwmInt(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    // ---- Shell items and icons ----

    [Flags]
    public enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHCreateItemFromIDList(
        IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    public enum SIGDN : uint
    {
        NormalDisplay = 0x00000000,
        DesktopAbsoluteParsing = 0x80028000,
        FileSysPath = 0x80058000,
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetNameFromIDList(IntPtr pidl, SIGDN sigdn, out IntPtr name);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    public static string? GetNameFromIDList(IntPtr pidl, SIGDN sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out var ptr) != 0 || ptr == IntPtr.Zero)
            return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    // ---- GDI (HBITMAP -> pixels) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObject(IntPtr hObject, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    // ---- File associations ----

    public const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    public static string? GetAssociatedExecutable(string assoc)
    {
        uint length = 1024;
        var sb = new StringBuilder((int)length);
        return AssocQueryString(0, ASSOCSTR_EXECUTABLE, assoc, null, sb, ref length) == 0 ? sb.ToString() : null;
    }
}
