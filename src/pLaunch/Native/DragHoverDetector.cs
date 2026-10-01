namespace pLaunch.Native;

/// <summary>
/// Tells a drag held on the taskbar from everything else the left button does, from what a timer sees: the
/// button, where the pointer is, whether that is on a taskbar. A drag is a press away from the taskbar
/// (a press on it, a click or a hold on a button, is the taskbar's business) that moves; held still on the
/// taskbar for a few ticks, it is worth a look at what is under it.
/// </summary>
internal sealed class DragHoverDetector
{
    public enum Step
    {
        None,
        /// <summary>A drag began: where the taskbar buttons are now, before the taskbar moves them.</summary>
        ReadButtons,
        /// <summary>The drag rests on the taskbar: is it on the button? Once per resting place.</summary>
        Probe,
    }

    /// <summary>Ticks the pointer rests before <see cref="Step.Probe"/>: about half a second at 150 ms.</summary>
    public const int RestTicks = 3;

    /// <summary>How far the pointer goes with the button held before it is a drag, not a click (pixels).</summary>
    public const int DragDistance = 16;

    const int RestDistance = 8;

    NativeMethods.POINT? _pressedAt;
    bool _pressedOffTaskbar, _buttonsRead;
    NativeMethods.POINT? _restingAt;
    int _restTicks;

    public Step Tick(bool buttonDown, NativeMethods.POINT point, bool onTaskbar, bool popupOpen)
    {
        if (!buttonDown)
        {
            _pressedAt = null;
            _restingAt = null;
            _buttonsRead = false;
            return Step.None;
        }
        if (_pressedAt is not { } press)
        {
            _pressedAt = point;
            _pressedOffTaskbar = !onTaskbar;
            return Step.None;
        }
        if (!_pressedOffTaskbar || popupOpen)
        {
            _restingAt = null;
            return Step.None;
        }
        if (!_buttonsRead && Far(point, press, DragDistance))
        {
            _buttonsRead = true;
            return Step.ReadButtons;
        }
        if (!onTaskbar)
        {
            _restingAt = null;
            return Step.None;
        }
        if (_restingAt is not { } rest || Far(point, rest, RestDistance))
        {
            _restingAt = point;
            _restTicks = 0;
            return Step.None;
        }
        return ++_restTicks == RestTicks ? Step.Probe : Step.None;
    }

    static bool Far(NativeMethods.POINT a, NativeMethods.POINT b, int distance) =>
        Math.Abs(a.X - b.X) > distance || Math.Abs(a.Y - b.Y) > distance;
}
