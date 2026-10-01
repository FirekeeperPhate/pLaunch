using System.Windows;
using System.Windows.Threading;
using pLaunch.Native;

namespace pLaunch;

// Something dragged onto the taskbar button and held there opens the list, whatever it is
public partial class PopupWindow
{
    DispatcherTimer? _dragWatch;
    bool? _pressedOffTaskbar;           // where the left button went down: a drag comes from elsewhere
    Task<List<Rect>>? _buttonsAtPress;  // where pLaunch's buttons were then, before the taskbar moved them
    NativeMethods.POINT? _restingAt;    // the pointer held still on the taskbar, since _restTicks ticks
    int _restTicks;
    bool _probing;

    const int DragWatchMs = 150;
    const int RestTicks = 3; // about half a second, like the taskbar's own hover

    /// <summary>
    /// Windows opens a window whose taskbar button something is dragged onto, but not when the dragged item
    /// can be pinned (a program, a shortcut to one): the taskbar offers "Pin to taskbar" instead, moving the
    /// buttons aside to make room under the pointer, and the list never opened. So pLaunch watches the
    /// pointer itself while the popup is closed: the left button held since a press away from the taskbar,
    /// the pointer resting on the taskbar where pLaunch's button was when the press began (or is now).
    /// </summary>
    void StartDragWatch()
    {
        _dragWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(DragWatchMs), DispatcherPriority.Background, (_, _) => DragWatchTick(), Dispatcher);
        _dragWatch.Start();
    }

    async void DragWatchTick()
    {
        if (!IsLeftButtonDown() || !NativeMethods.GetCursorPos(out var point))
        {
            _pressedOffTaskbar = null;
            _buttonsAtPress = null;
            _restingAt = null;
            return;
        }
        bool onTaskbar = TaskbarHitTest.IsTaskbarAt(point);
        if (_pressedOffTaskbar == null)
        {
            // A press on the taskbar (a click or a hold on the button itself) is the taskbar's business
            _pressedOffTaskbar = !onTaskbar;
            if (!onTaskbar && !IsOpen)
            {
                var title = Title;
                _buttonsAtPress = Task.Run(() => TaskbarHitTest.ButtonsOf(title));
            }
        }
        if (_pressedOffTaskbar != true || !onTaskbar || IsOpen || _probing)
        {
            _restingAt = null;
            return;
        }
        if (_restingAt is not { } rest || Math.Abs(point.X - rest.X) > 8 || Math.Abs(point.Y - rest.Y) > 8)
        {
            _restingAt = point;
            _restTicks = 0;
            return;
        }
        if (++_restTicks != RestTicks)
            return; // not yet, or this spot was looked at already
        _probing = true;
        try
        {
            bool onButton = _buttonsAtPress is { } before && TaskbarHitTest.Contains(await before, point);
            if (!onButton)
            {
                var title = Title;
                onButton = TaskbarHitTest.Contains(await Task.Run(() => TaskbarHitTest.ButtonsOf(title)), point);
            }
            if (onButton && IsLeftButtonDown() && !IsOpen && IsLoaded)
                ShowPopup(); // closes again by itself when the drop happens elsewhere (see OnPopupOpened)
        }
        finally
        {
            _probing = false;
        }
    }
}
