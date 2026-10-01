using System.Windows.Media;
using pLaunch.Models;
using pLaunch.Services;

namespace pLaunch.Tests;

public class AppearanceTests
{
    [Theory]
    [InlineData("#1E2A44", 0x1E, 0x2A, 0x44)]
    [InlineData("#fafafa", 0xFA, 0xFA, 0xFA)]
    public void Colors_ParseAndFormat(string hex, byte r, byte g, byte b)
    {
        Assert.True(Appearance.TryParse(hex, out var color));
        Assert.Equal(Color.FromRgb(r, g, b), color);
        Assert.Equal(hex.ToUpperInvariant(), Appearance.ToHex(color));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1E2A44")]
    [InlineData("#1E2A4")]
    [InlineData("#GGGGGG")]
    public void BadColors_AreRejected(string? hex)
    {
        Assert.False(Appearance.TryParse(hex, out _));
    }

    [Fact]
    public void Presets_GetReadableText()
    {
        // Every preset must pick the text color with the better contrast
        foreach (var (name, hex) in Appearance.Presets)
        {
            Appearance.TryParse(hex, out var color);
            double l = Appearance.Luminance(color);
            bool dark = Appearance.IsDark(new LauncherSettings { Background = hex }, systemIsDark: false);
            double contrastWithWhite = 1.05 / (l + 0.05), contrastWithBlack = (l + 0.05) / 0.05;
            Assert.True(dark == contrastWithWhite > contrastWithBlack, name);
            Assert.True(Math.Max(contrastWithWhite, contrastWithBlack) >= 7, name); // WCAG AAA
        }
    }

    [Fact]
    public void CustomBackground_OverridesTheThemeChoice()
    {
        Assert.True(Appearance.IsDark(new LauncherSettings { Theme = ThemeChoice.Light, Background = "#1E2A44" }, false));
        Assert.False(Appearance.IsDark(new LauncherSettings { Theme = ThemeChoice.Dark, Background = "#EDE3D1" }, true));
    }

    [Theory]
    [InlineData(ThemeChoice.System, false, false)]
    [InlineData(ThemeChoice.System, true, true)]
    [InlineData(ThemeChoice.Light, true, false)]
    [InlineData(ThemeChoice.Dark, false, true)]
    public void Acrylic_FollowsTheThemeChoice(ThemeChoice theme, bool systemDark, bool expected)
    {
        Assert.Equal(expected, Appearance.IsDark(new LauncherSettings { Theme = theme }, systemDark));
    }

    // Pixels as the shell hands them: B, G, R, A
    [Fact]
    public void ShellIcons_KeepTheirAlpha()
    {
        // A soft shadow (white at 25 %), a dark translucent pixel, an opaque and a transparent one
        byte[] icon = [255, 255, 255, 64, 40, 40, 40, 128, 0, 0, 200, 255, 0, 0, 0, 0];
        var before = (byte[])icon.Clone();
        IconProvider.OpaqueWithoutAlpha(icon);
        Assert.Equal(before, icon);
    }

    [Fact]
    public void OldIcons_WithoutAlpha_BecomeOpaque()
    {
        byte[] old = [10, 20, 30, 0, 0, 0, 0, 0];
        IconProvider.OpaqueWithoutAlpha(old);
        Assert.Equal([10, 20, 30, 255, 0, 0, 0, 255], old);
    }
}
