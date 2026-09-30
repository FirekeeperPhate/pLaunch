using System.Windows.Threading;

namespace pLaunch.Native;

/// <summary>
/// The taskbar button of a window left out of Alt+Tab and Win+Tab (see <see cref="TaskbarTab"/>), for its
/// whole life: asked for, confirmed by the shell, asked for again after Explorer restarts, taken away when
/// the window goes; and the minimized window is kept off screen. The window procedure hands every message
/// to <see cref="HandleMessage"/>.
/// </summary>
internal sealed class TaskbarButton
{
    const int WM_DESTROY = 0x0002;

    readonly IntPtr _hwnd;
    readonly Action<IntPtr> _add, _remove;
    readonly DispatcherTimer _timer = new();
    int _attempt;
    bool _closed; // the window is gone: nothing may be asked of the shell for it any more

    /// <summary>
    /// The waits before each request. The shell handles a new window asynchronously and silently drops a
    /// button given too early: requests go on (a slow logon, a taskbar not answering yet) until it says
    /// the button exists, usually after the first one. Each request briefly shows the button as active.
    /// </summary>
    internal TimeSpan[] Delays { get; init; } = [.. new[] { 300, 1500, 4000, 10000, 20000, 30000 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    /// <summary>The shell said the button exists ("TaskbarButtonCreated").</summary>
    public bool IsShown { get; private set; }

    /// <summary>For a real window: out of the switchers now, the button comes with <see cref="Request"/>.</summary>
    public static TaskbarButton For(IntPtr hwnd)
    {
        TaskbarTab.HideFromSwitchers(hwnd);
        TaskbarTab.AllowShellMessages(hwnd);
        return new TaskbarButton(hwnd, TaskbarTab.Add, TaskbarTab.Remove);
    }

    internal TaskbarButton(IntPtr hwnd, Action<IntPtr> add, Action<IntPtr> remove)
    {
        _hwnd = hwnd;
        _add = add;
        _remove = remove;
        _timer.Tick += (_, _) => AskAgain();
    }

    /// <summary>Asks for the button until the shell confirms it (never for a closed window).</summary>
    public void Request()
    {
        if (_closed)
            return;
        _attempt = 0;
        _timer.Interval = Delays[0];
        _timer.Start();
    }

    void AskAgain()
    {
        _timer.Stop();
        if (_closed || IsShown)
            return;
        _add(_hwnd);
        if (++_attempt < Delays.Length)
        {
            _timer.Interval = Delays[_attempt];
            _timer.Start();
        }
    }

    public void HandleMessage(int msg, IntPtr lParam)
    {
        if (msg == TaskbarTab.TaskbarButtonCreatedMessage)
        {
            IsShown = true;
            _timer.Stop();
        }
        else if (msg == TaskbarTab.TaskbarCreatedMessage)
        {
            IsShown = false; // Explorer restarted: the shell forgot the button it was given
            Request();
        }
        else if (msg == WM_DESTROY)
        {
            // A button that was asked for is not always removed by the shell with its window: it could stay
            _closed = true;
            _timer.Stop();
            _remove(_hwnd);
        }
        TaskbarTab.KeepMinimizedOffscreen(_hwnd, msg, lParam); // no little title bar above the taskbar
    }
}
