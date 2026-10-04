using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Broiler.Native.Linux.OpenGL;
using Broiler.UI;

namespace Broiler.Hosting.Linux;

/// <summary>
/// The X11 CLIPBOARD and PRIMARY selection, as the UI layer's clipboard port for Linux.
///
/// Enforces 1 MB boundary protection, safe property buffer validation, ICCCM-compliant
/// UTF8_STRING and STRING conversion, chunked INCR protocol support, and timeout guards.
/// </summary>
public sealed class LinuxX11Clipboard : IUiClipboardHost, IDisposable
{
    private const int MaximumBytes = 1024 * 1024; // 1 MB boundary protection

    /// <summary>
    /// How long a paste waits for the owning application to answer.
    /// </summary>
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromSeconds(1);

    private readonly IntPtr _display;
    private readonly IntPtr _window;
    private readonly IntPtr _clipboard;
    private readonly IntPtr _primary;
    private readonly IntPtr _targets;
    private readonly IntPtr _utf8String;
    private readonly IntPtr _text;
    private readonly IntPtr _incr;
    private readonly IntPtr _transferProperty;
    private string? _ownedText;
    private bool _isDisposed;

    internal LinuxX11Clipboard(IntPtr display, IntPtr window)
    {
        _display = display;
        _window = window;
        _clipboard = InternAtom(display, "CLIPBOARD");
        _primary = InternAtom(display, "PRIMARY");
        _targets = InternAtom(display, "TARGETS");
        _utf8String = InternAtom(display, "UTF8_STRING");
        _text = InternAtom(display, "TEXT");
        _incr = InternAtom(display, "INCR");
        _transferProperty = InternAtom(display, "BROILER_CLIPBOARD");
    }

    /// <summary>
    /// Connects to the X display and creates the unmapped 1x1 window that owns the selection.
    /// Returns null when not on Linux or when there is no X display.
    /// </summary>
    public static LinuxX11Clipboard? TryOpen()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        IntPtr display;
        try
        {
            display = LinuxX11Native.OpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return null;
        }

        if (display == IntPtr.Zero)
            return null;

        int screen = LinuxX11Native.DefaultScreen(display);
        IntPtr window = LinuxX11Native.CreateSimpleWindow(display, LinuxX11Native.RootWindow(display, screen), 0, 0, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            LinuxX11Native.CloseDisplay(display);
            return null;
        }

