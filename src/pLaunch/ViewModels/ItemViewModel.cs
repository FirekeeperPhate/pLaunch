using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch.ViewModels;

/// <summary>Where a drag would drop relative to an item: next to it, or into it (sub-folders).</summary>
public enum DropMarker { None, Before, After, Into }

public sealed class ItemViewModel(LaunchItem model) : INotifyPropertyChanged
{
    ImageSource? _icon;
    bool _isMissing, _isEditing;
    string _editName = "";
    DropMarker _dropMarker;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LaunchItem Model { get; } = model;

    public bool IsGroup => Model.Kind == ItemKind.Group;
    public bool IsSeparator => Model.Kind == ItemKind.Separator;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name != value)
            {
                Model.Name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public string ToolTip => Model.Kind switch
    {
        ItemKind.Group => $"{Model.Name} ({ChildInfo})",
        ItemKind.Separator => "",
        _ => $"{Model.Name}\n{(Model.Arguments is { Length: > 0 } args ? $"{Model.Target} {args}" : Model.Target)}",
    };

    /// <summary>Shortcuts inside a sub-folder (its own sub-folders included), shown next to its name.</summary>
    public string ChildInfo
    {
        get
        {
            int n = IsGroup ? ItemTree.CountLaunchables(Model) : 0;
            return n == 1 ? "1 item" : $"{n} items";
        }
    }

    /// <summary>After a move into or out of a sub-folder: its count changed.</summary>
    public void RefreshChildInfo()
    {
        OnPropertyChanged(nameof(ChildInfo));
        OnPropertyChanged(nameof(ToolTip));
    }

    /// <summary>Segoe Fluent Icons glyph shown until (or instead of) the shell icon.</summary>
    public string Glyph => Model.Kind switch
    {
        ItemKind.Url => "\xE774",    // Globe
        ItemKind.Folder => "\xE8B7", // Folder
        ItemKind.Group => "\xE8B7",  // Folder (drawn in the accent color)
        _ => "\xE8A5",               // Document
    };

    public ImageSource? Icon
    {
        get => _icon;
        set { if (Set(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); }
    }

    public bool HasIcon => _icon != null;

    /// <summary>Pixel size of <see cref="Icon"/> (0 = none yet): a DPI or size change asks for another one.</summary>
    public int IconPixels { get; set; }

    /// <summary>An icon request is running: no second one is queued (a network path may take long).</summary>
    public bool IconPending { get; set; }

    public bool IsMissing { get => _isMissing; set => Set(ref _isMissing, value); }

    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }

    public string EditName { get => _editName; set => Set(ref _editName, value); }

    public DropMarker DropMarker
    {
        get => _dropMarker;
        set
        {
            if (Set(ref _dropMarker, value))
            {
                OnPropertyChanged(nameof(DropBefore));
                OnPropertyChanged(nameof(DropAfter));
                OnPropertyChanged(nameof(DropInto));
            }
        }
    }

    public bool DropBefore => _dropMarker == DropMarker.Before;
    public bool DropAfter => _dropMarker == DropMarker.After;
    public bool DropInto => _dropMarker == DropMarker.Into;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
