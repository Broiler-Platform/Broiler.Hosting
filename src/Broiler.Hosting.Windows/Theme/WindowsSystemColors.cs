using System;
using System.Runtime.InteropServices;
using Broiler.Graphics.Color;
using Broiler.Native.Windows;

namespace Broiler.Hosting.Windows;

/// <summary>
/// The Windows system colors that define a high-contrast theme. Users choose these colors, so a
/// high-contrast palette built from them matches the rest of the desktop instead of a fixed preset.
/// </summary>
public readonly record struct WindowsSystemColors(
    BColor Window,
    BColor WindowText,
    BColor Highlight,
    BColor HighlightText,
    BColor ButtonFace,
    BColor ButtonText,
    BColor GrayText,
    BColor HotLight)
{
    /// <summary>Reads the current system colors. Returns null when not running on Windows.</summary>
    public static WindowsSystemColors? Query()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return new WindowsSystemColors(
                Read(WindowNative.ColorWindow),
                Read(WindowNative.ColorWindowText),
                Read(WindowNative.ColorHighlight),
                Read(WindowNative.ColorHighlightText),
                Read(WindowNative.ColorButtonFace),
                Read(WindowNative.ColorButtonText),
                Read(WindowNative.ColorGrayText),
                Read(WindowNative.ColorHotLight));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // The Windows 11 contrast themes as Windows ships them: the [Control Panel\Colors] section of the theme
    // files in %SystemRoot%\Resources\Ease of Access Themes (hcblack.theme is Aquatic, hcwhite.theme Desert,
    // hc1.theme Dusk, and hc2.theme Night sky). A user can edit a theme's colors, so these are what the theme
    // starts from, not what every user of it sees.

    /// <summary>The Aquatic contrast theme, the one Windows applies when none was chosen before.</summary>
    public static WindowsSystemColors Aquatic { get; } = FromRgb(0x202020, 0xFFFFFF, 0x8EE3F0, 0x263B50, 0x202020, 0xFFFFFF, 0xA6A6A6, 0x75E9FC);

    /// <summary>The Desert contrast theme, the light one.</summary>
    public static WindowsSystemColors Desert { get; } = FromRgb(0xFFFAEF, 0x3D3D3D, 0x903909, 0xFFF5E3, 0xFFFAEF, 0x202020, 0x676767, 0x1C5E75);

    /// <summary>The Dusk contrast theme.</summary>
    public static WindowsSystemColors Dusk { get; } = FromRgb(0x2D3236, 0xFFFFFF, 0xA1BFDE, 0x212D3B, 0x2D3236, 0xB6F6F0, 0xA6A6A6, 0x70EBDE);

    /// <summary>The Night sky contrast theme.</summary>
    public static WindowsSystemColors NightSky { get; } = FromRgb(0x000000, 0xFFFFFF, 0xD6B4FD, 0x2B2B2B, 0x000000, 0xFFEE32, 0xA6A6A6, 0x8080FF);

    /// <summary>Colors given as 0xRRGGBB, in the order of the constructor's parameters.</summary>
    private static WindowsSystemColors FromRgb(
        uint window, uint windowText, uint highlight, uint highlightText,
        uint buttonFace, uint buttonText, uint grayText, uint hotLight)
    {
        static BColor C(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        return new(C(window), C(windowText), C(highlight), C(highlightText), C(buttonFace), C(buttonText), C(grayText), C(hotLight));
    }

    /// <summary>Converts a Win32 COLORREF (0x00BBGGRR) to an opaque color.</summary>
    internal static BColor FromColorRef(uint colorRef) =>
        new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF), 255);

    private static BColor Read(int index) => FromColorRef(WindowNative.GetSysColor(index));
}
