using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using pLaunch.Models;
using pLaunch.Native;

namespace pLaunch.Services;

/// <summary>
/// Mirrors the items in the taskbar button's jump list (right click), as "pLaunch --launch &lt;id&gt;" tasks.
/// Updates are debounced and the icon lookups (shortcut targets, file associations) run off the UI thread,
/// cached per target, so saving a rename or a reorder costs nothing noticeable.
/// </summary>
public sealed class JumpListBuilder
{
    static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    readonly DispatcherTimer _timer;
    readonly ConcurrentDictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    Func<IReadOnlyList<LaunchItem>>? _source; // the whole tree
    int _generation;

    public JumpListBuilder()
    {
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            await ApplyAsync();
        };
    }

    /// <summary>
    /// Rebuilds the list shortly, from the items <paramref name="source"/> returns at that time: top-level
    /// shortcuts under "Shortcuts", those in sub-folders under the folder's name.
    /// </summary>
    public void Schedule(Func<IReadOnlyList<LaunchItem>> source)
    {
        _source = source;
        _timer.Stop();
        _timer.Start();
    }

    async Task ApplyAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe == null || _source == null)
            return;
        int generation = ++_generation;
        // Copies: the items may be renamed or removed while the icons are looked up
        var items = ItemTree.Launchables(_source(), "Shortcuts")
            .Select(e => (e.Item.Id, e.Item.Name, e.Item.Target, e.Item.Kind, e.Category)).ToList();
        var icons = await Task.Run(() => items.Select(i => IconResourceFor(i.Kind, i.Target) ?? exe).ToList());
        if (generation != _generation)
            return; // a newer update is on its way

        var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
        for (int i = 0; i < items.Count; i++)
        {
            list.JumpItems.Add(new JumpTask
            {
                Title = items[i].Name,
                Description = items[i].Target,
                ApplicationPath = exe,
                Arguments = "--launch " + items[i].Id,
                IconResourcePath = icons[i],
                CustomCategory = items[i].Category,
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
    string? IconResourceFor(ItemKind kind, string target)
    {
        var key = kind + "|" + target;
        if (_iconCache.TryGetValue(key, out var cached))
            return cached.Length > 0 ? cached : null;
        var icon = kind switch
        {
            ItemKind.Url => NativeMethods.GetAssociatedExecutable("https"),
            ItemKind.Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            // Network files are not opened (a .lnk would have to be read): the pLaunch icon stands in
            ItemKind.File when !Launcher.IsNetworkPath(target) => FileIcon(target),
            _ => null,
        };
        _iconCache[key] = icon ?? "";
        return icon;
    }

    static string? FileIcon(string target)
    {
        var ext = Path.GetExtension(target);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            return target;
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return ShortcutTarget(target) is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : null;
        return ext.Length > 0 ? NativeMethods.GetAssociatedExecutable(ext) : null;
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
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }
}
