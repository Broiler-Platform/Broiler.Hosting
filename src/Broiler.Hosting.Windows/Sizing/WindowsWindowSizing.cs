using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.Native.Windows;

namespace Broiler.Hosting.Windows;

/// <summary>
/// Window sizing, minimum dimension enforcement, and multi-monitor DPI transition handling.
/// </summary>
[SupportedOSPlatform("windows5.0")]
public static class WindowsWindowSizing
{
    public static void OnMessage(IntPtr window, uint message, IntPtr data, double scale, int minClientWidth = 640, int minClientHeight = 480)
    {
        if (message == WindowNative.WmGetMinMaxInfo && data != IntPtr.Zero)
        {
            var limits = Marshal.PtrToStructure<WindowNative.MINMAXINFO>(data);
            double effectiveScale = scale > 0 ? scale : 1.0;
            var rect = new WindowNative.RECT(
                0,
                0,
                (int)Math.Ceiling(minClientWidth * effectiveScale),
                (int)Math.Ceiling(minClientHeight * effectiveScale));
            WindowNative.AdjustWindowRectExForDpi(
                ref rect,
                (uint)WindowNative.GetWindowLong32(window, WindowNative.GwlStyle),
                false,
                (uint)WindowNative.GetWindowLong32(window, WindowNative.GwlExStyle),
                (uint)Math.Round(96 * effectiveScale));
            limits.ptMinTrackSize = new WindowNative.POINT { X = rect.Right - rect.Left, Y = rect.Bottom - rect.Top };
            Marshal.StructureToPtr(limits, data, false);
        }
        else if (message == WindowNative.WmDpiChanged && data != IntPtr.Zero)
        {
            var rect = Marshal.PtrToStructure<WindowNative.RECT>(data);
            WindowNative.SetWindowPos(
                window,
                IntPtr.Zero,
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                WindowNative.SwpNoZOrder | WindowNative.SwpNoActivate);
        }
    }
}