        LinuxX11Native.SelectInput(display, window, LinuxX11Native.PropertyChangeMask);
        LinuxX11Native.Flush(display);
        return new LinuxX11Clipboard(display, window);
    }

    /// <summary>Whether this process currently owns the CLIPBOARD selection.</summary>
    public bool OwnsClipboard => !_isDisposed && LinuxX11Native.GetSelectionOwner(_display, _clipboard) == _window;

    public bool TryGetText(out string text)
    {
        text = string.Empty;
        if (_isDisposed)
            return false;

        IntPtr owner = LinuxX11Native.GetSelectionOwner(_display, _clipboard);
        if (owner == IntPtr.Zero)
            return false;

        if (owner == _window)
        {
            text = _ownedText ?? string.Empty;
            return text.Length > 0;
        }

        if (TryConvert(_utf8String, Encoding.UTF8, out text) && text.Length > 0)
            return true;
        if (TryConvert(new IntPtr(LinuxX11Native.XaString), Encoding.Latin1, out text) && text.Length > 0)
            return true;

        text = string.Empty;
        return false;
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_isDisposed)
            return;

        // 1 MB boundary protection
        if (text.Length >= MaximumBytes / 2)
            return;

        _ownedText = text;
        LinuxX11Native.SetSelectionOwner(_display, _clipboard, _window, IntPtr.Zero);

        if (LinuxX11Native.GetSelectionOwner(_display, _clipboard) != _window)
        {
            _ownedText = null;
            return;
        }

        LinuxX11Native.SetSelectionOwner(_display, _primary, _window, IntPtr.Zero);
        LinuxX11Native.Flush(_display);
    }

    /// <summary>
    /// Answers whatever the server has queued for this connection.
    /// </summary>
    public void ProcessPendingEvents()
    {
        if (_isDisposed)
            return;

        while (LinuxX11Native.Pending(_display) > 0)
        {
            LinuxX11Native.NextEvent(_display, out LinuxX11Native.XEvent nextEvent);
            HandleEvent(ref nextEvent);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _ownedText = null;

        if (OperatingSystem.IsLinux() && _display != IntPtr.Zero)
        {
            if (LinuxX11Native.GetSelectionOwner(_display, _clipboard) == _window)
                LinuxX11Native.SetSelectionOwner(_display, _clipboard, IntPtr.Zero, IntPtr.Zero);
            if (LinuxX11Native.GetSelectionOwner(_display, _primary) == _window)
                LinuxX11Native.SetSelectionOwner(_display, _primary, IntPtr.Zero, IntPtr.Zero);

            LinuxX11Native.DestroyWindow(_display, _window);
            LinuxX11Native.CloseDisplay(_display);
        }
    }

    private void HandleEvent(ref LinuxX11Native.XEvent nextEvent)
    {
        switch (nextEvent.Type)
        {
            case LinuxX11Native.SelectionRequest:
                AnswerSelectionRequest(ref Unsafe.As<LinuxX11Native.XEvent, LinuxX11Native.XSelectionRequestEvent>(ref nextEvent));
                break;

            case LinuxX11Native.SelectionClear:
                _ownedText = null;
                break;
        }
    }

    private void AnswerSelectionRequest(ref LinuxX11Native.XSelectionRequestEvent request)
    {
        IntPtr property = request.Property == IntPtr.Zero ? request.Target : request.Property;
        bool answered = _ownedText is string owned && TryWriteRequestedTarget(ref request, property, owned);

        var reply = new LinuxX11Native.XSelectionEvent
        {
            Type = LinuxX11Native.SelectionNotify,
            Display = request.Display,
            Requestor = request.Requestor,
            Selection = request.Selection,
            Target = request.Target,
            Property = answered ? property : IntPtr.Zero,
            Time = request.Time,
        };

        LinuxX11Native.XEvent sendEvent = default;
        Unsafe.As<LinuxX11Native.XEvent, LinuxX11Native.XSelectionEvent>(ref sendEvent) = reply;

        LinuxX11Native.SendEvent(_display, request.Requestor, 0, 0, ref sendEvent);
        LinuxX11Native.Flush(_display);
    }

    private bool TryWriteRequestedTarget(ref LinuxX11Native.XSelectionRequestEvent request, IntPtr property, string owned)
    {
        if (request.Target == _targets)
        {
            IntPtr[] supported = [_targets, _utf8String, new IntPtr(LinuxX11Native.XaString), _text];
            LinuxX11Native.ChangeProperty(_display, request.Requestor, property, new IntPtr(LinuxX11Native.XaAtom), 32, LinuxX11Native.PropModeReplace, supported, supported.Length);
            return true;
        }

        Encoding? encoding = null;
        if (request.Target == _utf8String || request.Target == _text)
            encoding = Encoding.UTF8;
        else if (request.Target == new IntPtr(LinuxX11Native.XaString))
            encoding = Encoding.Latin1;

        if (encoding is null)
            return false;

        byte[] bytes = encoding.GetBytes(owned);

        if (bytes.Length > MaxPropertyBytes())
            return false;

        LinuxX11Native.ChangeProperty(_display, request.Requestor, property, request.Target, 8, LinuxX11Native.PropModeReplace, bytes, bytes.Length);
        return true;
    }

    private long MaxPropertyBytes()
    {
        long units = LinuxX11Native.ExtendedMaxRequestSize(_display);
        if (units <= 0)
            units = LinuxX11Native.MaxRequestSize(_display);

        return Math.Max(0, (units * 4) - 1024);
    }

    private bool TryConvert(IntPtr target, Encoding encoding, out string text)
    {
        text = string.Empty;
        LinuxX11Native.DeleteProperty(_display, _window, _transferProperty);
        LinuxX11Native.ConvertSelection(_display, _clipboard, target, _transferProperty, _window, IntPtr.Zero);
        LinuxX11Native.Flush(_display);

        if (!TryWaitForSelectionNotify(target, out bool refused) || refused)
            return false;

        if (!TryReadProperty(delete: true, out byte[] data, out IntPtr type))
            return false;

        if (type == _incr)
            return TryReadIncrementally(encoding, out text);

        text = encoding.GetString(data);
        return true;
    }

    private bool TryWaitForSelectionNotify(IntPtr target, out bool refused)
    {
        refused = false;
        long deadline = Deadline();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (LinuxX11Native.Pending(_display) == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            LinuxX11Native.NextEvent(_display, out LinuxX11Native.XEvent nextEvent);
            if (nextEvent.Type != LinuxX11Native.SelectionNotify)
            {
                HandleEvent(ref nextEvent);
                continue;
            }

            ref LinuxX11Native.XSelectionEvent notify = ref Unsafe.As<LinuxX11Native.XEvent, LinuxX11Native.XSelectionEvent>(ref nextEvent);
            if (notify.Requestor != _window || notify.Target != target)
                continue;

            refused = notify.Property == IntPtr.Zero;
            return true;
        }

        return false;
    }

    private bool TryReadIncrementally(Encoding encoding, out string text)
    {
        text = string.Empty;
        using var buffer = new MemoryStream();

        long deadline = Deadline();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (LinuxX11Native.Pending(_display) == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            LinuxX11Native.NextEvent(_display, out LinuxX11Native.XEvent nextEvent);
            if (nextEvent.Type != LinuxX11Native.PropertyNotify)
            {
                HandleEvent(ref nextEvent);
                continue;
            }

            ref LinuxX11Native.XPropertyEvent property = ref Unsafe.As<LinuxX11Native.XEvent, LinuxX11Native.XPropertyEvent>(ref nextEvent);
            if (property.Window != _window || property.Atom != _transferProperty || property.State != LinuxX11Native.PropertyNewValue)
                continue;

            if (!TryReadProperty(delete: true, out byte[] chunk, out _))
                return false;
            if (chunk.Length == 0)
            {
                text = encoding.GetString(buffer.ToArray());
                return true;
            }

            buffer.Write(chunk, 0, chunk.Length);
            deadline = Deadline();
        }

        return false;
    }

    private static long Deadline() =>
        Stopwatch.GetTimestamp() + (long)(ConvertTimeout.TotalSeconds * Stopwatch.Frequency);

    private bool TryReadProperty(bool delete, out byte[] data, out IntPtr type)
    {
        data = [];
        type = IntPtr.Zero;

        if (LinuxX11Native.GetWindowProperty(
                _display,
                _window,
                _transferProperty,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                out type,
                out int format,
                out IntPtr items,
                out IntPtr remaining,
                out IntPtr peek) != 0)
        {
            return false;
        }

        if (peek != IntPtr.Zero)
            LinuxX11Native.Free(peek);
        if (type == IntPtr.Zero)
            return false;

        long bytes = (long)remaining;
        var length = new IntPtr((bytes + 3) / 4);
        if (LinuxX11Native.GetWindowProperty(
                _display,
                _window,
                _transferProperty,
                IntPtr.Zero,
                length,
                delete ? 1 : 0,
                IntPtr.Zero,
                out type,
                out format,
                out items,
                out remaining,
                out IntPtr value) != 0)
        {
            return false;
        }

        if (value == IntPtr.Zero)
            return type != IntPtr.Zero;

        try
        {
            long count = format switch
            {
                32 => (long)items * IntPtr.Size,
                16 => (long)items * 2,
                _ => (long)items,
            };
            if (count <= 0)
                return true;

            data = new byte[count];
            Marshal.Copy(value, data, 0, data.Length);
            return true;
        }
        finally
        {
            LinuxX11Native.Free(value);
        }
    }

    private static IntPtr InternAtom(IntPtr display, string name) => LinuxX11Native.InternAtom(display, name, 0);
}
