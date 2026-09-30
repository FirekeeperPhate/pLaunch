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
        item.Kind == ItemKind.File && ElevatableExtensions.Contains(Path.GetExtension(item.Target));

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

    /// <summary>Starts the item; returns false when the user cancelled an elevation prompt.</summary>
    public static bool Launch(LaunchItem item, bool asAdmin = false)
    {
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

    internal static ProcessStartInfo CreateStartInfo(LaunchItem item, bool asAdmin = false)
    {
        // shell:AppsFolder\<AUMID> and ::{CLSID} names are resolved by Explorer
        if (item.Kind == ItemKind.Shell)
            return new ProcessStartInfo("explorer.exe", Quote(item.Target));

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
        psi.WindowStyle = item.StartWindow switch
        {
            StartWindow.Minimized => ProcessWindowStyle.Minimized,
            StartWindow.Maximized => ProcessWindowStyle.Maximized,
            _ => ProcessWindowStyle.Normal,
        };
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
                if (Launch(item))
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
