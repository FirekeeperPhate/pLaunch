using System.Windows;
using System.Windows.Threading;
using pLaunch.Native;

namespace pLaunch;

// Something dragged onto the taskbar button and held there opens the list, whatever it is
public partial class PopupWindow
{
    DispatcherTimer? _dragWatch;
    readonly DragHoverDetector _dragHover = new();
    Task<List<Rect>>? _buttonsAtDragStart; // where pLaunch's buttons were, before the taskbar moved them
    bool _probing;

    /// <summary>
    /// Windows opens a window whose taskbar button something is dragged onto, but not when the dragged item
    /// can be pinned (a program, a shortcut to one): the taskbar offers "Pin to taskbar" instead, moving the
    /// buttons aside to make room under the pointer, and the list never opened. So pLaunch watches the
    /// pointer itself (see <see cref="DragHoverDetector"/>): a drag resting on the taskbar where pLaunch's
    /// button was when the drag began (or is now) opens the list.
    /// </summary>
    void StartDragWatch()
    {
        _dragWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => DragWatchTick(), Dispatcher);
        _dragWatch.Start();
    }

    async void DragWatchTick()
    {
        bool down = IsLeftButtonDown();
        _closingClick.Tick(down); // the press that closed the popup: still held, or over
        NativeMethods.POINT point = default;
        bool onTaskbar = down && NativeMethods.GetCursorPos(out point) && TaskbarHitTest.IsTaskbarAt(point);
        var title = Title;
        switch (_dragHover.Tick(down, point, onTaskbar, IsOpen))
        {
            case DragHoverDetector.Step.ReadButtons:
                _buttonsAtDragStart = Task.Run(() => TaskbarHitTest.ButtonsOf(title));
                break;
            case DragHoverDetector.Step.Probe when !_probing:
                _probing = true;
                try
                {
                    bool onButton = _buttonsAtDragStart is { } before && TaskbarHitTest.Contains(await before, point)
                        || TaskbarHitTest.Contains(await Task.Run(() => TaskbarHitTest.ButtonsOf(title)), point);
                    if (onButton && IsLeftButtonDown() && !IsOpen && IsLoaded)
                        ShowPopup(); // closes again by itself when the drop happens elsewhere (see OnPopupOpened)
                }
                finally
                {
                    _probing = false;
                }
                break;
        }
        if (!down)
            _buttonsAtDragStart = null;
    }
}
