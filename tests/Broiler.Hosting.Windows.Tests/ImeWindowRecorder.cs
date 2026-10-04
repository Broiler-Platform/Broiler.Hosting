using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Broiler.Native.Windows;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>
/// Records what reaches the thread's default IME window, the window DefWindowProc hands IME messages to and
/// that shows the IME's own composition window. Everything is passed on unchanged.
/// </summary>
internal sealed partial class ImeWindowRecorder : IDisposable
{
    /// <summary>SM_IMMENABLED: nonzero when the system supports IMEs.</summary>
    public const int SmImmEnabled = 82;

    private readonly WindowNative.SubclassProc _recorder;
    private readonly HashSet<uint> _recorded;
    private readonly nint _imeWindow;

    public ImeWindowRecorder(nint imeWindow, params uint[] recorded)
    {
        _imeWindow = imeWindow;
        _recorded = new HashSet<uint>(recorded);
        _recorder = Record;
        Assert.True(WindowNative.SetWindowSubclass(_imeWindow, _recorder, 1, 0));
    }

    public List<(uint Message, nint WParam, nint LParam)> Received { get; } = [];

    public static nint DefaultImeWindow(nint window) => ImmGetDefaultIMEWnd(window);

    private nint Record(nint hWnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (_recorded.Contains(message))
            Received.Add((message, wParam, lParam));
        return WindowNative.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    public void Dispose() => WindowNative.RemoveWindowSubclass(_imeWindow, _recorder, 1);

    [LibraryImport("imm32.dll")]
    private static partial nint ImmGetDefaultIMEWnd(nint hWnd);
}
