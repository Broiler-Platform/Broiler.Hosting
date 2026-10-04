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
    /// colors; the accent and the selection use the highlight pair, so selected rows and selected text
    /// are drawn in the highlight text color. The focus ring uses the highlight color only when it
    /// stands out from the window background (3:1), and falls back to the window text color otherwise.
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
    /// Known gap: Broiler.UI also draws some control states on <c>AccentSoft</c>, which has to be the
    /// highlight color for selections. A hovered secondary button (<c>Text</c> on it), a checked or pressed
    /// toggle button (<c>Accent</c> on it), and a hovered spin box arrow (<c>TextMuted</c> on it) are
    /// therefore not readable in this palette until Broiler.UI gives state fills a role of their own.
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
            // Windows pairs Highlight with HighlightText and has no muted variant of it.
            SelectionText = colors.HighlightText,
            SelectionTextMuted = colors.HighlightText,
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
