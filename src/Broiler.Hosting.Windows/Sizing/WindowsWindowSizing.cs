using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Broiler.Hosting.Windows;

/// <summary>
/// Window sizing, minimum dimension enforcement, and multi-monitor DPI transition handling.
/// </summary>
[SupportedOSPlatform("windows5.0")]
public static class WindowsWindowSizing
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmDpiChanged = 0x02E0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    public static void OnMessage(IntPtr window, uint message, IntPtr data, double scale, int minClientWidth = 640, int minClientHeight = 480)
    {
        if (message == WmGetMinMaxInfo && data != IntPtr.Zero)
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(data);
            double effectiveScale = scale > 0 ? scale : 1.0;
            var rect = new Rect
            {
                Right = (int)Math.Ceiling(minClientWidth * effectiveScale),
                Bottom = (int)Math.Ceiling(minClientHeight * effectiveScale),
            };
            AdjustWindowRectExForDpi(
                ref rect,
                (uint)GetWindowLongW(window, GwlStyle),
                false,
                (uint)GetWindowLongW(window, GwlExStyle),
                (uint)Math.Round(96 * effectiveScale));
            limits.MinX = rect.Right - rect.Left;
            limits.MinY = rect.Bottom - rect.Top;
            Marshal.StructureToPtr(limits, data, false);
        }
        else if (message == WmDpiChanged && data != IntPtr.Zero)
        {
            var rect = Marshal.PtrToStructure<Rect>(data);
            SetWindowPos(
                window,
                IntPtr.Zero,
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                SwpNoZOrder | SwpNoActivate);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX, ReservedY, MaxX, MaxY, PositionX, PositionY, MinX, MinY, MaxTrackX, MaxTrackY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
