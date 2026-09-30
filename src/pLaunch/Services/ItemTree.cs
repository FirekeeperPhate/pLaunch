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

    /// <summary>True when <paramref name="candidate"/> is <paramref name="group"/> itself or somewhere inside it.</summary>
    public static bool IsSelfOrInside(LaunchItem candidate, LaunchItem group) =>
        candidate == group || (group.Children?.Any(c => IsSelfOrInside(candidate, c)) ?? false);

    /// <summary>
    /// The order shown in the popup. Custom = as stored. Alphabetical = each section between separators
    /// sorted on its own, sub-folders first; the stored (custom) order is left untouched.
    /// </summary>
    public static List<LaunchItem> DisplayOrder(IReadOnlyList<LaunchItem> items, SortMode sort)
    {
        if (sort == SortMode.Custom)
            return items.ToList();
        var result = new List<LaunchItem>(items.Count);
        var section = new List<LaunchItem>();
        void Flush()
        {
            result.AddRange(section
                .OrderBy(i => i.Kind == ItemKind.Group ? 0 : 1)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
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

    /// <summary>Number of launchable items in a group, sub-folders included.</summary>
    public static int CountLaunchables(LaunchItem group) =>
        group.Children?.Sum(c => c.Kind == ItemKind.Group ? CountLaunchables(c) : c.IsLaunchable ? 1 : 0) ?? 0;
}
