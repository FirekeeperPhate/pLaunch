namespace pLaunch.Native;

/// <summary>
/// A click on the taskbar button of the open popup means "close". Windows makes two things of it: pressing
/// the button takes the focus away from the popup (which hides), and releasing it asks to restore the
/// window. That restore must not open the popup again: it comes right after the popup hid, or, for a slow
/// click, while the press that hid it is still held or was just released.
/// </summary>
internal sealed class ClosingClick
{
    /// <summary>How long after the popup hid by itself a restore still belongs to the same click.</summary>
    public static readonly TimeSpan Guard = TimeSpan.FromMilliseconds(400);

    /// <summary>Ticks of the watching timer (150 ms) a released press still counts: the restore follows the release at once.</summary>
    public const int ReleaseTicks = 2;

    DateTime _hiddenAt;
    bool _pressHeld;
    int _upTicks;

    /// <summary>The popup hid because it lost the focus; <paramref name="pressedOnTaskbar"/>: to a press on the taskbar.</summary>
    public void PopupHidden(DateTime now, bool pressedOnTaskbar)
    {
        _hiddenAt = now;
        _pressHeld = pressedOnTaskbar;
        _upTicks = 0;
    }

    /// <summary>From the timer that watches the left button: a press released a while ago is over.</summary>
    public void Tick(bool buttonDown)
    {
        if (!_pressHeld)
            return;
        _upTicks = buttonDown ? 0 : _upTicks + 1;
        if (_upTicks >= ReleaseTicks)
            _pressHeld = false;
    }

    /// <summary>The popup is opened on purpose (shortcut, command line): nothing to hold back.</summary>
    public void Forget()
    {
        _hiddenAt = default;
        _pressHeld = false;
    }

    /// <summary>Whether a restore asked for now is the second half of the click that closed the popup. Only once.</summary>
    public bool Take(DateTime now)
    {
        bool closing = _pressHeld || now - _hiddenAt < Guard;
        Forget();
        return closing;
    }
}
