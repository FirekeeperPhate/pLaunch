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
}

public sealed class LaunchItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public ItemKind Kind { get; set; }

    public bool IsSameTarget(LaunchItem other) =>
        Kind == other.Kind
        && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Arguments ?? "", other.Arguments ?? "", StringComparison.Ordinal);
}
