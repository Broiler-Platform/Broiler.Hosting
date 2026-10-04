using System;
using System.Runtime.InteropServices;
using System.Text;
using Broiler.Graphics.Geometry;
using Broiler.Input;
using Broiler.Input.Mouse;
using Broiler.Input.Text;
using Broiler.Native.Windows;
using Broiler.Native.Windows.Input;
using Broiler.UI;

namespace Broiler.Hosting.Windows.Input;

/// <summary>
/// Native input and scroll fidelity bridge for Windows Direct2D application windows.
/// Preserves exactly-once text delivery across WM_CHAR and IME composition,
/// handles surrogate pairs, isolates shortcut control chords from text generation,
/// supports precision wheel deltas and horizontal scrolling, and manages activation focus handoff.
/// </summary>
public sealed class WindowsInputBridge : IDisposable
{
    private static nuint _subclassCounter;
    private readonly nuint _subclassId;
    private readonly WindowNative.SubclassProc? _subclassProc;

    private readonly nint _topLevelHwnd;
    private readonly nint _renderHwnd;
    private readonly UiSession _session;
    private readonly Func<UiInputEvent, bool>? _inputFilter;
    private readonly Func<double> _scaleProvider;
    private readonly Action? _invalidateAction;
    private readonly InputDeviceId _deviceId = InputDeviceId.FromOpaqueValue("windows-input-bridge");
    private long _sequence;

    private bool _isDisposed;
    private char _pendingHighSurrogate;
    private bool _deadKeyActive;
    private bool _isComposing;
    // The WM_CHAR copies Windows sends after an IME commit, still expected, and when the commit's
    // dispatch finished. The copies follow the commit at once and in order, so the first other
    // character, a new composition, or a focus change ends the suppression; the time limit is a backstop.
    // DefWindowProc makes the copies from a commit it is passed. With DrawsCompositionInline the bridge
    // keeps the commit from it and none come, but the suppression is armed all the same, for a host that
    // passes the commit on itself. The cost: the commit's first character typed again as plain text, before
    // anything else and within the time limit, is taken for a copy.
    private string _lastCommittedImeString = string.Empty;
    private long _lastCommittedImeTimestamp;
    private static readonly TimeSpan CommittedCopyWindow = TimeSpan.FromMilliseconds(500);

    // ISC_SHOWUICOMPOSITIONWINDOW in WM_IME_SETCONTEXT: the IME's own window shows the composition string.
    private const long IscShowUiCompositionWindow = 0x80000000L;

    /// <summary>
    /// Whether the application draws the IME composition itself, as Broiler.UI's editors do; true by default.
    /// The IME then does not show it a second time in its own composition window: WM_IME_SETCONTEXT reaches
    /// DefWindowProc without ISC_SHOWUICOMPOSITIONWINDOW, and WM_IME_STARTCOMPOSITION and the
    /// WM_IME_COMPOSITION messages the bridge has read do not reach it at all, so DefWindowProc makes no
    /// WM_IME_CHAR or WM_CHAR copies of a commit either. The IME's candidate list and guide still show.
    /// </summary>
    /// <remarks>
    /// Only messages that reach the bridge through its subclass of the render window are affected, and
    /// WM_IME_SETCONTEXT follows a change from the next time the window's input context is activated.
    /// A control that draws no composition, such as a password field, should have the IME turned off while
    /// it has the focus, as native password boxes do; otherwise its composition is shown nowhere.
    /// </remarks>
    public bool DrawsCompositionInline { get; set; } = true;

    // Testability hooks
    public Func<int, short> KeyStateProvider { get; set; } = WindowNative.GetKeyState;
    public Func<nint, uint, string> CompositionStringProvider { get; set; } = ReadCompositionStringFromImm;
    /// <summary>The clock for the backstop that ends suppression of an IME commit's WM_CHAR copies.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public Action<nint>? SetFocusAction { get; set; } = hwnd => { if (hwnd != nint.Zero) WindowNative.SetFocus(hwnd); };
    public Func<nint, WindowNative.POINT, WindowNative.POINT> ScreenToClientAction { get; set; } = (hwnd, pt) =>
    {
        if (hwnd != nint.Zero)
            WindowNative.ScreenToClient(hwnd, ref pt);
        return pt;
    };
    public event Action<UiInputEvent>? EventDispatched;

    public nint TopLevelHwnd => _topLevelHwnd;
    public nint RenderHwnd => _renderHwnd;
    public bool IsDisposed => _isDisposed;
    public bool IsComposing => _isComposing;
    public bool DeadKeyActive => _deadKeyActive;
    public char PendingHighSurrogate => _pendingHighSurrogate;

