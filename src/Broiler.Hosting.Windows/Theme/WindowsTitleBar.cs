using System;
using Broiler.Native.Windows;

namespace Broiler.Hosting.Windows;

/// <summary>
/// Matches the native window caption to the application palette. Windows keeps a light caption for
/// dark content unless the window opts in, which makes a dark theme look only partly applied.
/// </summary>
public static class WindowsTitleBar
{
    /// <summary>
    /// Requests a dark or light caption for <paramref name="window"/>. Call it after the native window
    /// exists (for a Direct2DWindow, after Show) and again whenever the applied palette changes.
    /// </summary>
    /// <returns>False when the handle is zero, the system predates Windows 10 build 19041, or DWM rejects the call.</returns>
    public static bool ApplyDarkMode(nint window, bool dark)
    {
        if (window == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return false;

        try
        {
            int value = dark ? 1 : 0;
            return DwmNative.DwmSetWindowAttribute(window, DwmNative.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
