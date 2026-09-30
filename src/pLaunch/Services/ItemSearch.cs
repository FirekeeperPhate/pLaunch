using System.Globalization;
using System.Text;
using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>Finds items by name in the whole list (every sub-folder), best matches first.</summary>
public static class ItemSearch
{
    public const int MaxResults = 60;

    /// <summary>
    /// Shortcuts and sub-folders whose name (or target file name) contains every word of the query,
    /// ignoring case and accents. Ranked: name starts with the query, then a word of the name starts with
    /// it, then anywhere; more launched first within a rank. Each result comes with its folder path.
    /// </summary>
    public static List<(LaunchItem Item, string Path)> Find(IReadOnlyList<LaunchItem> root, string query)
    {
        var q = Normalize(query);
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        var found = new List<(LaunchItem Item, string Path, int Rank)>();
        void Walk(IEnumerable<LaunchItem> level, string path)
        {
            foreach (var item in level)
            {
                if (item.Kind == ItemKind.Separator)
                    continue;
                var name = Normalize(item.Name);
                var file = item.Kind is ItemKind.File or ItemKind.Folder ? Normalize(System.IO.Path.GetFileNameWithoutExtension(item.Target)) : "";
                if (words.All(w => name.Contains(w) || file.Contains(w)))
                {
                    int rank = name.StartsWith(q) ? 0
                        : name.Split(' ', '-', '_', '.').Any(part => part.StartsWith(words[0])) ? 1
                        : 2;
                    found.Add((item, path, rank));
                }
                if (item.Kind == ItemKind.Group)
                    Walk(item.Children ?? [], path.Length == 0 ? item.Name : path + " \x203A " + item.Name);
            }
        }
        Walk(root, "");
        return found
            .OrderBy(f => f.Rank)
            .ThenByDescending(f => f.Item.LaunchCount)
            .ThenBy(f => f.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .Select(f => (f.Item, f.Path))
            .ToList();
    }

    /// <summary>Lower case without accents ("Città" -> "citta"), so typing is forgiving.</summary>
    internal static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).Trim();
    }
}
