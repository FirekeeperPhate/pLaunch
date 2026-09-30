using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using pLaunch.Models;

namespace pLaunch.ViewModels;

public enum DropMarker { None, Before, After }

public sealed class ItemViewModel(LaunchItem model) : INotifyPropertyChanged
{
    ImageSource? _icon;
    bool _iconLoaded, _isMissing, _isEditing;
    string _editName = "";
    DropMarker _dropMarker;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LaunchItem Model { get; } = model;

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); } }
    }

    public string ToolTip => Model.Arguments is { Length: > 0 } args ? $"{Model.Target} {args}" : Model.Target;

    /// <summary>Segoe Fluent Icons glyph shown until (or instead of) the shell icon.</summary>
    public string Glyph => Model.Kind switch
    {
        ItemKind.Url => "\xE774",    // Globe
        ItemKind.Folder => "\xE8B7", // Folder
        _ => "\xE8A5",               // Document
    };

    public ImageSource? Icon
    {
        get => _icon;
        set { if (Set(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); }
    }

    public bool HasIcon => _icon != null;

    /// <summary>Set once the icon lookup ran, even if it found nothing (then the glyph stays).</summary>
    public bool IconLoaded { get => _iconLoaded; set => _iconLoaded = value; }

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
            }
        }
    }

    public bool DropBefore => _dropMarker == DropMarker.Before;
    public bool DropAfter => _dropMarker == DropMarker.After;

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
