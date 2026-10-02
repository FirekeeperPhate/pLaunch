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
        if (_tray.Show(TrayIconHandle(), Title))
        {
            _trayRetries = 0;
        }
        else if (++_trayRetries <= 10)
        {
            var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            retry.Tick += (_, _) =>
            {
                retry.Stop();
                if (IsLoaded && _tray is { IsShown: false })
                    ShowTrayIcon();
            };
            retry.Start();
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
        if (action == TrayIcon.Action.Toggle)
            Dispatcher.BeginInvoke(() => ToggleFromTray(at));
        else if (action == TrayIcon.Action.Menu)
            Dispatcher.BeginInvoke(ShowTrayMenu);
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
        {
            HidePopup();
            return;
        }
        // Beside the icon. One hidden in the overflow is above the taskbar: the list still goes against the
        // taskbar, at the icon's place along it.
        _openAt = TaskbarRect() is { } bar && bar.Width >= bar.Height
            ? new NativeMethods.POINT { X = at.X, Y = bar.Top + bar.Height / 2 }
            : at;
        ShowPopup();
    }

    void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(CreateMenuItem("Open", ShowPopup, "\xE8A7"));
        menu.Items.Add(CreateMenuItem("Settings\x2026", () => { ShowPopup(); OpenSettings(); }, "\xE713"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit", Close, "\xE711"));
        // A menu of a window that is not in front would stay open when clicking elsewhere
        WindowInterop.SetForegroundWindow(_hwnd);
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(PassFocusOn, DispatcherPriority.Background);
        menu.IsOpen = true;
    }

    void RemoveTrayIcon()
    {
        _tray?.Remove();
        DestroySymbolIcon();
        if (_extractedTrayIcon != IntPtr.Zero)
        {
            ShellInterop.DestroyIcon(_extractedTrayIcon);
            _extractedTrayIcon = IntPtr.Zero;
        }
    }

    bool IsOnTrayIcon(NativeMethods.POINT point) =>
        _tray != null && _tray.TryGetRect(out var r)
        && point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom;
}
