using System.Runtime.InteropServices;

namespace pLaunch.Services;

/// <summary>
/// System-wide shortcuts (RegisterHotKey) owned by a window: the one that opens the popup and those of
/// the items. <see cref="HandleMessage"/> must be called from the window's WndProc.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    readonly IntPtr _hwnd;
    readonly Dictionary<int, Action> _actions = [];
    int _nextId = 1;

    public GlobalHotkeys(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>False when the shortcut is invalid or taken (by another app or another list).</summary>
    public bool Register(HotkeyGesture gesture, Action action)
    {
        if (!gesture.IsValid)
            return false;
        var (mods, vk) = gesture.ToNative();
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods, vk))
            return false;
        _actions[id] = action;
        return true;
    }

    /// <summary>
    /// Whether a shortcut can be registered right now (tried and released at once). A shortcut this window
    /// already holds counts as taken, so release it first when checking a change.
    /// </summary>
    public bool IsAvailable(HotkeyGesture gesture)
    {
        if (!gesture.IsValid)
            return false;
        var (mods, vk) = gesture.ToNative();
        const int probeId = 0xBFFF;
        if (!RegisterHotKey(_hwnd, probeId, mods, vk))
            return false;
        UnregisterHotKey(_hwnd, probeId);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    /// <summary>Runs the action of a WM_HOTKEY; true when it was one of ours.</summary>
    public bool HandleMessage(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY || !_actions.TryGetValue((int)wParam, out var action))
            return false;
        action();
        return true;
    }

    public void Dispose() => UnregisterAll();
}
