namespace pLaunch.Models;

public enum ItemKind
{
    /// <summary>A file of any type: program, shortcut, document.</summary>
    File,
    Folder,
    /// <summary>A URI opened by its registered handler (http, https, mailto, ...).</summary>
    Url,
    /// <summary>A shell namespace item without a file system path, e.g. shell:AppsFolder\&lt;AUMID&gt; for Store apps.</summary>
    Shell,
    /// <summary>A dividing line; in alphabetical order it also splits the list into sections sorted separately.</summary>
    Separator,
    /// <summary>A pLaunch sub-folder holding other items (<see cref="LaunchItem.Children"/>), opened inside the popup.</summary>
    Group,
}

public sealed class LaunchItem
{
    /// <summary>A new item: a fresh random id.</summary>
    public LaunchItem() => Id = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Used when reading items.json: an entry without "Id" gets an empty one (not a random one), so
    /// ItemStore can repair it the same way in every process (the jump list launches items by id).
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public LaunchItem(string? id) => Id = id ?? "";

    public string Id { get; set; }
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public ItemKind Kind { get; set; }
    /// <summary>Folder to start in (environment variables allowed); null = the target's folder.</summary>
    public string? WorkingDirectory { get; set; }
    /// <summary>Always start elevated (programs, shortcuts and scripts only).</summary>
    public bool RunAsAdmin { get; set; }
    /// <summary>
    /// Custom icon: an .ico/.png file, or an .exe/.dll with <see cref="IconIndex"/>; null = the target's own
    /// icon. Sub-folders can have one too.
    /// </summary>
    public string? IconPath { get; set; }
    public int IconIndex { get; set; }
    /// <summary>The content of a <see cref="ItemKind.Group"/>, in custom order.</summary>
    public List<LaunchItem>? Children { get; set; }

    public bool IsLaunchable => Kind is not (ItemKind.Separator or ItemKind.Group);

    /// <summary>Whether an icon is shown for it: launchable items, and sub-folders with a custom icon.</summary>
    public bool HasShellIcon => IsLaunchable || (Kind == ItemKind.Group && !string.IsNullOrWhiteSpace(IconPath));

    /// <summary>Same thing to launch (used to skip duplicates when adding). Groups and separators never match.</summary>
    public bool IsSameTarget(LaunchItem other) =>
        IsLaunchable
        && Kind == other.Kind
        && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Arguments ?? "", other.Arguments ?? "", StringComparison.Ordinal);
}
