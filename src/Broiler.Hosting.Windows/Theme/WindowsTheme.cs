using System;
using System.IO;
using System.Runtime.InteropServices;
using Broiler.UI;
using Broiler.UI.Standard;
using Microsoft.Win32;

namespace Broiler.Hosting.Windows;

/// <summary>
/// Queries system appearance, theme preferences, high contrast, and accessibility motion settings on Windows.
/// </summary>
public static class WindowsTheme
{
    public static StandardThemeTokens ResolveTheme(bool darkPreferred) =>
        darkPreferred ? StandardThemeTokens.Dark : StandardThemeTokens.Light;

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
            TextScale: 1.0,
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
