using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch;

// Where the icon that opens the list is: a taskbar button, an icon in the notification area, or both
public partial class PopupWindow
{
    TrayIcon? _tray;
    IntPtr _extractedTrayIcon;      // an icon handle of our own, to be destroyed
    NativeMethods.POINT? _openAt;   // the next opening is anchored here, not at the pointer
    int _trayRetries;
    DispatcherTimer? _trayRetry;
    DateTime _lastKeyToggle;
    Version? _notifiedUpdate;       // told once with a notification

    bool WantsTaskbarButton => _settings.IconPlace != IconPlace.Tray;
    bool WantsTrayIcon => _settings.IconPlace != IconPlace.Taskbar;

    internal void SetIconPlace(IconPlace place)
    {
        _settings.IconPlace = place;
        Save();
        ApplyIconPlace();
    }

    /// <summary>Gives the window its taskbar button and its notification area icon, or takes them away.</summary>
    void ApplyIconPlace()
    {
        if (_hwnd == IntPtr.Zero || _taskbarButton == null)
            return;
        if (!WantsTaskbarButton)
            _taskbarButton.Withdraw();
        else if (!_taskbarButton.Wanted || !_taskbarButton.IsShown)
            _taskbarButton.Request();

        if (WantsTrayIcon)
            ShowTrayIcon();
        else
            _tray?.Remove();
    }

    /// <summary>
    /// Shows the icon (again: after Explorer restarts, or when the list's icon changed). At logon the
    /// notification area may not be there yet: tried again for a while.
    /// </summary>
    void ShowTrayIcon()
    {
        if (_hwnd == IntPtr.Zero || !WantsTrayIcon)
            return;
        _tray ??= new TrayIcon(_hwnd);
        if (_trayRetry == null)
        {
            _trayRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _trayRetry.Tick += (_, _) => ShowTrayIcon();
        }
        _trayRetry.Stop(); // one retry pending at most, whoever asks
        if (_tray.Show(TrayIconHandle(), TrayTip()))
            _trayRetries = 0;
        else if (IsLoaded && ++_trayRetries <= 10)
            _trayRetry.Start();
    }

    /// <summary>The icon's tooltip: the list's name, and that an update is waiting.</summary>
    string TrayTip() => _update is { } update ? $"{Title} - version {update.Version} is available" : Title;

    /// <summary>
    /// A new version was found. On the taskbar button a badge says so; with the icon in the notification
    /// area only, the icon says it: in its tooltip, and once with a notification (a click on it opens the
    /// list, where the update is offered).
    /// </summary>
    void TellUpdateInTray()
    {
        if (_tray is not { IsShown: true })
            return;
        ShowTrayIcon(); // the tooltip
        if (_update is { } update && !WantsTaskbarButton && _notifiedUpdate != update.Version)
        {
            _notifiedUpdate = update.Version;
            _tray.Notify(Title, $"Version {update.Version} is available. Open the list to install it.");
        }
    }

    internal void SetTrayIconStyle(TrayIconStyle style)
    {
        _settings.TrayIcon = style;
        Save();
        ShowTrayIcon();
    }

    /// <summary>
    /// The white or black symbol when chosen; else the window's own small icon (the list's, when one was
    /// chosen), else the program's.
    /// </summary>
    IntPtr TrayIconHandle()
    {
        if (_settings.TrayIcon != TrayIconStyle.Standard && SymbolIcon(_settings.TrayIcon) is var symbol && symbol != IntPtr.Zero)
            return symbol;
        const int WM_GETICON = 0x007F, ICON_SMALL = 0, ICON_SMALL2 = 2;
        var icon = NativeMethods.SendMessage(_hwnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero);
        if (icon == IntPtr.Zero)
            icon = NativeMethods.SendMessage(_hwnd, WM_GETICON, ICON_SMALL, IntPtr.Zero);
        if (icon != IntPtr.Zero || Environment.ProcessPath is not { } exe)
            return icon;
        if (_extractedTrayIcon == IntPtr.Zero)
            _extractedTrayIcon = ShellInterop.ExtractIcon(exe, 0, (int)Math.Round(16 * VisualTreeHelperDpi()));
        return _extractedTrayIcon;
    }

    (TrayIconStyle Style, IntPtr Handle) _symbolIcon;

