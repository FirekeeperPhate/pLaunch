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
    /// <summary>A command line run by the Command Prompt or PowerShell (<see cref="LaunchItem.Target"/> holds it).</summary>
    Command,
    /// <summary>A text snippet: a click copies it (<see cref="LaunchItem.Target"/>) and can paste it into the active window.</summary>
    Text,
}

/// <summary>How the started window shows; <see cref="Hidden"/> only for commands (no console window at all).</summary>
public enum StartWindow { Normal, Minimized, Maximized, Hidden }

/// <summary>What runs a <see cref="ItemKind.Command"/>.</summary>
public enum CommandShell { Cmd, PowerShell, Pwsh }

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
    /// <summary>A folder of the disk that opens inside pLaunch, showing its current content ("live folder").</summary>
    public bool ShowContents { get; set; }
    /// <summary>How the program's window starts (programs may ignore it).</summary>
    public StartWindow StartWindow { get; set; }
    /// <summary>Global shortcut that launches the item without opening the popup ("Ctrl+Alt+E"); null = none.</summary>
    public string? Hotkey { get; set; }
    /// <summary>A program already running is brought to the front instead of being started again.</summary>
    public bool SwitchToRunning { get; set; }
    /// <summary>Commands: what runs them.</summary>
    public CommandShell Shell { get; set; }
    /// <summary>Commands: the console window stays open when the command ends.</summary>
    public bool KeepOpen { get; set; }
    /// <summary>Text snippets: pasted into the window that was active, not only copied.</summary>
    public bool PasteText { get; set; } = true;
    /// <summary>How often it was launched, for the "most used" order.</summary>
    public int LaunchCount { get; set; }
    public DateTime? LastLaunched { get; set; }
    /// <summary>The content of a <see cref="ItemKind.Group"/>, in custom order.</summary>
    public List<LaunchItem>? Children { get; set; }

    /// <summary>
    /// Made on the fly and never saved: an entry of a live folder (id "live:" + path, so the view keeps its
    /// icon across refreshes) or a suggestion of the search box (id "run:...", see RunSuggestions).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLive { get; init; }

    public bool IsLaunchable => Kind is not (ItemKind.Separator or ItemKind.Group);

    /// <summary>Opens inside the popup: sub-folders of pLaunch and live folders.</summary>
    public bool IsNavigable => Kind == ItemKind.Group || (Kind == ItemKind.Folder && ShowContents);

    /// <summary>Whether an icon is loaded for it: launchable items but snippets (a glyph), and anything with a custom icon.</summary>
    public bool HasShellIcon => Kind != ItemKind.Separator
        && (!string.IsNullOrWhiteSpace(IconPath) || (IsLaunchable && Kind != ItemKind.Text));

    /// <summary>The first line of a command or snippet, shortened: a name for it, and its tooltip/jump list text.</summary>
    public static string Summary(string text, int max = 60)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "\x2026";
    }

    /// <summary>Same thing to launch (used to skip duplicates when adding). Groups and separators never match.</summary>
    public bool IsSameTarget(LaunchItem other) =>
        IsLaunchable
        && Kind == other.Kind
        && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Arguments ?? "", other.Arguments ?? "", StringComparison.Ordinal);
}
