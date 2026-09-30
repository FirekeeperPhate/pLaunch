using Microsoft.Win32;

namespace pLaunch.Services;

/// <summary>"Start with Windows" through the per-user Run key; pLaunch then starts minimized.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "pLaunch";

    static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Points an existing entry at the current exe when its own exe is gone (the app was moved).
    /// A working entry is left alone: a second copy (a dev build) must not take over the installed one.
    /// </summary>
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string current || current == Command || File.Exists(ExePath(current)))
                return;
            using var writable = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            writable?.SetValue(ValueName, Command);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Run key locked by policy: startup must go on, the entry just stays as it is
        }
    }

    /// <summary>The exe of a Run command line: the quoted part, or everything before " --".</summary>
    internal static string ExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command[1..];
        }
        int options = command.IndexOf(" --", StringComparison.Ordinal);
        return options > 0 ? command[..options] : command;
    }
}
