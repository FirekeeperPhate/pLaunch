using System.Globalization;
using System.Windows.Media;
using pLaunch.Models;

namespace pLaunch.Services;

/// <summary>Background colors and the light/dark decision for the popup.</summary>
public static class Appearance
{
    /// <summary>Alpha of a translucent custom background over the acrylic.</summary>
    public const byte TranslucentAlpha = 0xD9; // 85 %

    public static readonly IReadOnlyList<(string Name, string Color)> Presets =
    [
        ("Graphite", "#202020"),
        ("Slate", "#2B3440"),
        ("Midnight", "#1E2A44"),
        ("Ocean", "#0F3D52"),
        ("Forest", "#1F3A2E"),
        ("Plum", "#33264A"),
        ("Wine", "#3D1F2B"),
        ("Paper", "#FAFAFA"),
        ("Sand", "#EDE3D1"),
        ("Sky", "#DCEBFA"),
        ("Mint", "#DDF0E6"),
        ("Rose", "#F6DDE4"),
    ];

    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>
    /// Whether the popup uses dark mode (light text): from the custom background when there is one
    /// (white or black text, whichever contrasts more), otherwise from the theme choice.
    /// </summary>
    public static bool IsDark(LauncherSettings settings, bool systemIsDark)
    {
        if (TryParse(settings.Background, out var color))
            return Luminance(color) < 0.18; // contrast with white beats contrast with black below ~0.18
        return settings.Theme switch
        {
            ThemeChoice.Light => false,
            ThemeChoice.Dark => true,
            _ => systemIsDark,
        };
    }
}
