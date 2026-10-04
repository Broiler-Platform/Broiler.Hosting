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

    /// <summary>Converts a Win32 COLORREF (0x00BBGGRR) to an opaque color.</summary>
    internal static BColor FromColorRef(uint colorRef) =>
        new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF), 255);

    private static BColor Read(int index) => FromColorRef(WindowNative.GetSysColor(index));
}
