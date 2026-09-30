using System.Windows;
using System.Windows.Shell;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>Mirrors the items in the taskbar button's jump list (right click), as "pLaunch --launch &lt;id&gt;" tasks.</summary>
public static class JumpListBuilder
{
    public static void Update(IEnumerable<LaunchItem> items)
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
            return;
        var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
        foreach (var item in items)
        {
            list.JumpItems.Add(new JumpTask
            {
                Title = item.Name,
                Description = item.Target,
                ApplicationPath = exe,
                Arguments = "--launch " + item.Id,
                IconResourcePath = IconResourceFor(item) ?? exe,
                CustomCategory = "Shortcuts",
            });
        }
        try
        {
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Explorer not ready yet (e.g. right after logon): the list is rebuilt on the next change
        }
    }

    /// <summary>A file whose first icon represents the item (jump lists take icon resources, not bitmaps).</summary>
    static string? IconResourceFor(LaunchItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Url:
                return NativeMethods.GetAssociatedExecutable("https");
            case ItemKind.Folder:
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            case ItemKind.File:
                var ext = Path.GetExtension(item.Target);
                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
                    return item.Target;
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    return ShortcutTarget(item.Target) is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : null;
                return ext.Length > 0 ? NativeMethods.GetAssociatedExecutable(ext) : null;
            default:
                return null;
        }
    }

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            var link = (NativeShellLink.IShellLinkW)new NativeShellLink.ShellLink();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
                var sb = new System.Text.StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