    /// <summary>Subclasses <paramref name="renderHwnd"/> for IME, surrogate, and precision-wheel input.</summary>
    /// <remarks>
    /// Both handles must belong to windows that already exist; with zero handles nothing is attached and
    /// only the managed text path works. With a Broiler.Graphics <c>Direct2DWindow</c>, construct the
    /// bridge in <c>OnCreated</c>, not in the window's constructor.
    /// </remarks>
    public WindowsInputBridge(
        nint topLevelHwnd,
        nint renderHwnd,
        UiSession session,
        Func<UiInputEvent, bool>? inputFilter = null,
        Func<double>? scaleProvider = null,
        Action? invalidateAction = null)
    {
        _topLevelHwnd = topLevelHwnd;
        _renderHwnd = renderHwnd;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _inputFilter = inputFilter;
        _scaleProvider = scaleProvider ?? (() => 1.0);
        _invalidateAction = invalidateAction;

        if (_renderHwnd != nint.Zero)
        {
            _subclassId = ++_subclassCounter;
            _subclassProc = SubclassWindowProc;
            WindowNative.SetWindowSubclass(_renderHwnd, _subclassProc, _subclassId, 0);
        }
    }

    public void OnTopLevelMessage(uint message, nint wParam, nint lParam)
    {
        if (_isDisposed || _renderHwnd == nint.Zero)
            return;

        if (message == WindowNative.WmSetFocus)
        {
            SetFocusAction?.Invoke(_renderHwnd);
        }
        else if (message == WindowNative.WmActivate)
        {
            int activation = (int)(wParam.ToInt64() & 0xFFFF);
            if (activation != WindowNative.WaInactive)
            {
                SetFocusAction?.Invoke(_renderHwnd);
            }
        }
    }

    private nint SubclassWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (_isDisposed)
            return WindowNative.DefSubclassProc(hWnd, uMsg, wParam, lParam);

