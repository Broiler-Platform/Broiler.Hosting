using System.Runtime.Versioning;
using Broiler.Native.Windows;

namespace Broiler.Hosting.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsPointerCursor
{
    private static readonly nint Arrow = WindowNative.LoadCursor(0, 32512);
    private static readonly nint Text = WindowNative.LoadCursor(0, 32513);

    public static void SetTextCursor(bool overText)
    {
        nint cursor = overText ? Text : Arrow;
        if (cursor != 0)
            WindowNative.SetCursor(cursor);
    }
}
