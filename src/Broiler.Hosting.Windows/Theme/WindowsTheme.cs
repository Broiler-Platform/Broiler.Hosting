using System;
using System.IO;
using System.Runtime.InteropServices;
using Broiler.Graphics.Color;
using Broiler.Native.Windows;
using Broiler.UI;
using Broiler.UI.Standard;
using Microsoft.Win32;

namespace Broiler.Hosting.Windows;

/// <summary>
/// Queries system appearance, theme preferences, high contrast, and accessibility motion settings on Windows.
/// </summary>
public static class WindowsTheme
{
    /// <summary>The smallest and largest Windows "Make text bigger" values, as percentages.</summary>
    public const int MinimumTextScalePercent = 100;
    public const int MaximumTextScalePercent = 225;

    public static StandardThemeTokens ResolveTheme(bool darkPreferred) =>
        darkPreferred ? StandardThemeTokens.Dark : StandardThemeTokens.Light;

    /// <summary>
    /// Resolves the palette for the given system settings. In high-contrast mode the palette is built
    /// from the user's actual system colors; otherwise the matching standard preset is used.
    /// </summary>
    public static StandardThemeTokens ResolveTheme(UiSystemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ContrastPreference == UiContrastPreference.More && WindowsSystemColors.Query() is { } colors)
            return CreateHighContrastTheme(colors, settings);
        return StandardThemeTokens.Select(settings);
    }

    /// <summary>
    /// Builds a high-contrast palette from system colors. Text, borders, and surfaces use the window
    /// colors; the accent, the selection, and the state fill use the highlight pair, so selected rows,
    /// selected text, and the control states Broiler.UI draws on the state fill (listed in the remarks)
    /// are drawn in the highlight text color on the highlight color. The focus ring uses the highlight
    /// color only when it stands out from the window background (3:1), and falls back to the window text
    /// color otherwise. Text drawn in the accent (<c>AccentText</c>) is drawn on the window color, so it
    /// uses the highlight color only when that reads there (4.5:1) and the highlight text is not the window
    /// text, and the window text color otherwise. Scrollbars draw their thumb in the window text color, like every
    /// other control at rest here, on a track of the window color.
    /// Status colors that Windows does not define keep the high-contrast preset values where they are
    /// readable on the window background (4.5:1), and fall back to the window text color otherwise;
    /// the link color does the same with the system hyperlink color. The palette is flagged as high
    /// contrast, and the settings' reduced motion, density, and text scale are applied.
    /// </summary>
    /// <remarks>
    /// <paramref name="colors"/> need not be the current system colors: pass one of the Windows 11
    /// contrast themes (<see cref="WindowsSystemColors.Aquatic"/> and the others), or any colors a user
    /// could choose, to build the palette that theme gives without changing the system's settings.
    /// <para>
    /// The state pair (<c>StateFill</c>, <c>StateText</c>) colors a hovered secondary button or spin box
    /// arrow, a checked, indeterminate, or pressed toggle button, and the open toolbar overflow button; the
    /// state fill alone marks the code editor's matching bracket. Windows 11 contrast themes draw UI that is
    /// hovered, pressed, or selected in Highlight and HighlightText, and keep ButtonFace and ButtonText for
    /// controls at rest, so the states take the highlight pair. ButtonFace is the window color in all four of
    /// the themes Windows ships, so a state drawn on it would look like the control at rest.
    /// </para>
    /// <para>
    /// Known gaps: Broiler.UI draws a pressed secondary button and a pressed spin box arrow on
    /// <c>SurfaceDisabled</c>, and a hovered unchecked toggle button on <c>SurfaceAlt</c>, not on the state
    /// fill. Both are the window color here, as disabled and alternate surfaces need, so those states stay
    /// readable but look like the control at rest. Broiler.UI also draws in <c>Accent</c>, here the highlight
    /// color, directly on the window color: text (the label of a toggle button that was never themed, the code
    /// editor's keywords) and marks (a checked check box's fill and border, a checked radio button's dot, the
    /// progress bar's and slider's fill on their track, the list's unread dot, a window's or dialog's active
    /// border). That reads in the four Windows 11 themes, but not in a custom theme whose highlight color is
    /// close to its window color. And while the highlight text is the window text, a format code view draws
    /// selected codes other than inline codes in the link and status colors on the highlight color, where they
    /// need not read.
    /// </para>
    /// <para>
    /// <c>AccentText</c> colors the selected tab's label and the bar under it, an accent label, a themed toggle
    /// button's label and icon at rest and hovered, and inline codes, all on the window color. In the four
    /// Windows 11 themes it is the highlight color, which reads there at 6.8:1 (Dusk) to 11.8:1 (Night sky). A
    /// custom theme's highlight color is chosen as a fill for its highlight text, so where it reads under 4.5:1
    /// on the window color the text takes the window text color, and the bar still marks the selected tab. On
    /// the highlight color, a checked, indeterminate, or pressed toggle button draws the state text, because the
    /// accent is the state fill, and a format code view draws selected text and codes in the selection text,
    /// except while that is the text color: then it draws each code in its own color, and selected inline codes
    /// in the accent text on the highlight. In this palette that is when the highlight text is the window text,
    /// so the accent text then takes the window text color too, which reads on the window and, as the highlight
    /// text, on the highlight.
    /// </para>
    /// <para>
    /// <c>ScrollbarTrack</c> and <c>ScrollbarThumb</c> color the bars of every Broiler.UI control that scrolls: the
    /// window text color on a track of the window color. A thumb is as wide as its track, so its sides meet the window
    /// beside the bar too, and the track is the window color: the thumb needs 3:1 on the window color alone, which the
    /// window text gives it at 10.4:1 (Desert) to 21:1 (Night sky) in the four Windows 11 themes. WinUI draws a thumb
    /// in ButtonText, the color of its buttons, Highlight while it is hovered or pressed, and GrayText while the bar
    /// is disabled. Broiler.UI has no button roles, so this palette draws buttons and every other control at rest in
    /// the window text color, and the thumb matches them. Where ButtonText is not the window text, as in Desert, Dusk
    /// and Night sky, these bars therefore differ from the ones WinUI apps draw. Broiler.UI has no hovered, pressed,
    /// or disabled thumb either, so those look like the thumb at rest. The two roles are set, so every copy of the
    /// palette keeps these bars, even one that clears <c>IsHighContrast</c>: its scroll views, rich edits and format
    /// code views still draw opaque bars, and lay their text out beside the bar rather than under it.
    /// </para>
    /// <para>
    /// Known gap: a focused list strokes its focus ring, the highlight color here, over its scrollbar, and a selected
    /// row's highlight fill meets the thumb's side. The highlight stands out from the thumb at only 1.4:1 (Desert) to
    /// 1.9:1 (Dusk) in the four Windows 11 themes, so the ring barely shows where it crosses the thumb. A scroll view
    /// draws that stretch of its ring again in a color that stands out on the thumb; the list does not.
    /// </para>
    /// </remarks>
    public static StandardThemeTokens CreateHighContrastTheme(WindowsSystemColors colors, UiSystemSettings? settings = null)
    {
        bool dark = RelativeLuminance(colors.Window) < RelativeLuminance(colors.WindowText);
        var preset = dark ? StandardThemeTokens.HighContrastDark : StandardThemeTokens.HighContrastLight;
        var focus = ContrastRatio(colors.Highlight, colors.Window) >= 3 ? colors.Highlight : colors.WindowText;
        var tokens = preset with
        {
            Name = "HighContrastSystem",
            IsDark = dark,
            Surface = colors.Window,
            SurfaceAlt = colors.Window,
            SurfaceDisabled = colors.Window,
            Border = colors.WindowText,
            BorderStrong = colors.WindowText,
            Text = colors.WindowText,
            // High contrast has no secondary text color; muted text must stay fully readable.
            TextMuted = colors.WindowText,
            TextDisabled = colors.GrayText,
            Accent = colors.Highlight,
            AccentHover = colors.Highlight,
            AccentPressed = colors.Highlight,
            AccentSoft = colors.Highlight,
            OnAccent = colors.HighlightText,
            // Accent text is drawn on the window color, where a highlight chosen as a fill need not read. While the
            // highlight text is the window text, selected inline codes are drawn in it on the highlight as well, where
            // only the window text reads.
            AccentText = colors.HighlightText == colors.WindowText ? colors.WindowText : ReadableOnWindow(colors.Highlight, colors),
            // Windows pairs Highlight with HighlightText and has no muted variant of it.
            SelectionText = colors.HighlightText,
            SelectionTextMuted = colors.HighlightText,
            // The control states Broiler.UI draws on the state fill are drawn as Windows draws them, in the
            // highlight pair (the remarks list them, and the states it does not). Set, not left to follow the
            // selection, so a copy that recolors the selection keeps it.
            StateFill = colors.Highlight,
            StateText = colors.HighlightText,
            // Every scrollbar in the window text, as every other control at rest here (the remarks say why not the
            // button text). Set, not left to follow the disabled surface and the strong border, so a copy that
            // recolors those keeps the bars.
            ScrollbarTrack = colors.Window,
            ScrollbarThumb = colors.WindowText,
            FocusRing = focus,
            Success = ReadableOnWindow(preset.Success, colors),
            Warning = ReadableOnWindow(preset.Warning, colors),
            Danger = ReadableOnWindow(preset.Danger, colors),
            Info = ReadableOnWindow(colors.HotLight, colors),
            IsHighContrast = true,
            ReducedMotion = settings?.ReducedMotion ?? preset.ReducedMotion,
            Density = settings?.Density ?? preset.Density,
        };
        // The same guard StandardThemeTokens.Select uses: a scale that is not a positive number means unscaled.
        double scale = settings?.TextScale ?? 1.0;
        return tokens.WithTextScale(double.IsFinite(scale) && scale > 0 ? scale : 1.0);
    }

    /// <summary>The color, when text in it reads on the window background (4.5:1); the window text color otherwise.</summary>
    private static BColor ReadableOnWindow(BColor color, WindowsSystemColors colors) =>
        ContrastRatio(color, colors.Window) >= 4.5 ? color : colors.WindowText;

    /// <summary>
    /// Converts the Windows "Make text bigger" registry value (a percentage) to a scale factor.
    /// Missing or non-numeric values mean 1.0; numbers are clamped to the range Windows offers.
    /// </summary>
    public static double ParseTextScale(object? registryValue) => registryValue is int percent
        ? Math.Clamp(percent, MinimumTextScalePercent, MaximumTextScalePercent) / 100.0
        : 1.0;

    /// <summary>Reads the Windows "Make text bigger" accessibility setting as a scale factor (1.0 to 2.25).</summary>
    public static double QueryTextScale()
    {
        if (!OperatingSystem.IsWindows())
            return 1.0;

        try
        {
            using var accessibility = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return ParseTextScale(accessibility?.GetValue("TextScaleFactor"));
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 1.0;
        }
    }

    /// <summary>WCAG relative luminance of an opaque color.</summary>
    internal static double RelativeLuminance(BColor color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG contrast ratio between two opaque colors, from 1 to 21.</summary>
    internal static double ContrastRatio(BColor first, BColor second)
    {
        double a = RelativeLuminance(first), b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    public static bool IsDarkThemePreferred()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var personalization = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalization?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static UiSystemSettings QuerySystemSettings()
    {
        UiColorScheme scheme = IsDarkThemePreferred() ? UiColorScheme.Dark : UiColorScheme.Light;

        bool highContrast = false;
        bool animationsEnabled = true;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var hc = new WindowNative.HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<WindowNative.HIGHCONTRAST>() };
                if (WindowNative.SystemParametersInfo(WindowNative.SpiGetHighContrast, hc.cbSize, ref hc, 0))
                    highContrast = (hc.dwFlags & WindowNative.HcfHighContrastOn) != 0;
            }
            catch { }

            try
            {
                WindowNative.SystemParametersInfo(WindowNative.SpiGetClientAreaAnimation, 0, ref animationsEnabled, 0);
            }
            catch { }
        }

        return new UiSystemSettings(
            ContrastPreference: highContrast ? UiContrastPreference.More : UiContrastPreference.NoPreference,
            TextScale: QueryTextScale(),
            ReducedMotion: !animationsEnabled,
            FlowDirection: UiFlowDirection.LeftToRight,
            ColorScheme: scheme,
            Density: UiDensity.Comfortable);
    }
}
