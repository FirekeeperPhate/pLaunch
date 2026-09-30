using System.Windows.Input;

namespace pLaunch.Services;

/// <summary>
/// A global shortcut: modifiers (at least one) plus a key, written "Ctrl+Alt+E" / "Win+Alt+Space".
/// </summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    // RegisterHotKey modifiers
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    /// <summary>Keys that cannot be the main key of a shortcut.</summary>
    static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None;

    /// <summary>A usable global shortcut: a real key and at least one of Ctrl, Alt, Win (Shift alone would steal typing).</summary>
    public bool IsValid => !IsModifierKey(Key)
        && (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None || !TryParseKey(raw, out key))
                        return false;
                    break;
            }
        }
        gesture = new HotkeyGesture(modifiers, key);
        return gesture.IsValid;
    }

    static bool TryParseKey(string text, out Key key)
    {
        // "1" -> D1, as the number row is shown
        if (text.Length == 1 && char.IsDigit(text[0]))
            return Enum.TryParse("D" + text, out key);
        return Enum.TryParse(text, ignoreCase: true, out key) && !IsModifierKey(key);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>What RegisterHotKey takes: modifier flags (no auto-repeat) and the virtual key.</summary>
    public (uint Modifiers, uint VirtualKey) ToNative()
    {
        uint mods = ModNoRepeat;
        if (Modifiers.HasFlag(ModifierKeys.Alt)) mods |= ModAlt;
        if (Modifiers.HasFlag(ModifierKeys.Control)) mods |= ModControl;
        if (Modifiers.HasFlag(ModifierKeys.Shift)) mods |= ModShift;
        if (Modifiers.HasFlag(ModifierKeys.Windows)) mods |= ModWin;
        return (mods, (uint)KeyInterop.VirtualKeyFromKey(Key));
    }
}
