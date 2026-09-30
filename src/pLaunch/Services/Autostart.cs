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

    /// <summary>Points an existing entry at the current exe (the app may have been moved or updated).</summary>
    public static void Refresh()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command)
            key.SetValue(ValueName, Command);
    }
}
