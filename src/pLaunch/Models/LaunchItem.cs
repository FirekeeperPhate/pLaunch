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
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public ItemKind Kind { get; set; }
    /// <summary>The content of a <see cref="ItemKind.Group"/>, in custom order.</summary>
    public List<LaunchItem>? Children { get; set; }

    public bool IsLaunchable => Kind is not (ItemKind.Separator or ItemKind.Group);

    /// <summary>Same thing to launch (used to skip duplicates when adding). Groups and separators never match.</summary>
    public bool IsSameTarget(LaunchItem other) =>
        IsLaunchable
        && Kind == other.Kind
        && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Arguments ?? "", other.Arguments ?? "", StringComparison.Ordinal);
}
