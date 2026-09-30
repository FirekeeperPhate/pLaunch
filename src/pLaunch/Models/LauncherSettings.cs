namespace pLaunch.Models;

public enum ViewMode
{
    /// <summary>One row per item: icon and name.</summary>
    List,
    /// <summary>Tiles: a big icon with the name below.</summary>
    Grid,
    /// <summary>Icons only, compact like the old Quick Launch (names in the tooltips).</summary>
    Icons,
}

public enum ItemSize { Small, Medium, Large }

public enum SortMode
{
    /// <summary>The order set by dragging.</summary>
    Custom,
    /// <summary>By name, sub-folders first, each section between separators sorted on its own.</summary>
    Alphabetical,
}

public enum ThemeChoice
{
    /// <summary>Light or dark like Windows (changes along with it).</summary>
    System,
    Light,
    Dark,
}

public sealed class LauncherSettings
{
    public ViewMode View { get; set; } = ViewMode.List;
    public ItemSize Size { get; set; } = ItemSize.Medium;
    public SortMode Sort { get; set; } = SortMode.Custom;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>
    /// "#RRGGBB" background of the popup, or null for the Windows acrylic. With a color the text turns
    /// light or dark by itself, whatever <see cref="Theme"/> says, so it stays readable.
    /// </summary>
    public string? Background { get; set; }
    /// <summary>A custom background lets a little of the acrylic show through; false = solid.</summary>
    public bool Translucent { get; set; } = true;
}
