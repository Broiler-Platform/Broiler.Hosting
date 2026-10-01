using System;
using System.Runtime.InteropServices;
using Broiler.Hosting.Windows;
using Broiler.UI;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

public sealed class WindowsHostingIntegrationTests
{
    [Fact]
    public void Clipboard_Rejects_Null_And_Oversized_Text()
    {
        var clipboard = new WindowsClipboard(0);

        Assert.Throws<ArgumentNullException>(() => clipboard.SetText(null!));

        // Text equal to or exceeding 512,000 characters (1 MB of UTF-16) is refused
        string oversized = new('x', 512 * 1024);
        clipboard.SetText(oversized); // Must not throw, simply refused
    }

    [Fact]
    public void Clipboard_Supports_Lazy_Owner_Evaluation()
    {
        int evaluationCount = 0;
        nint handle = 0x1234;
        var clipboard = new WindowsClipboard(() =>
        {
            evaluationCount++;
            return handle;
        });

        Assert.Equal(0, evaluationCount);
        // Attempting to read when clipboard cannot be opened on fake handle
        bool result = clipboard.TryGetText(out string text);
        Assert.False(result);
        Assert.Equal(string.Empty, text);
        Assert.True(evaluationCount > 0);
    }

    [Fact]
    public void WindowsWindowSizing_Sets_Minimum_Dimensions_On_GetMinMaxInfo()
    {
        // 40-byte MINMAXINFO structure
        nint memory = Marshal.AllocHGlobal(40);
        try
        {
            // Zero out memory
            for (int i = 0; i < 40; i++)
                Marshal.WriteByte(memory, i, 0);

            // WM_GETMINMAXINFO = 0x0024 at scale 1.0 (96 DPI)
            WindowsWindowSizing.OnMessage(0, 0x0024, memory, 1.0);

            // Read MinX and MinY (offsets 24 and 28 in MINMAXINFO)
            int minX = Marshal.ReadInt32(memory, 24);
            int minY = Marshal.ReadInt32(memory, 28);

            Assert.True(minX >= 640, $"Expected MinX >= 640, got {minX}");
            Assert.True(minY >= 480, $"Expected MinY >= 480, got {minY}");
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void WindowsWindowSizing_Scales_Minimum_Dimensions_For_HighDpi()
    {
        nint memory = Marshal.AllocHGlobal(40);
        try
        {
            for (int i = 0; i < 40; i++)
                Marshal.WriteByte(memory, i, 0);

            // WM_GETMINMAXINFO at scale 2.0 (192 DPI)
            WindowsWindowSizing.OnMessage(0, 0x0024, memory, 2.0);

            int minX = Marshal.ReadInt32(memory, 24);
            int minY = Marshal.ReadInt32(memory, 28);

            Assert.True(minX >= 1280, $"Expected MinX >= 1280, got {minX}");
            Assert.True(minY >= 960, $"Expected MinY >= 960, got {minY}");
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void WindowsWindowSizing_Handles_DpiChanged_Without_Error()
    {
        // 16-byte RECT structure
        nint memory = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(memory, 0, 100);  // Left
            Marshal.WriteInt32(memory, 4, 100);  // Top
            Marshal.WriteInt32(memory, 8, 900);  // Right
            Marshal.WriteInt32(memory, 12, 700); // Bottom

            // WM_DPICHANGED = 0x02E0 with null window handle
            WindowsWindowSizing.OnMessage(0, 0x02E0, memory, 1.5);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void WindowsTheme_Queries_System_Settings()
    {
        UiSystemSettings settings = WindowsTheme.QuerySystemSettings();
        Assert.NotNull(settings);
        Assert.True(settings.TextScale >= 1.0);
        Assert.Equal(UiFlowDirection.LeftToRight, settings.FlowDirection);
        Assert.Equal(UiDensity.Comfortable, settings.Density);
        Assert.True(settings.ColorScheme is UiColorScheme.Light or UiColorScheme.Dark);
    }
}
