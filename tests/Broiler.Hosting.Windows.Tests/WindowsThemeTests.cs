using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows;
using Broiler.Input;
using Broiler.Input.Mouse;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.SpinBox.Standard;
using Broiler.UI.Standard;
using Broiler.UI.TabView.Standard;
using Broiler.UI.ToggleButton;
using Broiler.UI.ToggleButton.Standard;
using Broiler.UI.Toolbar.Standard;
using Microsoft.Win32;
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

    // Custom contrast themes a user could choose, whose highlight is chosen as a fill for the highlight text and need
    // not read as text on the window: one that still stands out as a focus ring (4.2:1), one that blends in (1.5:1),
    // and two that read at 4.5:1 and differ only in the highlight text, white or the window text.
    public static TheoryData<string, uint[], bool> CustomContrastThemes => new()
    {
        { "Custom, highlight at 4.2:1", [0xFFFFFF, 0x000000, 0x3A7BD5, 0x000000, 0xFFFFFF, 0x000000, 0x6D6D6D, 0x0000EE], false },
        { "Custom, highlight at 1.5:1", [0x000000, 0xFFFFFF, 0x0000A0, 0xFFFFFF, 0x000000, 0xFFFFFF, 0xA6A6A6, 0x8080FF], true },
        { "Custom, highlight at 4.5:1", [0xFFFFFF, 0x000000, 0x767676, 0xFFFFFF, 0xFFFFFF, 0x000000, 0x6D6D6D, 0x0000EE], false },
        { "Custom, highlight at 4.5:1 under the window text", [0xFFFFFF, 0x000000, 0x767676, 0x000000, 0xFFFFFF, 0x000000, 0x6D6D6D, 0x0000EE], false },
    };

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    public void The_Named_Contrast_Themes_Are_The_Windows_Tables(string name, uint[] rgb, bool dark)
    {
        var named = Named(name);
        Assert.Equal(Colors(rgb), named);
        Assert.Equal(dark, WindowsTheme.CreateHighContrastTheme(named).IsDark);
    }

    [Theory]
    [InlineData("hcblack", "Aquatic")]
    [InlineData("hcwhite", "Desert")]
    [InlineData("hc1", "Dusk")]
    [InlineData("hc2", "Night sky")]
    public void The_Named_Contrast_Themes_Match_The_Theme_Files_Windows_Ships(string file, string name)
    {
        // Only Windows 11 ships these themes; Windows 10 and Windows Server use the same file names for others.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Ease of Access Themes", file + ".theme");
        if (!IsWindows11Client() || !File.Exists(path))
            return;

        var colors = ReadThemeColors(path);
        Assert.Equal(colors, Named(name));
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
        Assert.Equal(colors.Highlight, tokens.StateFill);
        Assert.Equal(colors.HighlightText, tokens.StateText);
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
        var tokens = WindowsTheme.CreateHighContrastTheme(Colors(rgb));
        Assert.Equal(dark, tokens.IsDark);

        // Text: 4.5:1 (WCAG AA for normal text) on the background it is drawn on. Accent text is the selected
        // tab's label, an accent label and a toggle button's label at rest; the selected tab is checked as the tab
        // view draws it in Accent_Text_Reads_On_The_Tab_Strip_As_The_Tab_View_Draws_It. The accent itself is text
        // where a control was never themed and draws from the shared palette (a toggle button's label).
        foreach (var (role, color) in new[] { ("text", tokens.Text), ("muted text", tokens.TextMuted),
            ("success", tokens.Success), ("warning", tokens.Warning), ("danger", tokens.Danger), ("link", tokens.Info),
            ("accent text", tokens.AccentText), ("accent", tokens.Accent) })
        {
            AssertContrast(color, tokens.Surface, 4.5, $"{name}: {role} on the window");
            AssertContrast(color, tokens.SurfaceAlt, 4.5, $"{name}: {role} on the alternate surface");
        }
        // A selected row or selected text, and a primary button's label.
        AssertContrast(tokens.SelectionText, tokens.AccentSoft, 4.5, $"{name}: selected text on the selection");
        AssertContrast(tokens.SelectionTextMuted, tokens.AccentSoft, 4.5, $"{name}: selected muted text on the selection");
        AssertContrast(tokens.OnAccent, tokens.Accent, 4.5, $"{name}: text on the accent");
        // A control state drawn on the state fill. The pairs the controls draw are checked in
        // Control_States_Are_Drawn_In_The_Highlight_Pair.
        AssertContrast(tokens.StateText, tokens.StateFill, 4.5, $"{name}: state text on the state fill");
        // Non-text: 3:1 (WCAG 1.4.11) for the focus ring, control borders, and the accent fill of a progress
        // bar or slider on its track.
        AssertContrast(tokens.FocusRing, tokens.Surface, 3, $"{name}: focus ring");
        AssertContrast(tokens.BorderStrong, tokens.Surface, 3, $"{name}: border");
        AssertContrast(tokens.Accent, tokens.SurfaceDisabled, 3, $"{name}: accent fill on the track");
    }

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    [MemberData(nameof(CustomContrastThemes))]
    public void Accent_Text_Reads_On_The_Tab_Strip_As_The_Tab_View_Draws_It(string name, uint[] rgb, bool dark)
    {
        var tokens = WindowsTheme.CreateHighContrastTheme(Colors(rgb));
        Assert.Equal(dark, tokens.IsDark);

        var tabs = new StandardTabView();
        foreach (string header in new[] { "Inbox", "Sent", "Settings" })
            tabs.AddTab(header.ToLowerInvariant(), header);
        tabs.ApplyTheme(tokens);
        using var view = new StateView(tabs, new BRect(0, 0, 300, 120));
        BRenderList list = view.Render();
        BRect selected = tabs.GetTabHeaderBounds(tabs.SelectedIndex);
        var fills = list.Commands.OfType<BRenderCommand.FillRoundedRect>().ToList();

        // The selected tab's label is accent text, drawn on the selected header's fill.
        BColor fill = Assert.Single(fills, command => command.Rect == selected).Color;
        BColor label = StateView.TextColor(list, "Inbox");
        Assert.Equal(tokens.AccentText, label);
        AssertContrast(label, fill, 4.5, $"{name}: selected tab label on its header");

        // The bar under it is a mark that is not text: 3:1 against the header's fill and against the strip, which
        // is not filled and shows the surface the tab view lies on.
        BColor bar = Assert.Single(fills, command => command.Rect != selected && command.Rect.Top >= selected.Top && command.Rect.Bottom <= selected.Bottom).Color;
        Assert.Equal(tokens.AccentText, bar);
        Assert.Equal(0, tabs.HeaderBackground.A);
        foreach (var (what, behind) in new[] { ("its header", fill), ("the window", tokens.Surface), ("the alternate surface", tokens.SurfaceAlt) })
            AssertContrast(bar, behind, 3, $"{name}: selected tab bar on {what}");

        // The other tabs' labels are text on the strip.
        foreach (string other in new[] { "Sent", "Settings" })
            AssertContrast(StateView.TextColor(list, other), tokens.Surface, 4.5, $"{name}: {other} tab label on the strip");
    }

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    // A custom contrast theme whose selected text is its window text, a pairing Windows lets a user choose.
    [InlineData("Custom", new uint[] { 0x000000, 0xFFFFFF, 0x0000A0, 0xFFFFFF, 0x000000, 0xFFFFFF, 0xA6A6A6, 0x8080FF }, true)]
    public void Control_States_Are_Drawn_In_The_Highlight_Pair(string name, uint[] rgb, bool dark)
    {
        var colors = Colors(rgb);
        var tokens = WindowsTheme.CreateHighContrastTheme(colors);
        Assert.Equal(dark, tokens.IsDark);
        // Windows draws a hovered, pressed, or checked control in the highlight pair. These are the states
        // Broiler.UI draws on the state fill, each checked as the control draws it, not only as the palette
        // names it. The states it draws elsewhere are checked in
        // Pressed_Buttons_And_A_Hovered_Unchecked_Toggle_Look_As_At_Rest.
        (BColor Fill, BColor Text) highlight = (colors.Highlight, colors.HighlightText);

        var button = new StandardButton { Text = "Reply" };
        button.ApplyTheme(tokens);
        using (var view = new StateView(button, new BRect(10, 10, 80, 30)))
        {
            view.Move(button.Bounds);
            AssertDrawnReadable(highlight, view.Look(button, "Reply"), $"{name}: hovered secondary button");
        }

        var toggle = new StandardToggleButton { Text = "Flag", IsThreeState = true };
        toggle.ApplyTheme(tokens);
        using (var view = new StateView(toggle, new BRect(10, 10, 80, 30)))
        {
            view.Press(toggle.Bounds);
            AssertDrawnReadable(highlight, view.Look(toggle, "Flag"), $"{name}: pressed toggle button");
            view.Release(toggle.Bounds);
            Assert.Equal(UiToggleState.On, toggle.ToggleState);
            AssertDrawnReadable(highlight, view.Look(toggle, "Flag"), $"{name}: checked toggle button");
            toggle.ToggleState = UiToggleState.Indeterminate;
            AssertDrawnReadable(highlight, view.Look(toggle, "Flag"), $"{name}: indeterminate toggle button");
        }

        var spin = new StandardSpinBox { Minimum = 0, Maximum = 100, Value = 5 };
        spin.ApplyTheme(tokens);
        using (var view = new StateView(spin, new BRect(10, 10, 120, 32)))
        {
            BRect up = spin.UpArrowBounds;
            view.Move(up);
            BRenderList list = view.Render();
            BColor fill = Assert.Single(list.Commands.OfType<BRenderCommand.FillRect>(), command => command.Rect == up).Color;
            // The up arrow is drawn first.
            BColor arrow = list.Commands.OfType<BRenderCommand.FillTriangle>().First().Color;
            AssertDrawnReadable(highlight, (fill, arrow), $"{name}: hovered spin box arrow");
        }

        // Four 80-wide items in a bar with room for two of them.
        var toolbar = new StandardToolbar { Padding = 10, Spacing = 4 };
        foreach (string item in new[] { "New", "Reply", "Forward", "Delete" })
            toolbar.AddChild(new StandardButton { Text = item, PreferredSize = new BSize(80, 30) });
        toolbar.ApplyTheme(tokens);
        using (var view = new StateView(toolbar, new BRect(0, 0, 220, 44)))
        {
            Assert.True(toolbar.OpenOverflow());
            BRenderList list = view.Render();
            BColor fill = Assert.Single(list.Commands.OfType<BRenderCommand.FillRoundedRect>(), command => command.Rect == toolbar.OverflowButtonBounds).Color;
            AssertDrawnReadable(highlight, (fill, StateView.TextColor(list, "»")), $"{name}: open toolbar overflow button");
        }
    }

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    [MemberData(nameof(CustomContrastThemes))]
    public void Pressed_Buttons_And_A_Hovered_Unchecked_Toggle_Look_As_At_Rest(string name, uint[] rgb, bool dark)
    {
        // The known gap in CreateHighContrastTheme's remarks: Broiler.UI draws these states on SurfaceDisabled
        // or SurfaceAlt, both the window color here, not on the state fill, so they read but give no feedback.
        // Once Broiler.UI draws them in the state pair, move them to
        // Control_States_Are_Drawn_In_The_Highlight_Pair and drop the gap from the remarks and the README.
        var colors = Colors(rgb);
        var tokens = WindowsTheme.CreateHighContrastTheme(colors);
        Assert.Equal(dark, tokens.IsDark);

        var button = new StandardButton { Text = "Reply" };
        button.ApplyTheme(tokens);
        using (var view = new StateView(button, new BRect(10, 10, 80, 30)))
        {
            var rest = view.Look(button, "Reply");
            Assert.Equal((colors.Window, colors.WindowText), rest);
            view.Move(button.Bounds);
            view.Press(button.Bounds);
            Assert.True(button.IsPressed);
            AssertDrawnReadable(rest, view.Look(button, "Reply"), $"{name}: pressed secondary button");
        }

        var toggle = new StandardToggleButton { Text = "Flag" };
        toggle.ApplyTheme(tokens);
        using (var view = new StateView(toggle, new BRect(10, 10, 80, 30)))
        {
            // A themed toggle button's label is accent text, here on the window color at rest and when hovered.
            var rest = view.Look(toggle, "Flag");
            Assert.Equal((colors.Window, tokens.AccentText), rest);
            view.Move(toggle.Bounds);
            AssertDrawnReadable(rest, view.Look(toggle, "Flag"), $"{name}: hovered unchecked toggle button");
        }

        var spin = new StandardSpinBox { Minimum = 0, Maximum = 100, Value = 5 };
        spin.ApplyTheme(tokens);
        using (var view = new StateView(spin, new BRect(10, 10, 120, 32)))
        {
            BRect up = spin.UpArrowBounds;
            view.Move(up);
            view.Press(up);
            BRenderList list = view.Render();
            BColor fill = Assert.Single(list.Commands.OfType<BRenderCommand.FillRect>(), command => command.Rect == up).Color;
            // The up arrow is drawn first, in the color it has at rest.
            BColor arrow = list.Commands.OfType<BRenderCommand.FillTriangle>().First().Color;
            AssertDrawnReadable((colors.Window, colors.WindowText), (fill, arrow), $"{name}: pressed spin box arrow");
        }
    }

    [Fact]
    public void The_States_Keep_The_Highlight_Pair_In_A_Copy_That_Recolors_The_Selection()
    {
        var colors = WindowsSystemColors.Aquatic;
        var tokens = WindowsTheme.CreateHighContrastTheme(colors) with
        {
            AccentSoft = colors.Window,
            SelectionText = colors.WindowText,
            SelectionTextMuted = colors.WindowText,
        };

        Assert.Equal(colors.Highlight, tokens.StateFill);
        Assert.Equal(colors.HighlightText, tokens.StateText);
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

    [Fact]
    public void Accent_Text_Falls_Back_To_The_Window_Text_Where_The_Highlight_Does_Not_Read_On_The_Window()
    {
        // The Windows 11 themes' highlight reads on their window, so their accent text stays the highlight color.
        foreach (var named in new[] { WindowsSystemColors.Aquatic, WindowsSystemColors.Desert, WindowsSystemColors.Dusk, WindowsSystemColors.NightSky })
            Assert.Equal(named.Highlight, WindowsTheme.CreateHighContrastTheme(named).AccentText);

        // A highlight at 4.2:1 on the window still stands out as a focus ring, and still fills the accent, the
        // selection and the states, but text in it would not read: accent text takes the window text color.
        var colors = Colors([0xFFFFFF, 0x000000, 0x3A7BD5, 0x000000, 0xFFFFFF, 0x000000, 0x6D6D6D, 0x0000EE]);
        var tokens = WindowsTheme.CreateHighContrastTheme(colors);
        Assert.Equal(colors.WindowText, tokens.AccentText);
        Assert.Equal(colors.Highlight, tokens.FocusRing);
        Assert.Equal(new[] { colors.Highlight, colors.Highlight, colors.Highlight }, new[] { tokens.Accent, tokens.AccentSoft, tokens.StateFill });

        // At 4.5:1 it reads, and stays the accent text.
        var readable = Colors([0xFFFFFF, 0x000000, 0x767676, 0xFFFFFF, 0xFFFFFF, 0x000000, 0x6D6D6D, 0x0000EE]);
        Assert.InRange(WindowsTheme.ContrastRatio(readable.Highlight, readable.Window), 4.5, 4.6);
        Assert.Equal(readable.Highlight, WindowsTheme.CreateHighContrastTheme(readable).AccentText);
    }

    [Fact]
    public void The_Accent_Text_Is_Kept_In_A_Copy_That_Recolors_The_Accent()
    {
        var colors = WindowsSystemColors.Aquatic;
        var tokens = WindowsTheme.CreateHighContrastTheme(colors) with { Accent = BColor.FromArgb(0xFF, 0x0B, 0x6F, 0xD8) };

        Assert.Equal(colors.Highlight, tokens.AccentText);
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

    /// <summary>The control drew the expected fill and label, and the label reads on the fill (4.5:1).</summary>
    private static void AssertDrawnReadable((BColor Fill, BColor Text) expected, (BColor Fill, BColor Text) drawn, string what)
    {
        Assert.True(expected == drawn, $"{what}: drawn {drawn.Text} on {drawn.Fill}, expected {expected.Text} on {expected.Fill}");
        AssertContrast(drawn.Text, drawn.Fill, 4.5, what);
    }

    [Fact]
    public void Title_Bar_Rejects_Missing_Or_Invalid_Windows()
    {
        Assert.False(WindowsTitleBar.ApplyDarkMode(0, true));
        Assert.False(WindowsTitleBar.ApplyDarkMode(0x1234, true));
    }

    private static WindowsSystemColors Named(string name) => name switch
    {
        "Aquatic" => WindowsSystemColors.Aquatic,
        "Desert" => WindowsSystemColors.Desert,
        "Dusk" => WindowsSystemColors.Dusk,
        "Night sky" => WindowsSystemColors.NightSky,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static bool IsWindows11Client()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;
        using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return version?.GetValue("InstallationType") as string == "Client";
    }

    /// <summary>Reads the system colors of a .theme file's [Control Panel\Colors] section ("R G B" values).</summary>
    private static WindowsSystemColors ReadThemeColors(string path)
    {
        // Only the colors read here are parsed: Windows' own hc2.theme has "MenuText=255 255 255e".
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inColors = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inColors = string.Equals(line, @"[Control Panel\Colors]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int equals = line.IndexOf('=');
            if (inColors && equals > 0)
                values[line[..equals]] = line[(equals + 1)..];
        }

        BColor Read(string key)
        {
            string[] rgb = values[key].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, rgb.Length);
            return new(byte.Parse(rgb[0]), byte.Parse(rgb[1]), byte.Parse(rgb[2]), 255);
        }
        return new(Read("Window"), Read("WindowText"), Read("Hilight"), Read("HilightText"),
            Read("ButtonFace"), Read("ButtonText"), Read("GrayText"), Read("HotTrackingColor"));
    }

    private static WindowsSystemColors Colors(uint[] rgb)
    {
        static BColor C(uint value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
        return new(C(rgb[0]), C(rgb[1]), C(rgb[2]), C(rgb[3]), C(rgb[4]), C(rgb[5]), C(rgb[6]), C(rgb[7]));
    }

    /// <summary>A session showing one control in a fixed box, and a mouse to work it with.</summary>
    private sealed class StateView : IDisposable
    {
        private readonly UiSession _session;
        private long _sequence;

        public StateView(UiElement element, BRect box)
        {
            _session = new StandardUiSessionBuilder().WithDispatcher(new ImmediateUiDispatcher()).Build(new Host());
            _session.AddRoot(new FixedBox(element, box));
            _session.RenderFrame();
        }

        public BRenderList Render() => _session.RenderFrame();

        /// <summary>The fill drawn over the element's bounds and the color of its label, in a new frame.</summary>
        public (BColor Fill, BColor Text) Look(UiElement element, string label)
        {
            BRenderList list = Render();
            return (Assert.Single(list.Commands.OfType<BRenderCommand.FillRoundedRect>(), command => command.Rect == element.Bounds).Color, TextColor(list, label));
        }

        public static BColor TextColor(BRenderList list, string text) =>
            Assert.Single(list.Commands.OfType<BRenderCommand.DrawText>(), command => command.Text.Text == text).Text.Color;

        public void Move(BRect target) =>
            _session.DispatchInput(UiInputEvent.FromMouseMove(new MouseMoveEvent(Header(), Middle(target), MouseButtons.None, InputEventSource.Synthetic)));

        public void Press(BRect target) => Button(target, MouseButtonTransition.Down);

        public void Release(BRect target) => Button(target, MouseButtonTransition.Up);

        public void Dispose() => _session.Dispose();

        private void Button(BRect target, MouseButtonTransition transition) =>
            _session.DispatchInput(UiInputEvent.FromMouseButton(new MouseButtonEvent(
                Header(),
                Middle(target),
                transition == MouseButtonTransition.Down ? MouseButtons.Left : MouseButtons.None,
                MouseButton.Left,
                transition,
                InputEventSource.Synthetic)));

        private static InputPoint Middle(BRect rect) =>
            InputPoint.ClientDeviceIndependentPixels(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

        private InputEventHeader Header()
        {
            _sequence++;
            return new InputEventHeader(
                InputDeviceId.FromOpaqueValue("mouse:theme"),
                new InputTimestamp(_sequence, TimeSpan.TicksPerSecond, "theme-tests"),
                _sequence);
        }

        /// <summary>Arranges its one child into a fixed rectangle.</summary>
        private sealed class FixedBox : UiElement
        {
            private readonly UiElement _child;
            private readonly BRect _box;

            public FixedBox(UiElement child, BRect box)
            {
                _child = child;
                _box = box;
                AddChild(child);
            }

            protected override BSize MeasureCore(BSize availableSize)
            {
                _child.Measure(new BSize(_box.Width, _box.Height));
                return availableSize;
            }

            protected override void ArrangeCore(BRect finalRect) => _child.Arrange(_box);
        }

        private sealed class Host : IUiHost
        {
            public BSize ViewportSize { get; } = new(320, 200);
            public double Scale => 1;
            public BRenderList CreateRenderList(int capacity = 0) => new(capacity);
            public void Invalidate(UiInvalidation invalidation) { }
            public void Present(BRenderList renderList) { }
        }
    }
}
