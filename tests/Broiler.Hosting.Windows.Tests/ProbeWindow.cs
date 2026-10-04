using System;
using System.Collections.Generic;
using Broiler.Native.Windows;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>
/// A hidden top-level window on the test's own thread, never shown or activated. Its subclass, installed
/// before anything the test attaches, records what an attached bridge passes on to the window procedure.
/// Recorded messages stop at the probe unless listed in <see cref="PassedOn"/>; everything else goes on.
/// </summary>
internal sealed class ProbeWindow : IDisposable
{
    private readonly WindowNative.SubclassProc _probe;
    private readonly HashSet<uint> _recorded;

    public ProbeWindow(params uint[] recorded)
    {
        _recorded = new HashSet<uint>(recorded);
        Handle = WindowNative.CreateWindowEx(0, "STATIC", string.Empty, 0, 0, 0, 100, 100, 0, 0, WindowNative.GetModuleHandle(null), 0);
        Assert.NotEqual(0, Handle);
        _probe = Probe;
        Assert.True(WindowNative.SetWindowSubclass(Handle, _probe, 1, 0));
    }

    public nint Handle { get; }

    /// <summary>The recorded messages that reached the probe, in order.</summary>
    public List<(uint Message, nint WParam, nint LParam)> Received { get; } = [];

    /// <summary>Recorded messages that go on to the window's own procedure, and so to DefWindowProc.</summary>
    public HashSet<uint> PassedOn { get; } = [];

    /// <summary>Sends a message to the window, as Windows does: synchronously, on this thread.</summary>
    public nint Send(uint message, nint wParam, nint lParam) => WindowNative.SendMessage(Handle, message, wParam, lParam);

    private nint Probe(nint hWnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (!_recorded.Contains(message))
            return WindowNative.DefSubclassProc(hWnd, message, wParam, lParam);
        Received.Add((message, wParam, lParam));
        return PassedOn.Contains(message) ? WindowNative.DefSubclassProc(hWnd, message, wParam, lParam) : 0;
    }

    public void Dispose()
    {
        WindowNative.RemoveWindowSubclass(Handle, _probe, 1);
        WindowNative.DestroyWindow(Handle);
    }
}
