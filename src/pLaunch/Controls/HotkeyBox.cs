using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using pLaunch.Services;

namespace pLaunch.Controls;

/// <summary>
/// Records a shortcut: click it and press the keys (Ctrl/Alt/Shift + a key). The Win key cannot be caught
/// this way (Windows takes most Win+key combinations first), so it comes from a separate check box
/// (<see cref="UseWin"/>). Backspace or Delete alone clears it.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    ModifierKeys _modifiers;
    Key _key;
    bool _useWin;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        // A TextBox subclass does not get the theme's TextBox style by itself
        SetResourceReference(StyleProperty, typeof(TextBox));
        VerticalContentAlignment = VerticalAlignment.Center;
        UpdateText();
    }

    public event EventHandler? GestureChanged;

    /// <summary>Adds Win to the recorded keys (e.g. Win+Alt+Space).</summary>
    public bool UseWin
    {
        get => _useWin;
        set
        {
            if (_useWin == value)
                return;
            _useWin = value;
            UpdateText();
            GestureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>"Ctrl+Alt+E", or null when empty (or not a usable shortcut).</summary>
    public string? Gesture
    {
        get
        {
            var gesture = new HotkeyGesture(_modifiers | (_useWin ? ModifierKeys.Windows : 0), _key);
            return gesture.IsValid ? gesture.ToString() : null;
        }
        set
        {
            if (HotkeyGesture.TryParse(value, out var gesture))
            {
                _useWin = gesture.Modifiers.HasFlag(ModifierKeys.Windows);
                _modifiers = gesture.Modifiers & ~ModifierKeys.Windows;
                _key = gesture.Key;
            }
            else
            {
                _modifiers = ModifierKeys.None;
                _key = Key.None;
                _useWin = false;
            }
            UpdateText();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers & ~ModifierKeys.Windows;
        // Tab alone keeps moving the focus
        if (key == Key.Tab && modifiers == ModifierKeys.None)
            return;
        e.Handled = true; // Alt+Space would open the window menu, Alt+F4 would close the dialog
        if (key is Key.Back or Key.Delete or Key.Escape && modifiers == ModifierKeys.None)
        {
            _modifiers = ModifierKeys.None;
            _key = Key.None;
        }
        else if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                 or Key.LWin or Key.RWin)
        {
            return; // wait for the real key
        }
        else
        {
            _modifiers = modifiers;
            _key = key;
        }
        UpdateText();
        GestureChanged?.Invoke(this, EventArgs.Empty);
    }

    void UpdateText()
    {
        var gesture = new HotkeyGesture(_modifiers | (_useWin ? ModifierKeys.Windows : 0), _key);
        Text = _key == Key.None ? "None: click, then press the keys"
            : gesture.IsValid ? gesture.ToString()
            : gesture + "  (add Ctrl, Alt or Win)";
    }
}
