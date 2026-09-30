using System.Collections.Concurrent;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>A window of another program, as the taskbar sees it: its program file and taskbar identity.</summary>
public readonly record struct AppWindow(IntPtr Handle, string? ExePath, string? AppId);

/// <summary>What identifies an item's windows: its program file and/or its AppUserModelID.</summary>
public readonly record struct AppIdentity(string? ExePath, string? AppId)
{
    public bool Matches(AppWindow window) =>
        (ExePath != null && string.Equals(window.ExePath, ExePath, StringComparison.OrdinalIgnoreCase))
        || (AppId != null && string.Equals(window.AppId, AppId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Finds the open windows of an item's program: the running dot in the list, and "switch to it instead
/// of starting it again". Works for programs (.exe), shortcuts to them (.lnk) and Start menu apps.
/// </summary>
public static class RunningApps
{
    const string AppsFolderPrefix = "shell:AppsFolder\\";

    // Reading a .lnk costs a COM call: once per shortcut until the file changes
    static readonly ConcurrentDictionary<string, (DateTime Written, AppIdentity? Identity)> ShortcutCache =
        new(StringComparer.OrdinalIgnoreCase);

    static readonly object ScanLock = new();
    static (long Time, List<AppWindow> Windows)? _lastScan; // null until the first scan

    /// <summary>The windows of other programs, front to back, looked up now.</summary>
    public static List<AppWindow> Windows() => Windows(TimeSpan.Zero);

    /// <summary>
    /// The same, or the list of a scan at most <paramref name="maxAge"/> old: menus opened one after
    /// another while the pointer moves need not read every window's properties again.
    /// </summary>
    public static List<AppWindow> Windows(TimeSpan maxAge)
    {
        lock (ScanLock)
        {
            if (maxAge > TimeSpan.Zero && _lastScan is { } last && Environment.TickCount64 - last.Time <= (long)maxAge.TotalMilliseconds)
                return last.Windows;
        }
        var paths = new Dictionary<uint, string?>();
        var result = new List<AppWindow>();
        foreach (var (hwnd, pid) in WindowInterop.AppWindows())
        {
            if (!paths.TryGetValue(pid, out var exe))
                paths[pid] = exe = WindowInterop.ProcessPath(pid);
            result.Add(new AppWindow(hwnd, exe, ShellInterop.GetWindowAppId(hwnd)));
        }
        lock (ScanLock)
            _lastScan = (Environment.TickCount64, result);
        return result;
    }

    /// <summary>Whether pLaunch can tell the item's windows (programs and apps, not documents or links).</summary>
    public static bool CanSwitch(LaunchItem item) =>
        item.Kind == ItemKind.Shell ? item.Target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
        : item.Kind == ItemKind.File && Path.GetExtension(item.Target).ToLowerInvariant() is ".exe" or ".lnk";

    /// <summary>The item's program file and taskbar identity; null when it is not a program or app.</summary>
    public static AppIdentity? Identity(LaunchItem item)
    {
        if (!CanSwitch(item))
            return null;
        if (item.Kind == ItemKind.Shell)
        {
            var appId = item.Target[AppsFolderPrefix.Length..];
            return new AppIdentity(ExeFromAppId(appId), appId);
        }
        if (item.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return new AppIdentity(item.Target, null);
        if (Launcher.IsNetworkPath(item.Target))
            return null; // reading the shortcut could hang
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(item.Target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        if (ShortcutCache.TryGetValue(item.Target, out var cached) && cached.Written == written)
            return cached.Identity;
        var target = NativeShellLink.GetTarget(item.Target);
        var exe = target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
        var id = ShellInterop.GetShortcutAppId(item.Target);
        AppIdentity? identity = exe != null || id != null ? new AppIdentity(exe, id) : null;
        ShortcutCache[item.Target] = (written, identity);
        return identity;
    }

    /// <summary>
    /// Start menu ids of desktop programs are often a known folder id plus a relative path
    /// ("{6D809377-...}\Notepad++\notepad++.exe"): the program file, so its windows can be recognized.
    /// </summary>
    internal static string? ExeFromAppId(string appId, Func<Guid, string?>? knownFolder = null)
    {
        // "{" + 36 characters + "}" = 38, then the backslash
        if (appId.Length < 40 || appId[0] != '{' || appId[37] != '}' || appId[38] != '\\'
            || !appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(appId[..38], out var folderId))
            return null;
        var folder = (knownFolder ?? WindowInterop.KnownFolderPath)(folderId);
        return folder == null ? null : Path.Combine(folder, appId[39..]);
    }

    /// <summary>The item's windows among <paramref name="windows"/>, front to back.</summary>
    public static List<IntPtr> WindowsOf(LaunchItem item, IReadOnlyList<AppWindow> windows)
    {
        if (Identity(item) is not { } identity)
            return [];
        return windows.Where(identity.Matches).Select(w => w.Handle).ToList();
    }

    /// <summary>
    /// Brings the item's program to the front when it is running: its most recent window, or its oldest
    /// one when the most recent is already in front (cycling like the taskbar button). False = not running.
    /// </summary>
    public static bool TryActivate(LaunchItem item)
    {
        var windows = WindowsOf(item, Windows());
        if (windows.Count == 0)
            return false;
        var target = windows[0] == WindowInterop.GetForegroundWindow() && windows.Count > 1 ? windows[^1] : windows[0];
        if (WindowInterop.IsIconic(target))
            WindowInterop.ShowWindowAsync(target, WindowInterop.SW_RESTORE);
        // Refused only when pLaunch has no right to the foreground: the button flashes, nothing else starts
        WindowInterop.SetForegroundWindow(target);
        return true;
    }
}
