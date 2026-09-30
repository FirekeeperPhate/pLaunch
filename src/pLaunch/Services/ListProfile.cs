using System.Security.Principal;

namespace pLaunch.Services;

/// <summary>
/// Which list a pLaunch process shows. The default list is plain "pLaunch"; a named one runs as
/// "pLaunch --list Name" with its own file, taskbar button (its own AppUserModelID, so it can be pinned
/// separately), single-instance channel, jump list and autostart entry.
/// </summary>
public sealed class ListProfile
{
    public static readonly ListProfile Default = new(null);

    public const int MaxNameLength = 40;

    ListProfile(string? name) => Name = name;

    /// <summary>Null for the default list.</summary>
    public string? Name { get; }

    public bool IsDefault => Name == null;

    public static ListProfile Named(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"\"{name}\" is not a valid list name.", nameof(name));
        return new ListProfile(name.Trim());
    }

    /// <summary>For the command line: a missing or invalid name means the default list.</summary>
    public static ListProfile FromArgument(string? name) =>
        name != null && IsValidName(name) ? new ListProfile(name.Trim()) : Default;

    /// <summary>Letters, digits, spaces, '-' and '_': the name is also the file name and part of object names.</summary>
    public static bool IsValidName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= MaxNameLength
            && trimmed.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')
            && !trimmed.Equals("default", StringComparison.OrdinalIgnoreCase);
    }

    public string FilePath(string dataDirectory) => IsDefault
        ? Path.Combine(dataDirectory, "items.json")
        : Path.Combine(dataDirectory, "lists", Name + ".json");

    public static string ListsDirectory(string dataDirectory) => Path.Combine(dataDirectory, "lists");

    /// <summary>The named lists that exist in the data folder, by name.</summary>
    public static List<string> ExistingNames(string dataDirectory)
    {
        var dir = ListsDirectory(dataDirectory);
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(IsValidName)
            .Select(n => n!)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Explicit AppUserModelID of a named list; the default list keeps the one Windows derives from the exe.</summary>
    public string? AppUserModelId => IsDefault ? null : "Phate.pLaunch.List." + Name!.Replace(' ', '_');

    public string Title => IsDefault ? "pLaunch" : $"pLaunch \x2013 {Name}";

    /// <summary>Arguments that select this list, to put before others ("--list \"Work\" ").</summary>
    public string Arguments => IsDefault ? "" : $"--list \"{Name}\" ";

    public string AutostartValueName => IsDefault ? "pLaunch" : $"pLaunch ({Name})";

    /// <summary>Part of the single-instance mutex and pipe names: one running process per list and user.</summary>
    public string InstanceKey => IsDefault ? "" : "-" + Name!.Replace(' ', '_');

    public static string UserKey => WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
}
