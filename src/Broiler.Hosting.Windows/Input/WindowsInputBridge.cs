using System;
using System.Runtime.InteropServices;
using System.Text;
using Broiler.Graphics.Geometry;
using Broiler.Input;
using Broiler.Input.Mouse;
using Broiler.Input.Text;
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
    private readonly InputNative.SubclassProc? _subclassProc;

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
    private string _lastCommittedImeString = string.Empty;
    private long _lastCommittedImeTimestamp;
    private static readonly TimeSpan CommittedCopyWindow = TimeSpan.FromMilliseconds(500);

    // Testability hooks
    public Func<int, short> KeyStateProvider { get; set; } = InputNative.GetKeyState;
    public Func<nint, uint, string> CompositionStringProvider { get; set; } = ReadCompositionStringFromImm;
    /// <summary>The clock for the backstop that ends suppression of an IME commit's WM_CHAR copies.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public Action<nint>? SetFocusAction { get; set; } = hwnd => { if (hwnd != nint.Zero) InputNative.SetFocus(hwnd); };
    public Func<nint, InputNative.POINT, InputNative.POINT> ScreenToClientAction { get; set; } = (hwnd, pt) =>
    {
        if (hwnd != nint.Zero)
            InputNative.ScreenToClient(hwnd, ref pt);
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
            InputNative.SetWindowSubclass(_renderHwnd, _subclassProc, _subclassId, 0);
        }
    }

    public void OnTopLevelMessage(uint message, nint wParam, nint lParam)
    {
        if (_isDisposed || _renderHwnd == nint.Zero)
            return;

        if (message == InputNative.WM_SETFOCUS)
        {
            SetFocusAction?.Invoke(_renderHwnd);
        }
        else if (message == InputNative.WM_ACTIVATE)
        {
            int activation = (int)(wParam.ToInt64() & 0xFFFF);
            if (activation != InputNative.WA_INACTIVE)
            {
                SetFocusAction?.Invoke(_renderHwnd);
            }
        }
    }

    private nint SubclassWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (_isDisposed)
            return InputNative.DefSubclassProc(hWnd, uMsg, wParam, lParam);

        return ProcessNativeMessage(hWnd, uMsg, wParam, lParam, fromSubclass: true);
    }

    public nint ProcessNativeMessage(nint hWnd, uint uMsg, nint wParam, nint lParam, bool fromSubclass = false)
    {
        switch (uMsg)
        {
            case InputNative.WM_KILLFOCUS:
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

            case InputNative.WM_IME_STARTCOMPOSITION:
                _isComposing = true;
                _lastCommittedImeString = string.Empty;
                Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                    NextHeader("text"),
                    string.Empty,
                    TextCompositionState.Started,
                    Source: InputEventSource.Synthetic)));
                break;

            case InputNative.WM_IME_COMPOSITION:
                long compFlags = lParam.ToInt64();
                if ((compFlags & InputNative.GCS_RESULTSTR) != 0)
                {
                    string resultText = CompositionStringProvider(hWnd, InputNative.GCS_RESULTSTR);
                    if (!string.IsNullOrEmpty(resultText))
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
                if ((compFlags & InputNative.GCS_COMPSTR) != 0)
                {
                    string compText = CompositionStringProvider(hWnd, InputNative.GCS_COMPSTR);
                    _isComposing = true;
                    Dispatch(UiInputEvent.FromTextComposition(new TextCompositionEvent(
                        NextHeader("text"),
                        compText,
                        TextCompositionState.Updated,
                        Source: InputEventSource.Synthetic)));
                }
                break;

            case InputNative.WM_IME_ENDCOMPOSITION:
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

            case InputNative.WM_DEADCHAR:
            case InputNative.WM_SYSDEADCHAR:
                _deadKeyActive = true;
                break;

            case InputNative.WM_CHAR:
            case InputNative.WM_SYSCHAR:
            case InputNative.WM_UNICHAR:
                char character = (char)wParam;
                ProcessChar(character);
                return 0; // Handled to suppress downstream duplicate processing in RenderHostWindowProc

            case InputNative.WM_MOUSEHWHEEL:
                short hRawDelta = (short)(wParam.ToInt64() >> 16);
                double hNotches = hRawDelta / InputNative.WheelDelta;
                BPoint hPoint = GetClientPoint(hWnd, lParam);
                InputModifiers hModifiers = GetCurrentModifiers();
                ProcessMouseWheel(MouseWheelAxis.Horizontal, hNotches, hPoint, hModifiers);
                return 0;

            case InputNative.WM_MOUSEWHEEL:
                short vRawDelta = (short)(wParam.ToInt64() >> 16);
                double vNotches = vRawDelta / InputNative.WheelDelta;
                BPoint vPoint = GetClientPoint(hWnd, lParam);
                InputModifiers vModifiers = GetCurrentModifiers();
                bool shiftHeld = (vModifiers & InputModifiers.Shift) != 0;
                MouseWheelAxis axis = shiftHeld ? MouseWheelAxis.Horizontal : MouseWheelAxis.Vertical;
                ProcessMouseWheel(axis, vNotches, vPoint, vModifiers);
                return 0;
        }

        return fromSubclass ? InputNative.DefSubclassProc(hWnd, uMsg, wParam, lParam) : 0;
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
        bool ctrl = (KeyStateProvider(InputNative.VK_CONTROL) & 0x8000) != 0;
        bool alt = (KeyStateProvider(InputNative.VK_MENU) & 0x8000) != 0;

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
        var pt = new InputNative.POINT { X = screenX, Y = screenY };
        pt = ScreenToClientAction(hWnd, pt);
        double scale = _scaleProvider();
        if (scale <= 0) scale = 1.0;
        return new BPoint(pt.X / scale, pt.Y / scale);
    }

    private InputModifiers GetCurrentModifiers()
    {
        InputModifiers mods = InputModifiers.None;
        if ((KeyStateProvider(InputNative.VK_CONTROL) & 0x8000) != 0)
            mods |= InputModifiers.Control;
        if ((KeyStateProvider(InputNative.VK_SHIFT) & 0x8000) != 0)
            mods |= InputModifiers.Shift;
        if ((KeyStateProvider(InputNative.VK_MENU) & 0x8000) != 0)
            mods |= InputModifiers.Alt;
        return mods;
    }

    private MouseButtons GetCurrentMouseButtons()
    {
        MouseButtons buttons = MouseButtons.None;
        if ((KeyStateProvider(InputNative.VK_LBUTTON) & 0x8000) != 0)
            buttons |= MouseButtons.Left;
        if ((KeyStateProvider(InputNative.VK_RBUTTON) & 0x8000) != 0)
            buttons |= MouseButtons.Right;
        if ((KeyStateProvider(InputNative.VK_MBUTTON) & 0x8000) != 0)
            buttons |= MouseButtons.Middle;
        return buttons;
    }

    private static string ReadCompositionStringFromImm(nint hWnd, uint dwIndex)
    {
        if (hWnd == nint.Zero)
            return string.Empty;

        nint hImc = InputNative.ImmGetContext(hWnd);
        if (hImc == nint.Zero)
            return string.Empty;

        try
        {
            int bytes = InputNative.ImmGetCompositionString(hImc, dwIndex, nint.Zero, 0);
            if (bytes <= 0)
                return string.Empty;

            byte[] buffer = new byte[bytes];
            unsafe
            {
                fixed (byte* pBuf = buffer)
                {
                    InputNative.ImmGetCompositionString(hImc, dwIndex, (nint)pBuf, (uint)bytes);
                }
            }
            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        finally
        {
            InputNative.ImmReleaseContext(hWnd, hImc);
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
            InputNative.RemoveWindowSubclass(_renderHwnd, _subclassProc, _subclassId);
        }
    }
}
