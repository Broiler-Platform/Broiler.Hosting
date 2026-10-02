using System;
using System.IO;
using System.Runtime.InteropServices;
using Broiler.Graphics.Color;
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
    /// colors; the accent uses the highlight pair. The focus ring uses the highlight color only when it
    /// stands out from the window background (3:1), and falls back to the window text color otherwise.
    /// Status colors that Windows does not define keep the high-contrast preset values.
    /// </summary>
    public static StandardThemeTokens CreateHighContrastTheme(WindowsSystemColors colors, UiSystemSettings? settings = null)
    {
        bool dark = RelativeLuminance(colors.Window) < RelativeLuminance(colors.WindowText);
        var preset = dark ? StandardThemeTokens.HighContrastDark : StandardThemeTokens.HighContrastLight;
        var focus = ContrastRatio(colors.Highlight, colors.Window) >= 3 ? colors.Highlight : colors.WindowText;
        var link = ContrastRatio(colors.HotLight, colors.Window) >= 4.5 ? colors.HotLight : colors.WindowText;
        return preset with
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
            FocusRing = focus,
            Info = link,
            ReducedMotion = settings?.ReducedMotion ?? preset.ReducedMotion,
            Density = settings?.Density ?? preset.Density,
        };
    }

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
                var hc = new HighContrast { cbSize = (uint)Marshal.SizeOf<HighContrast>() };
                if (SystemParametersInfoW(0x0042 /* SPI_GETHIGHCONTRAST */, hc.cbSize, ref hc, 0))
                    highContrast = (hc.dwFlags & 1 /* HCF_HIGHCONTRASTON */) != 0;
            }
            catch { }

            try
            {
                SystemParametersInfoBool(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref animationsEnabled, 0);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint cbSize;
        public uint dwFlags;
        public nint lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref HighContrast pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoBool(uint uiAction, uint uiParam, [MarshalAs(UnmanagedType.Bool)] ref bool pvParam, uint fWinIni);
}
