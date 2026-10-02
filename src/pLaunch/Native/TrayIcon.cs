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

    public enum Action
    {
        None,
        /// <summary>A click on the icon.</summary>
        Toggle,
        /// <summary>Enter or Space on the icon. The shell sends it twice for Enter: once is enough.</summary>
        ToggleByKey,
        /// <summary>A right click, the menu key.</summary>
        Menu,
        /// <summary>A click on the notification the icon showed.</summary>
        Open,
    }

    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const int NIIF_INFO = 0x1;
    const int NOTIFYICON_VERSION_4 = 4;
    const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_BALLOONUSERCLICK = 0x405, WM_CONTEXTMENU = 0x7B;
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
        data.szTip = Fit(tip, 127);
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

    /// <summary>A notification from the icon (a Windows toast). A click on it comes back as <see cref="Action.Open"/>.</summary>
    public void Notify(string title, string text)
    {
        if (!_shown)
            return;
        var data = NewData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Fit(title, 63);
        data.szInfo = Fit(text, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
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

    /// <summary>
    /// What a <see cref="CallbackMessage"/> asks for. The place is that of the icon, or of the click on it
    /// (screen pixels).
    /// </summary>
    public static (Action Action, NativeMethods.POINT At) Decode(IntPtr wParam, IntPtr lParam)
    {
        int notification = (int)(lParam.ToInt64() & 0xFFFF);
        long where = wParam.ToInt64();
        var at = new NativeMethods.POINT { X = (short)(where & 0xFFFF), Y = (short)((where >> 16) & 0xFFFF) };
        var action = notification switch
        {
            NIN_SELECT => Action.Toggle,
            NIN_KEYSELECT => Action.ToggleByKey,
            WM_CONTEXTMENU => Action.Menu,
            NIN_BALLOONUSERCLICK => Action.Open,
            _ => Action.None,
        };
        return (action, at);
    }

    static string Fit(string text, int max) => text.Length > max ? text[..max] : text;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
}