        return ProcessNativeMessage(hWnd, uMsg, wParam, lParam, fromSubclass: true);
    }

    public nint ProcessNativeMessage(nint hWnd, uint uMsg, nint wParam, nint lParam, bool fromSubclass = false)
    {
        switch (uMsg)
        {
            case WindowNative.WmKillFocus:
                if (_isComposing)
                {
                    _isComposing = false;
                    Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                        NextHeader("text"),
                        string.Empty,
                        TextCompositionState.Cancelled,
                        Source: InputEventSource.Synthetic)));
                }
                _pendingHighSurrogate = '\0';
                _deadKeyActive = false;
                _lastCommittedImeString = string.Empty;
                break;

            // DefWindowProc passes this on to the default IME window, which shows the IME's windows its display
            // options name. Drawn inline, the composition must not show there too; candidates and guide still do.
            case ImmNative.WM_IME_SETCONTEXT when DrawsCompositionInline && fromSubclass:
                return WindowNative.DefSubclassProc(hWnd, uMsg, wParam, (nint)(lParam.ToInt64() & ~IscShowUiCompositionWindow));

            case ImmNative.WM_IME_STARTCOMPOSITION:
                _isComposing = true;
                _lastCommittedImeString = string.Empty;
                Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                    NextHeader("text"),
                    string.Empty,
                    TextCompositionState.Started,
                    Source: InputEventSource.Synthetic)));
                if (DrawsCompositionInline)
                    return 0;
                break;

            case ImmNative.WM_IME_COMPOSITION:
                long compFlags = lParam.ToInt64();
                bool resultUnread = false;
                if ((compFlags & ImmNative.GCS_RESULTSTR) != 0)
                {
                    string resultText = CompositionStringProvider(hWnd, ImmNative.GCS_RESULTSTR);
                    resultUnread = string.IsNullOrEmpty(resultText);
                    if (!resultUnread)
                    {
                        _isComposing = false;
                        Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                            NextHeader("text"),
                            resultText,
                            TextCompositionState.Committed,
                            Source: InputEventSource.Synthetic)));
                        // Measured from the end of the dispatch: a slow first insertion (JIT, first
                        // layout) must not use up the window before the copies arrive.
                        _lastCommittedImeString = resultText;
                        _lastCommittedImeTimestamp = Clock.GetTimestamp();
                    }
                }
                if ((compFlags & ImmNative.GCS_COMPSTR) != 0)
                {
                    string compText = CompositionStringProvider(hWnd, ImmNative.GCS_COMPSTR);
                    _isComposing = true;
                    Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                        NextHeader("text"),
                        compText,
                        TextCompositionState.Updated,
                        Source: InputEventSource.Synthetic)));
                }
                // Passed on, the IME's window would draw the composition again, and DefWindowProc would turn
                // the result into WM_IME_CHAR and WM_CHAR. A result the bridge could not read goes on, so that
                // path still delivers it.
                if (DrawsCompositionInline && !resultUnread)
                    return 0;
                break;

            // Passed on in either mode: it only lets the IME's window close its composition.
            case ImmNative.WM_IME_ENDCOMPOSITION:
                if (_isComposing)
                {
                    _isComposing = false;
                    Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                        NextHeader("text"),
                        string.Empty,
                        TextCompositionState.Cancelled,
                        Source: InputEventSource.Synthetic)));
                }
                break;

            case WindowNative.WmDeadChar:
            case WindowNative.WmSysDeadChar:
                _deadKeyActive = true;
                break;

            // A key pressed with Alt (Alt+F, Alt+Space) is a menu key, not text: it goes on to DefWindowProc,
            // which opens the window menu for Alt+Space. AltGr (Ctrl+Alt) and Alt+numpad codes arrive as
            // WM_CHAR, so no text is lost.
            case WindowNative.WmSysChar:
                break;

            case WindowNative.WmChar:
            case WindowNative.WmUniChar:
                char character = (char)wParam;
                ProcessChar(character);
                return 0; // Handled to suppress downstream duplicate processing in RenderHostWindowProc

            case WindowNative.WmMouseHWheel:
                short hRawDelta = (short)(wParam.ToInt64() >> 16);
                double hNotches = hRawDelta / (double)WindowNative.WheelDelta;
                BPoint hPoint = GetClientPoint(hWnd, lParam);
                InputModifiers hModifiers = GetCurrentModifiers();
                ProcessMouseWheel(MouseWheelAxis.Horizontal, hNotches, hPoint, hModifiers);
                return 0;

            case WindowNative.WmMouseWheel:
                short vRawDelta = (short)(wParam.ToInt64() >> 16);
                double vNotches = vRawDelta / (double)WindowNative.WheelDelta;
                BPoint vPoint = GetClientPoint(hWnd, lParam);
                InputModifiers vModifiers = GetCurrentModifiers();
                bool shiftHeld = (vModifiers & InputModifiers.Shift) != 0;
                MouseWheelAxis axis = shiftHeld ? MouseWheelAxis.Horizontal : MouseWheelAxis.Vertical;
                ProcessMouseWheel(axis, vNotches, vPoint, vModifiers);
                return 0;
        }

        return fromSubclass ? WindowNative.DefSubclassProc(hWnd, uMsg, wParam, lParam) : 0;
    }

    public bool ProcessTextInput(char character) => ProcessChar(character);

    internal bool ProcessChar(char c)
    {
        // 1. Suppress the WM_CHAR copies Windows synthesizes after an IME commit
        if (!string.IsNullOrEmpty(_lastCommittedImeString))
        {
            if (_lastCommittedImeString[0] == c && Clock.GetElapsedTime(_lastCommittedImeTimestamp) < CommittedCopyWindow)
            {
                _lastCommittedImeString = _lastCommittedImeString[1..];
                return true;
            }
            // Anything else means the copies are not coming, or are over: it is typed normally.
            _lastCommittedImeString = string.Empty;
        }

        // 2. Control chord suppression: Ctrl+Key without Alt produces ASCII control codes
        bool ctrl = (KeyStateProvider(WindowNative.VkControl) & 0x8000) != 0;
        bool alt = (KeyStateProvider(WindowNative.VkMenu) & 0x8000) != 0;

        if (ctrl && !alt)
        {
            // ASCII control codes (0x00 to 0x1F) and DEL (0x7F) from Ctrl+C, Ctrl+A, Ctrl+Backspace, etc.
            if (c < 0x20 || c == 0x7F)
            {
                return true;
            }
        }

        // 3. UTF-16 surrogate pair assembly
        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return true; // Buffered; await low surrogate
        }

        if (char.IsLowSurrogate(c))
        {
            if (_pendingHighSurrogate != '\0')
            {
                string surrogatePair = new string(new[] { _pendingHighSurrogate, c });
                _pendingHighSurrogate = '\0';
                Dispatch(UiInputEvent.FromTextInput(new TextInputEvent(
                    NextHeader("text"),
                    surrogatePair,
                    InputEventSource.Synthetic)));
                return true;
            }

            // Stray low surrogate without a high surrogate is invalid UTF-16; discard
            _pendingHighSurrogate = '\0';
            return true;
        }

        if (_pendingHighSurrogate != '\0')
        {
            // Orphaned high surrogate discarded
            _pendingHighSurrogate = '\0';
        }

        // 4. Suppress unprintable control characters (< 0x20 except Enter/Newline/Tab)
        if (c < 0x20 && c != '\r' && c != '\n' && c != '\t')
        {
            return true;
        }

        // 5. Valid printable character (including AltGr graphemes like @, €, etc.)
        _deadKeyActive = false;
        Dispatch(UiInputEvent.FromTextInput(new TextInputEvent(
            NextHeader("text"),
            c.ToString(),
            InputEventSource.Synthetic)));
        return true;
    }

    public bool ProcessMouseWheel(MouseWheelAxis axis, double deltaNotches, BPoint position, InputModifiers modifiers)
    {
        var wheelEvent = new MouseWheelEvent(
            NextHeader("mouse"),
            InputPoint.ClientDeviceIndependentPixels(position.X, position.Y),
            GetCurrentMouseButtons(),
            axis,
            deltaNotches,
            InputEventSource.Synthetic,
            modifiers);

        UiInputEvent uiEvent = UiInputEvent.FromMouseWheel(wheelEvent);
        Dispatch(uiEvent);
        return true;
    }

    private void Dispatch(UiInputEvent input)
    {
        EventDispatched?.Invoke(input);
        if (_inputFilter?.Invoke(input) == true || _session.DispatchInput(input))
        {
            _invalidateAction?.Invoke();
        }
    }

    private InputEventHeader NextHeader(string channel) =>
        new(_deviceId, new InputTimestamp(++_sequence, TimeSpan.TicksPerSecond, channel), _sequence);

    private BPoint GetClientPoint(nint hWnd, nint lParam)
    {
        short screenX = (short)(lParam.ToInt64() & 0xFFFF);
        short screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var pt = new WindowNative.POINT { X = screenX, Y = screenY };
        pt = ScreenToClientAction(hWnd, pt);
        double scale = _scaleProvider();
        if (scale <= 0) scale = 1.0;
        return new BPoint(pt.X / scale, pt.Y / scale);
    }

    private InputModifiers GetCurrentModifiers()
    {
        InputModifiers mods = InputModifiers.None;
        if ((KeyStateProvider(WindowNative.VkControl) & 0x8000) != 0)
            mods |= InputModifiers.Control;
        if ((KeyStateProvider(WindowNative.VkShift) & 0x8000) != 0)
            mods |= InputModifiers.Shift;
        if ((KeyStateProvider(WindowNative.VkMenu) & 0x8000) != 0)
            mods |= InputModifiers.Alt;
        return mods;
    }

    private MouseButtons GetCurrentMouseButtons()
    {
        MouseButtons buttons = MouseButtons.None;
        if ((KeyStateProvider(WindowNative.VkLButton) & 0x8000) != 0)
            buttons |= MouseButtons.Left;
        if ((KeyStateProvider(WindowNative.VkRButton) & 0x8000) != 0)
            buttons |= MouseButtons.Right;
        if ((KeyStateProvider(WindowNative.VkMButton) & 0x8000) != 0)
            buttons |= MouseButtons.Middle;
        return buttons;
    }

    private static string ReadCompositionStringFromImm(nint hWnd, uint dwIndex)
    {
        if (hWnd == nint.Zero)
            return string.Empty;

        nint hImc = ImmNative.ImmGetContext(hWnd);
        if (hImc == nint.Zero)
            return string.Empty;

        try
        {
            int bytes = ImmNative.ImmGetCompositionString(hImc, dwIndex, nint.Zero, 0);
            if (bytes <= 0)
                return string.Empty;

            byte[] buffer = new byte[bytes];
            unsafe
            {
                fixed (byte* pBuf = buffer)
                {
                    ImmNative.ImmGetCompositionString(hImc, dwIndex, (nint)pBuf, (uint)bytes);
                }
            }
            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        finally
        {
            ImmNative.ImmReleaseContext(hWnd, hImc);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_isComposing)
        {
            _isComposing = false;
            try
            {
                Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                    NextHeader("text"),
                    string.Empty,
                    TextCompositionState.Cancelled,
                    Source: InputEventSource.Synthetic)));
            }
            catch
            {
                // Suppress exceptions during disposal cleanup
            }
        }

        if (_renderHwnd != nint.Zero && _subclassProc != null)
        {
            WindowNative.RemoveWindowSubclass(_renderHwnd, _subclassProc, _subclassId);
        }
    }
}