    /// <summary>The symbol-only icon (Assets/pLaunchWhite.ico, pLaunchBlack.ico) at the notification area's size.</summary>
    IntPtr SymbolIcon(TrayIconStyle style)
    {
        if (_symbolIcon.Handle != IntPtr.Zero && _symbolIcon.Style == style)
            return _symbolIcon.Handle;
        DestroySymbolIcon();
        try
        {
            var uri = new Uri($"pack://application:,,,/pLaunch;component/Assets/pLaunch{style}.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream == null)
                return IntPtr.Zero;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            _symbolIcon = (style, IconFile.Load(bytes.ToArray(), NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON)));
        }
        catch (IOException)
        {
            return IntPtr.Zero;
        }
        return _symbolIcon.Handle;
    }

    void DestroySymbolIcon()
    {
        if (_symbolIcon.Handle != IntPtr.Zero)
            ShellInterop.DestroyIcon(_symbolIcon.Handle);
        _symbolIcon = default;
    }

    double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>The shell's messages about the icon, from the window procedure.</summary>
    void OnTrayMessage(IntPtr wParam, IntPtr lParam)
    {
        var (action, at) = TrayIcon.Decode(wParam, lParam);
        if (action != TrayIcon.Action.None)
            Dispatcher.BeginInvoke(() => OnTrayAction(action, at));
    }

    void OnTrayAction(TrayIcon.Action action, NativeMethods.POINT at)
    {
        // One of pLaunch's own dialogs is open (Settings, Properties, a question): that is what the icon
        // brings to the front. The list under it neither closes nor gets another dialog.
        if (_suppressHide > 0)
        {
            var dialog = WindowInterop.EnabledPopup(_hwnd);
            WindowInterop.SetForegroundWindow(dialog != IntPtr.Zero ? dialog : _hwnd);
            return;
        }
        switch (action)
        {
            case TrayIcon.Action.Toggle:
                ToggleFromTray(at);
                break;
            case TrayIcon.Action.ToggleByKey:
                // Enter comes twice (Space once): the second one must not close what the first one opened
                var now = DateTime.UtcNow;
                if (now - _lastKeyToggle > TimeSpan.FromMilliseconds(500))
                    ToggleFromTray(at);
                _lastKeyToggle = now;
                break;
            case TrayIcon.Action.Menu:
                ShowTrayMenu(at);
                break;
            case TrayIcon.Action.Open:
                if (!IsOpen)
                    OpenFromTray(at);
                break;
        }
    }

    /// <summary>
    /// A click on the icon: like the taskbar button, it opens the list or closes the open one. Pressing the
    /// icon of the open list already hid it (the popup lost the focus): that click is done (ClosingClick).
    /// </summary>
    void ToggleFromTray(NativeMethods.POINT at)
    {
        if (_closingClick.Take(DateTime.UtcNow))
            return;
        if (IsOpen)
            HidePopup();
        else
            OpenFromTray(at);
    }

    void OpenFromTray(NativeMethods.POINT at)
    {
        // Beside the icon. One hidden in the overflow is above the taskbar: the list still goes against the
        // taskbar, at the icon's place along it.
        _openAt = TaskbarRect() is { } bar && bar.Width >= bar.Height
            ? new NativeMethods.POINT { X = at.X, Y = bar.Top + bar.Height / 2 }
            : at;
        ShowPopup();
    }

    /// <summary>The icon's menu, at the icon (the place of the right click, or the icon's for the menu key).</summary>
    void ShowTrayMenu(NativeMethods.POINT at)
    {
        double scale = VisualTreeHelperDpi();
        var menu = new ContextMenu { Placement = PlacementMode.AbsolutePoint, HorizontalOffset = at.X / scale, VerticalOffset = at.Y / scale };
        menu.Items.Add(CreateMenuItem("Open", () => OpenFromTray(at), "\xE8A7"));
        menu.Items.Add(CreateMenuItem("Settings\x2026", () => { OpenFromTray(at); OpenSettings(); }, "\xE713"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit", Close, "\xE711"));
        // A menu of a window that is not in front would stay open when clicking elsewhere
        WindowInterop.SetForegroundWindow(_hwnd);
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(PassFocusOn, DispatcherPriority.Background);
        menu.IsOpen = true;
    }

    void RemoveTrayIcon()
    {
        _trayRetry?.Stop();
        _tray?.Remove();
        DestroySymbolIcon();
        if (_extractedTrayIcon != IntPtr.Zero)
        {
            ShellInterop.DestroyIcon(_extractedTrayIcon);
            _extractedTrayIcon = IntPtr.Zero;
        }
    }
}
