using System;
using System.Runtime.InteropServices;
using Broiler.Graphics.Color;

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
    private const int ColorWindow = 5;
    private const int ColorWindowText = 8;
    private const int ColorHighlight = 13;
    private const int ColorHighlightText = 14;
    private const int ColorButtonFace = 15;
    private const int ColorGrayText = 17;
    private const int ColorButtonText = 18;
    private const int ColorHotLight = 26;

    /// <summary>Reads the current system colors. Returns null when not running on Windows.</summary>
    public static WindowsSystemColors? Query()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return new WindowsSystemColors(
                Read(ColorWindow), Read(ColorWindowText), Read(ColorHighlight), Read(ColorHighlightText),
                Read(ColorButtonFace), Read(ColorButtonText), Read(ColorGrayText), Read(ColorHotLight));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Converts a Win32 COLORREF (0x00BBGGRR) to an opaque color.</summary>
    internal static BColor FromColorRef(uint colorRef) =>
        new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF), 255);

    private static BColor Read(int index) => FromColorRef(GetSysColor(index));

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
