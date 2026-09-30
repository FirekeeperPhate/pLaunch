using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>The current content of a folder of the disk, as items that are shown but never saved.</summary>
public static class LiveFolder
{
    public const int MaxEntries = 500;

    /// <summary>
    /// Folders first, then files, each by name; hidden and system entries left out. At most
    /// <see cref="MaxEntries"/> entries (<c>truncated</c> says whether there were more).
    /// </summary>
    public static (List<LaunchItem> Items, bool Truncated) List(string folder)
    {
        var dir = new DirectoryInfo(folder);
        var entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        });
        var folders = new List<LaunchItem>();
        var files = new List<LaunchItem>();
        bool truncated = false;
        foreach (var entry in entries)
        {
            if (folders.Count + files.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }
            if (entry is DirectoryInfo)
            {
                folders.Add(new LaunchItem("live:" + entry.FullName)
                {
                    Kind = ItemKind.Folder,
                    Name = entry.Name,
                    Target = entry.FullName,
                    ShowContents = true, // sub-folders of a live folder open inside too
                    IsLive = true,
                });
            }
            else
            {
                files.Add(new LaunchItem("live:" + entry.FullName)
                {
                    Kind = ItemKind.File,
                    Name = ItemFactory.FileDisplayName(entry.FullName),
                    Target = entry.FullName,
                    IsLive = true,
                });
            }
        }
        var byName = StringComparer.CurrentCultureIgnoreCase;
        folders.Sort((a, b) => byName.Compare(a.Name, b.Name));
        files.Sort((a, b) => byName.Compare(a.Name, b.Name));
        return ([.. folders, .. files], truncated);
    }
}
