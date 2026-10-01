using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
    private const int SelectionClear = 29;
    private const int SelectionRequest = 30;
    private const int SelectionNotify = 31;
    private const int PropertyNotify = 28;

    private const int PropertyNewValue = 0;
    private const long PropertyChangeMask = 1L << 22;
    private const int PropModeReplace = 0;
    private const int XaAtom = 4;
    private const int XaString = 31;

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
            display = XOpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return null;
        }

        if (display == IntPtr.Zero)
            return null;

        int screen = XDefaultScreen(display);
        IntPtr window = XCreateSimpleWindow(display, XRootWindow(display, screen), 0, 0, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            XCloseDisplay(display);
            return null;
        }

        XSelectInput(display, window, PropertyChangeMask);
        XFlush(display);
        return new LinuxX11Clipboard(display, window);
    }

    /// <summary>Whether this process currently owns the CLIPBOARD selection.</summary>
    public bool OwnsClipboard => !_isDisposed && XGetSelectionOwner(_display, _clipboard) == _window;

    public bool TryGetText(out string text)
    {
        text = string.Empty;
        if (_isDisposed)
            return false;

        IntPtr owner = XGetSelectionOwner(_display, _clipboard);
        if (owner == IntPtr.Zero)
            return false;

        if (owner == _window)
        {
            text = _ownedText ?? string.Empty;
            return text.Length > 0;
        }

        if (TryConvert(_utf8String, Encoding.UTF8, out text) && text.Length > 0)
            return true;
        if (TryConvert(new IntPtr(XaString), Encoding.Latin1, out text) && text.Length > 0)
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
        XSetSelectionOwner(_display, _clipboard, _window, IntPtr.Zero);

        if (XGetSelectionOwner(_display, _clipboard) != _window)
        {
            _ownedText = null;
            return;
        }

        XSetSelectionOwner(_display, _primary, _window, IntPtr.Zero);
        XFlush(_display);
    }

    /// <summary>
    /// Answers whatever the server has queued for this connection.
    /// </summary>
    public void ProcessPendingEvents()
    {
        if (_isDisposed)
            return;

        while (XPending(_display) > 0)
        {
            XNextEvent(_display, out XEvent nextEvent);
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
            if (XGetSelectionOwner(_display, _clipboard) == _window)
                XSetSelectionOwner(_display, _clipboard, IntPtr.Zero, IntPtr.Zero);
            if (XGetSelectionOwner(_display, _primary) == _window)
                XSetSelectionOwner(_display, _primary, IntPtr.Zero, IntPtr.Zero);

            XDestroyWindow(_display, _window);
            XCloseDisplay(_display);
        }
    }

    private void HandleEvent(ref XEvent nextEvent)
    {
        switch (nextEvent.Type)
        {
            case SelectionRequest:
                AnswerSelectionRequest(ref Unsafe.As<XEvent, XSelectionRequestEvent>(ref nextEvent));
                break;

            case SelectionClear:
                _ownedText = null;
                break;
        }
    }

    private void AnswerSelectionRequest(ref XSelectionRequestEvent request)
    {
        IntPtr property = request.Property == IntPtr.Zero ? request.Target : request.Property;
        bool answered = _ownedText is string owned && TryWriteRequestedTarget(ref request, property, owned);

        var reply = new XSelectionEvent
        {
            Type = SelectionNotify,
            Display = request.Display,
            Requestor = request.Requestor,
            Selection = request.Selection,
            Target = request.Target,
            Property = answered ? property : IntPtr.Zero,
            Time = request.Time,
        };

        XEvent sendEvent = default;
        Unsafe.As<XEvent, XSelectionEvent>(ref sendEvent) = reply;

        XSendEvent(_display, request.Requestor, 0, 0, ref sendEvent);
        XFlush(_display);
    }

    private bool TryWriteRequestedTarget(ref XSelectionRequestEvent request, IntPtr property, string owned)
    {
        if (request.Target == _targets)
        {
            IntPtr[] supported = [_targets, _utf8String, new IntPtr(XaString), _text];
            XChangeProperty(_display, request.Requestor, property, new IntPtr(XaAtom), 32, PropModeReplace, supported, supported.Length);
            return true;
        }

        Encoding? encoding = null;
        if (request.Target == _utf8String || request.Target == _text)
            encoding = Encoding.UTF8;
        else if (request.Target == new IntPtr(XaString))
            encoding = Encoding.Latin1;

        if (encoding is null)
            return false;

        byte[] bytes = encoding.GetBytes(owned);

        if (bytes.Length > MaxPropertyBytes())
            return false;

        XChangeProperty(_display, request.Requestor, property, request.Target, 8, PropModeReplace, bytes, bytes.Length);
        return true;
    }

    private long MaxPropertyBytes()
    {
        long units = XExtendedMaxRequestSize(_display);
        if (units <= 0)
            units = XMaxRequestSize(_display);

        return Math.Max(0, (units * 4) - 1024);
    }

    private bool TryConvert(IntPtr target, Encoding encoding, out string text)
    {
        text = string.Empty;
        XDeleteProperty(_display, _window, _transferProperty);
        XConvertSelection(_display, _clipboard, target, _transferProperty, _window, IntPtr.Zero);
        XFlush(_display);

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
            if (XPending(_display) == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            XNextEvent(_display, out XEvent nextEvent);
            if (nextEvent.Type != SelectionNotify)
            {
                HandleEvent(ref nextEvent);
                continue;
            }

            ref XSelectionEvent notify = ref Unsafe.As<XEvent, XSelectionEvent>(ref nextEvent);
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
            if (XPending(_display) == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            XNextEvent(_display, out XEvent nextEvent);
            if (nextEvent.Type != PropertyNotify)
            {
                HandleEvent(ref nextEvent);
                continue;
            }

            ref XPropertyEvent property = ref Unsafe.As<XEvent, XPropertyEvent>(ref nextEvent);
            if (property.Window != _window || property.Atom != _transferProperty || property.State != PropertyNewValue)
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

        if (XGetWindowProperty(
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
            XFree(peek);
        if (type == IntPtr.Zero)
            return false;

        long bytes = (long)remaining;
        var length = new IntPtr((bytes + 3) / 4);
        if (XGetWindowProperty(
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
            XFree(value);
        }
    }

    private static IntPtr InternAtom(IntPtr display, string name) => XInternAtom(display, name, 0);

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct XEvent
    {
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSelectionRequestEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Owner;
        public IntPtr Requestor;
        public IntPtr Selection;
        public IntPtr Target;
        public IntPtr Property;
        public IntPtr Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSelectionEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Requestor;
        public IntPtr Selection;
        public IntPtr Target;
        public IntPtr Property;
        public IntPtr Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XPropertyEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr Atom;
        public IntPtr Time;
        public int State;
    }

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XDefaultScreen")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XRootWindow")]
    private static extern IntPtr XRootWindow(IntPtr display, int screen);

    [DllImport("libX11.so.6", EntryPoint = "XCreateSimpleWindow")]
    private static extern IntPtr XCreateSimpleWindow(
        IntPtr display,
        IntPtr parent,
        int x,
        int y,
        uint width,
        uint height,
        uint borderWidth,
        IntPtr border,
        IntPtr background);

    [DllImport("libX11.so.6", EntryPoint = "XDestroyWindow")]
    private static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6", EntryPoint = "XSelectInput")]
    private static extern int XSelectInput(IntPtr display, IntPtr window, long mask);

    [DllImport("libX11.so.6", EntryPoint = "XInternAtom")]
    private static extern IntPtr XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);

    [DllImport("libX11.so.6", EntryPoint = "XSetSelectionOwner")]
    private static extern int XSetSelectionOwner(IntPtr display, IntPtr selection, IntPtr owner, IntPtr time);

    [DllImport("libX11.so.6", EntryPoint = "XGetSelectionOwner")]
    private static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection);

    [DllImport("libX11.so.6", EntryPoint = "XConvertSelection")]
    private static extern int XConvertSelection(IntPtr display, IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, IntPtr time);

    [DllImport("libX11.so.6", EntryPoint = "XChangeProperty")]
    private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, byte[] data, int elements);

    [DllImport("libX11.so.6", EntryPoint = "XChangeProperty")]
    private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, IntPtr[] data, int elements);

    [DllImport("libX11.so.6", EntryPoint = "XDeleteProperty")]
    private static extern int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);

    [DllImport("libX11.so.6", EntryPoint = "XGetWindowProperty")]
    private static extern int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        IntPtr offset,
        IntPtr length,
        int delete,
        IntPtr requestedType,
        out IntPtr actualType,
        out int actualFormat,
        out IntPtr items,
        out IntPtr bytesAfter,
        out IntPtr value);

    [DllImport("libX11.so.6", EntryPoint = "XSendEvent")]
    private static extern int XSendEvent(IntPtr display, IntPtr window, int propagate, long mask, ref XEvent sendEvent);

    [DllImport("libX11.so.6", EntryPoint = "XMaxRequestSize")]
    private static extern long XMaxRequestSize(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XExtendedMaxRequestSize")]
    private static extern long XExtendedMaxRequestSize(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XPending")]
    private static extern int XPending(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XNextEvent")]
    private static extern int XNextEvent(IntPtr display, out XEvent nextEvent);

    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XFree")]
    private static extern int XFree(IntPtr data);
}
