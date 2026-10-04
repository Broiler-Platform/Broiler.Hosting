using System;
using Broiler.Graphics.Color;
using Broiler.Hosting.Windows;
using Broiler.UI;
using Broiler.UI.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

public sealed class WindowsThemeTests
{
    // The Windows 11 contrast themes: Window, WindowText, Highlight, HighlightText, ButtonFace, ButtonText,
    // GrayText, HotLight, as the [Control Panel\Colors] sections of the theme files Windows 11 ships in
    // %SystemRoot%\Resources\Ease of Access Themes give them (hcblack.theme is Aquatic, hcwhite.theme Desert,
    // hc1.theme Dusk, hc2.theme Night sky).
    public static TheoryData<string, uint[], bool> ContrastThemes => new()
    {
        { "Aquatic", [0x202020, 0xFFFFFF, 0x8EE3F0, 0x263B50, 0x202020, 0xFFFFFF, 0xA6A6A6, 0x75E9FC], true },
        { "Desert", [0xFFFAEF, 0x3D3D3D, 0x903909, 0xFFF5E3, 0xFFFAEF, 0x202020, 0x676767, 0x1C5E75], false },
        { "Dusk", [0x2D3236, 0xFFFFFF, 0xA1BFDE, 0x212D3B, 0x2D3236, 0xB6F6F0, 0xA6A6A6, 0x70EBDE], true },
        { "Night sky", [0x000000, 0xFFFFFF, 0xD6B4FD, 0x2B2B2B, 0x000000, 0xFFEE32, 0xA6A6A6, 0x8080FF], true },
    };

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    public void The_Named_Contrast_Themes_Are_The_Windows_Tables(string name, uint[] rgb, bool dark)
    {
        var named = name switch
        {
            "Aquatic" => WindowsSystemColors.Aquatic,
            "Desert" => WindowsSystemColors.Desert,
            "Dusk" => WindowsSystemColors.Dusk,
            "Night sky" => WindowsSystemColors.NightSky,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
        Assert.Equal(Colors(rgb), named);
        Assert.Equal(dark, WindowsTheme.CreateHighContrastTheme(named).IsDark);
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("125", 1.0)]
    [InlineData(100, 1.0)]
    [InlineData(125, 1.25)]
    [InlineData(225, 2.25)]
    [InlineData(300, 2.25)]
    [InlineData(50, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(-5, 1.0)]
    public void TextScale_Is_Parsed_From_The_Percentage_And_Clamped(object? registryValue, double expected) =>
        Assert.Equal(expected, WindowsTheme.ParseTextScale(registryValue), 3);

    [Fact]
    public void System_Settings_Report_The_Queried_Text_Scale()
    {
        double scale = WindowsTheme.QueryTextScale();
        Assert.InRange(scale, 1.0, 2.25);
        Assert.Equal(scale, WindowsTheme.QuerySystemSettings().TextScale);
    }

    [Fact]
    public void ColorRef_Is_Read_As_Blue_Green_Red()
    {
        var color = WindowsSystemColors.FromColorRef(0x00336699);
        Assert.Equal((0x99, 0x66, 0x33, 0xFF), (color.R, color.G, color.B, color.A));
    }

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    public void High_Contrast_Palette_Uses_System_Colors_And_Stays_Readable(string name, uint[] rgb, bool dark)
    {
        var colors = Colors(rgb);
        var settings = UiSystemSettings.Default with { ContrastPreference = UiContrastPreference.More, ReducedMotion = true };
        var tokens = WindowsTheme.CreateHighContrastTheme(colors, settings);

        Assert.Equal("HighContrastSystem", tokens.Name);
        Assert.Equal(dark, tokens.IsDark);
        Assert.Equal(colors.Window, tokens.Surface);
        Assert.Equal(colors.WindowText, tokens.Text);
        Assert.Equal(tokens.Text, tokens.TextMuted);
        Assert.Equal(colors.Highlight, tokens.Accent);
        Assert.Equal(colors.HighlightText, tokens.OnAccent);
        Assert.Equal(colors.Highlight, tokens.AccentSoft);
        Assert.Equal(colors.HighlightText, tokens.SelectionText);
        Assert.Equal(colors.HighlightText, tokens.SelectionTextMuted);
        Assert.True(tokens.IsHighContrast);
        Assert.True(tokens.ReducedMotion);
        Assert.True(WindowsTheme.ContrastRatio(tokens.Text, tokens.Surface) >= 4.5, $"{name}: text");
        Assert.True(WindowsTheme.ContrastRatio(tokens.OnAccent, tokens.Accent) >= 4.5, $"{name}: accent");
        Assert.True(WindowsTheme.ContrastRatio(tokens.FocusRing, tokens.Surface) >= 3, $"{name}: focus ring");
        Assert.True(WindowsTheme.ContrastRatio(tokens.Info, tokens.Surface) >= 4.5, $"{name}: link");
    }

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    public void Text_Selection_Status_And_Focus_Colors_Are_Readable_In_The_Windows_Contrast_Themes(string name, uint[] rgb, bool dark)
    {
        // Not covered, because the palette cannot meet them yet: the control states Broiler.UI draws on the
        // AccentSoft selection fill (a hovered secondary button, a checked toggle button, a hovered spin box
        // arrow), which wait for a state-fill role in Broiler.UI. See CreateHighContrastTheme's remarks.
        var tokens = WindowsTheme.CreateHighContrastTheme(Colors(rgb));
        Assert.Equal(dark, tokens.IsDark);

        // Text: 4.5:1 (WCAG AA for normal text) on the background it is drawn on.
        foreach (var (role, color) in new[] { ("text", tokens.Text), ("muted text", tokens.TextMuted),
            ("success", tokens.Success), ("warning", tokens.Warning), ("danger", tokens.Danger), ("link", tokens.Info) })
        {
            AssertContrast(color, tokens.Surface, 4.5, $"{name}: {role} on the window");
            AssertContrast(color, tokens.SurfaceAlt, 4.5, $"{name}: {role} on the alternate surface");
        }
        // A selected row or selected text, and a primary button's label.
        AssertContrast(tokens.SelectionText, tokens.AccentSoft, 4.5, $"{name}: selected text on the selection");
        AssertContrast(tokens.SelectionTextMuted, tokens.AccentSoft, 4.5, $"{name}: selected muted text on the selection");
        AssertContrast(tokens.OnAccent, tokens.Accent, 4.5, $"{name}: text on the accent");
        // Non-text: 3:1 (WCAG 1.4.11) for the focus ring and control borders.
        AssertContrast(tokens.FocusRing, tokens.Surface, 3, $"{name}: focus ring");
        AssertContrast(tokens.BorderStrong, tokens.Surface, 3, $"{name}: border");
    }

    [Fact]
    public void A_Status_Color_That_Blends_Into_The_Window_Falls_Back_To_The_Window_Text()
    {
        // Dusk's window is a dark gray, and the dark preset's danger red reaches only 4.4:1 on it.
        var dusk = WindowsTheme.CreateHighContrastTheme(WindowsSystemColors.Dusk);
        Assert.True(WindowsTheme.ContrastRatio(StandardThemeTokens.HighContrastDark.Danger, WindowsSystemColors.Dusk.Window) < 4.5);
        Assert.Equal(WindowsSystemColors.Dusk.WindowText, dusk.Danger);
        Assert.Equal(StandardThemeTokens.HighContrastDark.Success, dusk.Success);
        Assert.Equal(StandardThemeTokens.HighContrastDark.Warning, dusk.Warning);

        // On Night sky's black it is readable, so the hue stays.
        var nightSky = WindowsTheme.CreateHighContrastTheme(WindowsSystemColors.NightSky);
        Assert.Equal(StandardThemeTokens.HighContrastDark.Danger, nightSky.Danger);

        // Every status color falls back on a window none of them reads on.
        var gray = Colors([0x808080, 0x000000, 0x000000, 0xFFFFFF, 0x808080, 0x000000, 0x404040, 0x000000]);
        var tokens = WindowsTheme.CreateHighContrastTheme(gray);
        Assert.Equal(new[] { gray.WindowText, gray.WindowText, gray.WindowText }, new[] { tokens.Success, tokens.Warning, tokens.Danger });
    }

    [Fact]
    public void Focus_Ring_And_Link_Fall_Back_To_Text_When_Highlight_Blends_In()
    {
        var colors = Colors([0x000000, 0xFFFFFF, 0x101010, 0xFFFFFF, 0x000000, 0xFFFFFF, 0x808080, 0x000040]);
        var tokens = WindowsTheme.CreateHighContrastTheme(colors);
        Assert.Equal(colors.WindowText, tokens.FocusRing);
        Assert.Equal(colors.WindowText, tokens.Info);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void The_High_Contrast_Palette_Takes_The_Text_Scale(double scale)
    {
        var settings = UiSystemSettings.Default with { ContrastPreference = UiContrastPreference.More, TextScale = scale };
        var tokens = WindowsTheme.CreateHighContrastTheme(WindowsSystemColors.Aquatic, settings);
        var expected = StandardThemeTokens.HighContrastDark.WithTextScale(scale);

        Assert.Equal(scale, tokens.TextScale);
        Assert.Equal(expected.FontBody, tokens.FontBody);
        Assert.Equal(expected.FontTitle, tokens.FontTitle);
        Assert.Equal(expected.FontSubtitle, tokens.FontSubtitle);
        Assert.Equal(expected.FontCaption, tokens.FontCaption);
        Assert.Equal(expected.FontCode, tokens.FontCode);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_Text_Scale_That_Is_Not_A_Positive_Number_Leaves_The_Palette_Unscaled(double scale)
    {
        var settings = UiSystemSettings.Default with { TextScale = scale };
        var tokens = WindowsTheme.CreateHighContrastTheme(WindowsSystemColors.Desert, settings);
        Assert.Equal(1.0, tokens.TextScale);
        Assert.Equal(StandardThemeTokens.HighContrastLight.FontBody, tokens.FontBody);
        Assert.Equal(1.0, WindowsTheme.CreateHighContrastTheme(WindowsSystemColors.Desert).TextScale);
    }

    [Fact]
    public void Contrast_Ratio_Matches_Wcag_Extremes()
    {
        Assert.Equal(21, WindowsTheme.ContrastRatio(BColor.Black, BColor.White), 1);
        Assert.Equal(1, WindowsTheme.ContrastRatio(BColor.White, BColor.White), 3);
    }

    [Fact]
    public void Resolve_Uses_The_Preset_Without_High_Contrast_And_System_Colors_With_It()
    {
        var standard = UiSystemSettings.Default with { ContrastPreference = UiContrastPreference.NoPreference, ColorScheme = UiColorScheme.Dark };
        Assert.Equal(StandardThemeTokens.Select(standard), WindowsTheme.ResolveTheme(standard));

        var high = standard with { ContrastPreference = UiContrastPreference.More };
        var tokens = WindowsTheme.ResolveTheme(high);
        if (WindowsSystemColors.Query() is { } colors)
        {
            Assert.Equal("HighContrastSystem", tokens.Name);
            Assert.Equal(colors.Window, tokens.Surface);
        }
        else Assert.Equal(StandardThemeTokens.Select(high), tokens);
    }

    [Fact]
    public void Resolve_Applies_Every_Setting_In_High_Contrast()
    {
        var high = UiSystemSettings.Default with
        {
            ContrastPreference = UiContrastPreference.More,
            TextScale = 2.0,
            ReducedMotion = true,
            Density = UiDensity.Compact,
        };
        var tokens = WindowsTheme.ResolveTheme(high);

        Assert.True(tokens.IsHighContrast);
        Assert.Equal(2.0, tokens.TextScale);
        Assert.True(tokens.ReducedMotion);
        Assert.Equal(UiDensity.Compact, tokens.Density);
    }

    private static void AssertContrast(BColor foreground, BColor background, double minimum, string what)
    {
        double ratio = WindowsTheme.ContrastRatio(foreground, background);
        Assert.True(ratio >= minimum, $"{what}: {ratio:0.00}:1, needs {minimum}:1");
    }

    [Fact]
    public void Title_Bar_Rejects_Missing_Or_Invalid_Windows()
    {
        Assert.False(WindowsTitleBar.ApplyDarkMode(0, true));
        Assert.False(WindowsTitleBar.ApplyDarkMode(0x1234, true));
    }

    private static WindowsSystemColors Colors(uint[] rgb)
    {
        static BColor C(uint value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
        return new(C(rgb[0]), C(rgb[1]), C(rgb[2]), C(rgb[3]), C(rgb[4]), C(rgb[5]), C(rgb[6]), C(rgb[7]));
    }
}
