using System.Runtime.InteropServices;

namespace pLaunch.Native;

/// <summary>
/// An icon in the notification area (the "tray") for a window: clicks come back to the window procedure as
/// <see cref="CallbackMessage"/>. Like the taskbar button, the shell forgets it when Explorer restarts
/// ("TaskbarCreated"): it is shown again then.
/// </summary>
internal sealed class TrayIcon
{
    /// <summary>The message the shell sends for the icon (WM_APP + 0x51).</summary>
    public const int CallbackMessage = 0x8000 + 0x51;

    public enum Action { None, Toggle, Menu }

    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    const int NOTIFYICON_VERSION_4 = 4;
    const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B;
    const uint Id = 1;

    readonly IntPtr _hwnd;
    bool _shown;

    public TrayIcon(IntPtr hwnd) => _hwnd = hwnd;

    public bool IsShown => _shown;

    /// <summary>Shows the icon, or changes the one shown. False when there is no notification area (yet).</summary>
    public bool Show(IntPtr icon, string tip)
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = icon;
        data.szTip = tip.Length > 127 ? tip[..127] : tip;
        if (_shown && Shell_NotifyIcon(NIM_MODIFY, ref data))
            return true;
        // Not there (any more): added. A leftover of a previous add with the same id is taken away first.
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _shown = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_shown)
        {
            data.uVersion = NOTIFYICON_VERSION_4; // clicks as NIN_SELECT / WM_CONTEXTMENU, with the place
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
        return _shown;
    }

    public void Remove()
    {
        if (!_shown)
            return;
        var data = NewData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _shown = false;
    }

    /// <summary>Explorer restarted: the icon is gone with it.</summary>
    public void Forget() => _shown = false;

    /// <summary>Where the icon is on screen; false when it is not shown (or hidden in the overflow).</summary>
    public bool TryGetRect(out NativeMethods.RECT rect)
    {
        rect = default;
        if (!_shown)
            return false;
        var id = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _hwnd, uID = Id };
        return Shell_NotifyIconGetRect(ref id, out rect) == 0;
    }

    /// <summary>
    /// What a <see cref="CallbackMessage"/> asks for: a click or Enter/Space on the icon toggles the popup,
    /// a right click (or the menu key) wants the menu. The place is that of the icon (screen pixels).
    /// </summary>
    public static (Action Action, NativeMethods.POINT At) Decode(IntPtr wParam, IntPtr lParam)
    {
        int notification = (int)(lParam.ToInt64() & 0xFFFF);
        long where = wParam.ToInt64();
        var at = new NativeMethods.POINT { X = (short)(where & 0xFFFF), Y = (short)((where >> 16) & 0xFFFF) };
        var action = notification switch
        {
            NIN_SELECT or NIN_KEYSELECT => Action.Toggle,
            WM_CONTEXTMENU => Action.Menu,
            _ => Action.None,
        };
        return (action, at);
    }

    NOTIFYICONDATA NewData() => new() { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = Id, szTip = "", szInfo = "", szInfoTitle = "" };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion; // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out NativeMethods.RECT rect);
}
