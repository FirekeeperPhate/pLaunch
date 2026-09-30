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

        ProcessStartInfo psi;
        if (item.Kind == ItemKind.Shell)
        {
            // shell:AppsFolder\<AUMID> and ::{CLSID} names are resolved by Explorer
            psi = new ProcessStartInfo("explorer.exe", Quote(item.Target));
        }
        else
        {
            psi = new ProcessStartInfo(item.Target) { UseShellExecute = true, Arguments = item.Arguments ?? "" };
            // Start in the target's folder, like the old Quick Launch shortcuts: scripts (.bat, .cmd)
            // and many programs use relative paths. A .lnk keeps its own "Start in" if it has one.
            if (item.Kind == ItemKind.File)
                psi.WorkingDirectory = Path.GetDirectoryName(item.Target) ?? "";
            if (asAdmin)
                psi.Verb = "runas";
        }

        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
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
