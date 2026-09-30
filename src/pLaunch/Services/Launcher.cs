using System.ComponentModel;
using System.Diagnostics;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch.Services;

public static class Launcher
{
    const int ErrorCancelled = 1223; // the user said no to the UAC prompt

    static readonly HashSet<string> ElevatableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".bat", ".cmd", ".msc",
    };

    public static bool CanRunAsAdmin(LaunchItem item) =>
        item.Kind == ItemKind.Command
        || (item.Kind == ItemKind.File && ElevatableExtensions.Contains(Path.GetExtension(item.Target)));

    public static bool HasLocation(LaunchItem item) => item.Kind is ItemKind.File or ItemKind.Folder;

    /// <summary>True when the file or folder is known to be gone. Network paths are not probed (they can hang).</summary>
    public static bool IsMissing(LaunchItem item)
    {
        if (item.Kind is not (ItemKind.File or ItemKind.Folder) || IsNetworkPath(item.Target))
            return false;
        return item.Kind == ItemKind.File ? !File.Exists(item.Target) : !Directory.Exists(item.Target);
    }

    /// <summary>
    /// UNC paths and mapped network drives: touching them can block for the length of an SMB timeout.
    /// GetDriveType (behind DriveInfo.DriveType) only reads the drive table, so it answers at once even
    /// for a disconnected drive.
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            return false;
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the item; returns false when the user cancelled an elevation prompt. A program set to
    /// "switch to it" is brought to the front when running, unless <paramref name="newWindow"/>. A text
    /// snippet is copied and, when <paramref name="paste"/> and the item say so, pasted into the window
    /// that gets the focus next.
    /// </summary>
    public static bool Launch(LaunchItem item, bool asAdmin = false, bool newWindow = false, bool paste = true)
    {
        if (item.Kind == ItemKind.Text)
        {
            CopyText(item.Target);
            if (paste && item.PasteText)
                LastPaste = PasteIntoActiveWindowAsync();
            return true;
        }
        if (item.SwitchToRunning && !asAdmin && !newWindow && RunningApps.TryActivate(item))
            return true;
        // Let the started app (or an already running one that receives the file/URL) take the foreground
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        try
        {
            Process.Start(CreateStartInfo(item, asAdmin))?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>The paste of the last snippet launched (tests wait for it).</summary>
    internal static Task LastPaste { get; private set; } = Task.CompletedTask;

    static void CopyText(string text)
    {
        try
        {
            // copy: true = the text stays on the clipboard after this process exits (jump list launches)
            // Saved with \n line ends; Windows programs expect \r\n on the clipboard
            System.Windows.Clipboard.SetDataObject(text.Replace("\r\n", "\n").Replace("\n", "\r\n"), copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            throw new InvalidOperationException("Another program is holding the clipboard. Try again.", ex);
        }
    }

    /// <summary>
    /// Ctrl+V into the window that has the focus once pLaunch is out of the way: after the keys of the
    /// shortcut that launched the snippet are released (Ctrl+Alt+V would be something else) and the popup
    /// has given the focus back. Never into pLaunch itself, the taskbar or the desktop.
    /// </summary>
    static Task PasteIntoActiveWindowAsync() => Task.Run(() =>
    {
        long deadline = Environment.TickCount64 + 3000;
        bool Ready()
        {
            var foreground = WindowInterop.GetForegroundWindow();
            if (foreground == IntPtr.Zero || WindowInterop.ModifiersDown())
                return false;
            WindowInterop.GetWindowThreadProcessId(foreground, out var pid);
            return pid != Environment.ProcessId;
        }
        while (!Ready())
        {
            if (Environment.TickCount64 > deadline)
                return;
            Thread.Sleep(20);
        }
        Thread.Sleep(80); // the window that just got the focus settles (its caret, its input field)
        if (Ready() && !WindowInterop.IsShellWindow(WindowInterop.GetForegroundWindow()))
            WindowInterop.SendPaste();
    });

    /// <summary>What runs a command: cmd.exe, Windows PowerShell or PowerShell 7.</summary>
    public static string CommandHost(CommandShell shell) => shell switch
    {
        CommandShell.PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
        CommandShell.Pwsh => PwshPath ?? "pwsh.exe",
        _ => Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec ? comspec : Path.Combine(Environment.SystemDirectory, "cmd.exe"),
    };

    static readonly Lazy<string?> Pwsh = new(() => RunSuggestions.ResolveCommand("pwsh.exe")
        ?? new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), @"PowerShell\7\pwsh.exe")).FirstOrDefault(File.Exists));

    /// <summary>PowerShell 7 (pwsh.exe), when installed.</summary>
    public static string? PwshPath => Pwsh.Value;

    /// <summary>
    /// The host's arguments for a command. cmd runs the lines one after the other ("a &amp; b"); /s keeps
    /// the quotes inside the command as they are. PowerShell gets it Base64-encoded, so no quoting can
    /// break it, and scripts may run whatever the execution policy says.
    /// </summary>
    internal static string CommandArguments(LaunchItem item)
    {
        var lines = item.Target.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (item.Shell == CommandShell.Cmd)
            return $"/s {(item.KeepOpen ? "/k" : "/c")} \"{string.Join(" & ", lines)}\"";
        var script = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(string.Join("\n", lines)));
        return $"-NoLogo {(item.KeepOpen ? "-NoExit " : "")}-ExecutionPolicy Bypass -EncodedCommand {script}";
    }

    static ProcessStartInfo CreateCommandStartInfo(LaunchItem item, bool asAdmin)
    {
        bool admin = asAdmin || item.RunAsAdmin;
        // Hidden and not elevated: no console at all (a console handed to Windows Terminal could still
        // show). An elevated one goes through the shell, which is asked to hide it.
        bool hidden = item.StartWindow == StartWindow.Hidden && !item.KeepOpen;
        var psi = new ProcessStartInfo(CommandHost(item.Shell), CommandArguments(item))
        {
            UseShellExecute = admin || !hidden,
            CreateNoWindow = hidden && !admin,
            WorkingDirectory = item.WorkingDirectory is { Length: > 0 } folder
                ? Environment.ExpandEnvironmentVariables(folder)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : WindowStyleOf(item.StartWindow),
        };
        if (admin)
            psi.Verb = "runas";
        return psi;
    }

    static ProcessWindowStyle WindowStyleOf(StartWindow window) => window switch
    {
        StartWindow.Minimized => ProcessWindowStyle.Minimized,
        StartWindow.Maximized => ProcessWindowStyle.Maximized,
        _ => ProcessWindowStyle.Normal,
    };

    internal static ProcessStartInfo CreateStartInfo(LaunchItem item, bool asAdmin = false)
    {
        // shell:AppsFolder\<AUMID> and ::{CLSID} names: the shell opens them itself. Much sooner than
        // through a new explorer.exe, which starts a whole process first (a Store app came up in 0.2-0.4 s
        // instead of 0.4-0.8 s).
        if (item.Kind == ItemKind.Shell)
            return new ProcessStartInfo(item.Target) { UseShellExecute = true };
        if (item.Kind == ItemKind.Command)
            return CreateCommandStartInfo(item, asAdmin);

        var psi = new ProcessStartInfo(item.Target) { UseShellExecute = true, Arguments = item.Arguments ?? "" };
        if (item.WorkingDirectory is { Length: > 0 } folder)
            psi.WorkingDirectory = Environment.ExpandEnvironmentVariables(folder);
        else if (item.Kind == ItemKind.File)
            // The target's folder, like the old Quick Launch shortcuts: scripts (.bat, .cmd) and many
            // programs use relative paths. A .lnk keeps its own "Start in" if it has one.
            psi.WorkingDirectory = Path.GetDirectoryName(item.Target) ?? "";
        if (asAdmin || (item.RunAsAdmin && CanRunAsAdmin(item)))
            psi.Verb = "runas";
        // Passed to the program as its first show command; some programs ignore it
        psi.WindowStyle = WindowStyleOf(item.StartWindow);
        return psi;
    }

    /// <summary>Counts a launch, for the "most used" order.</summary>
    public static void RecordLaunch(LaunchItem item)
    {
        item.LaunchCount++;
        item.LastLaunched = DateTime.UtcNow;
    }

    /// <summary>
    /// Launches several items (a selection, or everything in a sub-folder). Returns how many started and
    /// the errors of the others; an elevation prompt the user declined is not an error.
    /// </summary>
    public static (int Started, List<string> Errors) LaunchAll(IEnumerable<LaunchItem> items)
    {
        int started = 0;
        var errors = new List<string>();
        foreach (var item in items.Where(i => i.IsLaunchable))
        {
            if (IsMissing(item))
            {
                errors.Add($"{item.Name}: not found");
                continue;
            }
            try
            {
                // Several snippets pasted at once would race for the clipboard: they are only copied
                if (Launch(item, paste: false))
                {
                    RecordLaunch(item);
                    started++;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                errors.Add($"{item.Name}: {ex.Message}");
            }
        }
        return (started, errors);
    }

    public static void OpenLocation(LaunchItem item)
    {
        if (item.Kind == ItemKind.Folder)
            Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true })?.Dispose();
        else if (item.Kind == ItemKind.File)
            Process.Start("explorer.exe", "/select," + Quote(item.Target))?.Dispose();
    }

    static string Quote(string s) => "\"" + s + "\"";
}
