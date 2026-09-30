using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>Operations on the tree of items (sub-folders are <see cref="ItemKind.Group"/> items with children).</summary>
public static class ItemTree
{
    public static LaunchItem? Find(List<LaunchItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Id == id)
                return item;
            if (item.Children != null && Find(item.Children, id) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>The list that directly contains the item with <paramref name="id"/>.</summary>
    public static List<LaunchItem>? FindContainer(List<LaunchItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Id == id)
                return items;
            if (item.Children != null && FindContainer(item.Children, id) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>The sub-folders from the top level down to (not including) the item; null if it is not in the tree.</summary>
    public static List<LaunchItem>? PathTo(IReadOnlyList<LaunchItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Id == id)
                return [];
            if (item.Children != null && PathTo(item.Children, id) is { } below)
            {
                below.Insert(0, item);
                return below;
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="group"/> itself or somewhere inside it.</summary>
    public static bool IsSelfOrInside(LaunchItem candidate, LaunchItem group) =>
        candidate == group || (group.Children?.Any(c => IsSelfOrInside(candidate, c)) ?? false);

    /// <summary>
    /// The order shown in the popup. Custom = as stored. Alphabetical / most used = each section between
    /// separators sorted on its own, sub-folders first; the stored (custom) order is left untouched.
    /// </summary>
    public static List<LaunchItem> DisplayOrder(IReadOnlyList<LaunchItem> items, SortMode sort)
    {
        if (sort == SortMode.Custom)
            return items.ToList();
        var result = new List<LaunchItem>(items.Count);
        var section = new List<LaunchItem>();
        void Flush()
        {
            var ordered = section.OrderBy(i => i.Kind == ItemKind.Group ? 0 : 1);
            if (sort == SortMode.MostUsed)
                ordered = ordered.ThenByDescending(i => i.LaunchCount).ThenByDescending(i => i.LastLaunched ?? DateTime.MinValue);
            result.AddRange(ordered.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
            section.Clear();
        }
        foreach (var item in items)
        {
            if (item.Kind == ItemKind.Separator)
            {
                Flush();
                result.Add(item);
            }
            else
            {
                section.Add(item);
            }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Launchable items in custom order with the category to show them under in the jump list:
    /// top-level ones under <paramref name="rootCategory"/>, the others under "Folder › Sub-folder".
    /// </summary>
    public static List<(LaunchItem Item, string Category)> Launchables(IReadOnlyList<LaunchItem> items, string rootCategory)
    {
        var result = new List<(LaunchItem, string)>();
        void Walk(IReadOnlyList<LaunchItem> level, string? path)
        {
            foreach (var item in level)
            {
                if (item.Kind == ItemKind.Group)
                    Walk(item.Children ?? [], path == null ? item.Name : path + " \x203A " + item.Name);
                else if (item.IsLaunchable)
                    result.Add((item, path ?? rootCategory));
            }
        }
        Walk(items, null);
        return result;
    }

    /// <summary>
    /// "3 shortcuts, 1 sub-folder" for what a group holds at any depth, or null when it holds nothing
    /// worth confirming (empty, or separators only).
    /// </summary>
    public static string? DescribeContent(LaunchItem group)
    {
        var (shortcuts, folders) = CountContent(group);
        var parts = new List<string>();
        if (shortcuts > 0)
            parts.Add(shortcuts == 1 ? "1 shortcut" : $"{shortcuts} shortcuts");
        if (folders > 0)
            parts.Add(folders == 1 ? "1 sub-folder" : $"{folders} sub-folders");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>Number of launchable items in a group, sub-folders included.</summary>
    public static int CountLaunchables(LaunchItem group) => CountContent(group).Shortcuts;

    /// <summary>What a group holds at any depth: shortcuts and sub-folders (separators do not count).</summary>
    public static (int Shortcuts, int Folders) CountContent(LaunchItem group)
    {
        int shortcuts = 0, folders = 0;
        foreach (var item in group.Children ?? [])
        {
            if (item.Kind == ItemKind.Group)
            {
                var (s, f) = CountContent(item);
                shortcuts += s;
                folders += f + 1;
            }
            else if (item.IsLaunchable)
            {
                shortcuts++;
            }
        }
        return (shortcuts, folders);
    }
}
