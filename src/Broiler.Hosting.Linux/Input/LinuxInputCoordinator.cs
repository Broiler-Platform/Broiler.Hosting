using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Graphics.Geometry;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.Input.Keyboard.Linux;
using Broiler.Input.Linux;
using Broiler.Input.Mouse;
using Broiler.Input.Mouse.Linux;
using Broiler.UI;

namespace Broiler.Hosting.Linux;

/// <summary>
/// Coordinates evdev keyboard and mouse input streams into <see cref="UiInputEvent"/> instances for Linux desktop heads.
/// Merges modifier key state across independent evdev device streams, normalizes key events, and exposes non-blocking queues.
/// </summary>
public sealed class LinuxInputCoordinator : IAsyncDisposable
{
    private readonly bool _enabled;
    private readonly bool _externalPointer;
    private readonly string _applicationName;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<UiInputEvent> _pending = new();
    private readonly object _gate = new();
    private readonly InputDeviceId _externalPointerId = InputDeviceId.FromOpaqueValue("linux:hosting:x11-pointer");
    private MouseButtons _buttons;

    /// <summary>
    /// Modifier keys last reported by the keyboard, stamped onto pointer events.
    /// </summary>
    private InputModifiers _modifiers;
    private long _externalSequence;
    private LinuxKeyboardProvider? _keyboardProvider;
    private LinuxMouseProvider? _mouseProvider;
    private KeyboardInputDevice? _keyboard;
    private MouseInputDevice? _mouse;
    private bool _active;
    private bool _initialized;
    private bool _disposed;
    private bool _pointerInitialized;
    private BSize _viewport = new(1120, 780);
    private double _pointerX = 560;
    private double _pointerY = 390;
    private int _keyEvents;
    private int _textEvents;
    private int _mouseMoveEvents;
    private int _mouseButtonEvents;
    private int _mouseWheelEvents;
    private string? _keyboardSummary;
    private string? _mouseSummary;

    public LinuxInputCoordinator(bool enabled, Action<string> log, bool externalPointer = false, string applicationName = "Broiler Linux")
    {
        _enabled = enabled;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _externalPointer = externalPointer;
        _applicationName = string.IsNullOrWhiteSpace(applicationName) ? "Broiler Linux" : applicationName;
    }

    public LinuxInputSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new LinuxInputSnapshot(
                    _enabled,
                    _initialized,
                    _active,
                    _pointerX,
                    _pointerY,
                    _keyEvents,
                    _textEvents,
                    _mouseMoveEvents,
                    _mouseButtonEvents,
                    _mouseWheelEvents,
                    _keyboardSummary,
                    _mouseSummary);
            }
        }
    }

    public void SetExternalPointer(double x, double y)
    {
        UiInputEvent? uiEvent = null;
        lock (_gate)
        {
            double newX = Clamp(x, 0, Math.Max(0, _viewport.Width - 1));
            double newY = Clamp(y, 0, Math.Max(0, _viewport.Height - 1));
            bool moved = !_pointerInitialized || Math.Abs(newX - _pointerX) >= 0.5 || Math.Abs(newY - _pointerY) >= 0.5;
            _pointerX = newX;
            _pointerY = newY;
            _pointerInitialized = true;

            if (moved)
            {
                InputEventHeader header = new(_externalPointerId, StopwatchInputClock.Shared.GetTimestamp(), ++_externalSequence);
                uiEvent = UiInputEvent.FromMouseMove(new MouseMoveEvent(
                    header,
                    InputPoint.ClientDeviceIndependentPixels(_pointerX, _pointerY),
                    _buttons,
                    InputEventSource.Semantic,
                    _modifiers));
            }
        }

        if (uiEvent is not null)
            _pending.Enqueue(uiEvent);
    }

    public void SetViewport(BSize viewport)
    {
        lock (_gate)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0)
                return;

            _viewport = viewport;
            if (!_pointerInitialized)
            {
                _pointerX = viewport.Width / 2;
                _pointerY = viewport.Height / 2;
                _pointerInitialized = true;
            }
            else
            {
                _pointerX = Clamp(_pointerX, 0, Math.Max(0, viewport.Width - 1));
                _pointerY = Clamp(_pointerY, 0, Math.Max(0, viewport.Height - 1));
            }
        }
    }

    public int Drain(Action<UiInputEvent> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        int count = 0;
        while (_pending.TryDequeue(out UiInputEvent? input))
        {
            dispatch(input);
            count++;
        }

        return count;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled || _initialized)
            return;

        if (!OperatingSystem.IsLinux())
        {
            _log("evdev input requested, but Linux input devices are only opened on Linux.");
            return;
        }

        LinuxEventDeviceAccessStatus access = LinuxEventDeviceAccessProbe.CheckEventDeviceAccess();
        _log("evdev device access: " + access.Diagnostic);

        LinuxEvdevProviderOptions options = new(AcknowledgeRawBackgroundInput: true);
        _keyboardProvider = new LinuxKeyboardProvider(options);
        _mouseProvider = new LinuxMouseProvider(options);

        IReadOnlyList<InputDeviceDescriptor> keyboards = await _keyboardProvider.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InputDeviceDescriptor> mice = await _mouseProvider.GetDevicesAsync(cancellationToken).ConfigureAwait(false);

        _keyboard = await TryOpenKeyboardAsync(keyboards, cancellationToken).ConfigureAwait(false);
        _mouse = await TryOpenMouseAsync(mice, cancellationToken).ConfigureAwait(false);

        if (_keyboard is null && _mouse is null)
        {
            _log("evdev input enabled, but no readable keyboard or mouse event devices were opened.");
            LogInputAccessRemediation(access);
            return;
        }

        if (_keyboard is not null)
            _keyboard.KeyChanged += OnKeyChanged;
        if (_mouse is not null)
        {
            _mouse.Moved += OnMouseMoved;
            _mouse.ButtonChanged += OnMouseButtonChanged;
            _mouse.WheelChanged += OnMouseWheelChanged;
        }

        lock (_gate)
            _initialized = true;

        _log("evdev input opened. Events run only while the X11 window is focused; use File > Exit to close " + _applicationName + ".");
    }

    public async ValueTask SetActiveAsync(bool active, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized || _active == active)
            return;

        if (active)
        {
            if (_keyboard is not null)
                await _keyboard.StartAsync(cancellationToken).ConfigureAwait(false);
            if (_mouse is not null)
                await _mouse.StartAsync(cancellationToken).ConfigureAwait(false);
            _log("evdev input resumed for focused X11 window.");
        }
        else
        {
            if (_keyboard is not null)
                await _keyboard.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_mouse is not null)
                await _mouse.StopAsync(cancellationToken).ConfigureAwait(false);
            _log("evdev input paused because the X11 window is not focused.");
        }

        lock (_gate)
            _active = active;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_active)
            await SetActiveAsync(false).ConfigureAwait(false);

        if (_keyboard is not null)
        {
            _keyboard.KeyChanged -= OnKeyChanged;
            await _keyboard.DisposeAsync().ConfigureAwait(false);
        }

        if (_mouse is not null)
        {
            _mouse.Moved -= OnMouseMoved;
            _mouse.ButtonChanged -= OnMouseButtonChanged;
            _mouse.WheelChanged -= OnMouseWheelChanged;
            await _mouse.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void LogInputAccessRemediation(LinuxEventDeviceAccessStatus access)
    {
        if (!access.DirectoryExists || access.EventDeviceCount == 0)
        {
            _log("  reason: no /dev/input/event* devices are visible (headless session, or /dev/input not passed into the container).");
            return;
        }

        if (access.ReadableEventDeviceCount == 0)
        {
            _log("  reason: " + access.EventDeviceCount.ToString(CultureInfo.InvariantCulture) +
                " /dev/input/event* device(s) exist but are not readable by this user (permission denied).");
            _log("  fix: add your user to the input group, then log out and back in:");
            _log("       sudo usermod -aG input \"$USER\"");
            _log("  verify: 'groups' should list 'input', and 'ls -l /dev/input/event*' should show a readable 'input' group.");
            _log("  alternatives: a scoped udev rule on a trusted test machine, or pass /dev/input into the container.");
        }
    }

    private async ValueTask<KeyboardInputDevice?> TryOpenKeyboardAsync(
        IReadOnlyList<InputDeviceDescriptor> keyboards,
        CancellationToken cancellationToken)
    {
        if (_keyboardProvider is null)
            return null;

        foreach (InputDeviceDescriptor descriptor in keyboards.Where(static descriptor => descriptor.Availability == InputDeviceAvailability.Available))
        {
            try
            {
                KeyboardInputDevice device = await _keyboardProvider.OpenAsync(descriptor, new KeyboardOpenOptions(ReceiveText: false), cancellationToken).ConfigureAwait(false);
                _keyboardSummary = DescribeDescriptor(descriptor);
                _log("keyboard: " + _keyboardSummary);
                return device;
            }
            catch (Exception exception)
            {
                _log("keyboard open failed for " + descriptor.DisplayName + ": " + exception.Message);
            }
        }

        if (keyboards.Any(static descriptor => descriptor.Availability == InputDeviceAvailability.PermissionDenied))
            _log("keyboard: event devices were found but permission was denied.");

        return null;
    }

    private async ValueTask<MouseInputDevice?> TryOpenMouseAsync(
        IReadOnlyList<InputDeviceDescriptor> mice,
        CancellationToken cancellationToken)
    {
        if (_mouseProvider is null)
            return null;

        foreach (InputDeviceDescriptor descriptor in mice.Where(static descriptor => descriptor.Availability == InputDeviceAvailability.Available))
        {
            try
            {
                MouseInputDevice device = await _mouseProvider.OpenAsync(descriptor, new MouseOpenOptions(), cancellationToken).ConfigureAwait(false);
                _mouseSummary = DescribeDescriptor(descriptor);
                string pointerKind = descriptor.Capabilities
                    .Any(static capability => capability.Name == "pointer" && capability.Value == "touchpad")
                    ? " [touchpad: absolute motion + tap-to-click]"
                    : " [mouse: relative motion]";
                _log("mouse: " + _mouseSummary + pointerKind);
                return device;
            }
            catch (Exception exception)
            {
                _log("mouse open failed for " + descriptor.DisplayName + ": " + exception.Message);
            }
        }

        if (mice.Any(static descriptor => descriptor.Availability == InputDeviceAvailability.PermissionDenied))
            _log("mouse: event devices were found but permission was denied.");

        return null;
    }

    private void OnKeyChanged(KeyboardKeyEvent inputEvent)
    {
        KeyboardKeyEvent normalized = NormalizeKeyboardEvent(inputEvent);
        lock (_gate)
        {
            _modifiers = (InputModifiers)normalized.Modifiers;
        }

        _pending.Enqueue(UiInputEvent.FromKeyboardKey(normalized));

        if (TryCreateTextInput(normalized, out string text))
            _pending.Enqueue(UiInputEvent.FromKeyboardText(new KeyboardTextEvent(normalized.Header, text, Source: InputEventSource.Semantic)));

        lock (_gate)
        {
            _keyEvents++;
            if (text.Length > 0)
                _textEvents++;
        }
    }

    private void OnMouseMoved(MouseMoveEvent inputEvent)
    {
        UiInputEvent? uiEvent = null;
        lock (_gate)
        {
            _mouseMoveEvents++;

            if (_externalPointer)
                return;

            EnsurePointerInitialized();
            _pointerX = Clamp(_pointerX + inputEvent.Position.X, 0, Math.Max(0, _viewport.Width - 1));
            _pointerY = Clamp(_pointerY + inputEvent.Position.Y, 0, Math.Max(0, _viewport.Height - 1));
            uiEvent = UiInputEvent.FromMouseMove(new MouseMoveEvent(
                inputEvent.Header,
                InputPoint.ClientDeviceIndependentPixels(_pointerX, _pointerY),
                inputEvent.Buttons,
                InputEventSource.Semantic,
                _modifiers));
        }

        if (uiEvent is not null)
            _pending.Enqueue(uiEvent);
    }

    private void OnMouseButtonChanged(MouseButtonEvent inputEvent)
    {
        UiInputEvent uiEvent;
        lock (_gate)
        {
            EnsurePointerInitialized();
            _mouseButtonEvents++;
            _buttons = inputEvent.Buttons;
            uiEvent = UiInputEvent.FromMouseButton(new MouseButtonEvent(
                inputEvent.Header,
                InputPoint.ClientDeviceIndependentPixels(_pointerX, _pointerY),
                inputEvent.Buttons,
                inputEvent.Button,
                inputEvent.Transition,
                InputEventSource.Semantic,
                _modifiers));
        }

        _pending.Enqueue(uiEvent);
    }

    private void OnMouseWheelChanged(MouseWheelEvent inputEvent)
    {
        UiInputEvent uiEvent;
        lock (_gate)
        {
            EnsurePointerInitialized();
            _mouseWheelEvents++;
            uiEvent = UiInputEvent.FromMouseWheel(new MouseWheelEvent(
                inputEvent.Header,
                InputPoint.ClientDeviceIndependentPixels(_pointerX, _pointerY),
                inputEvent.Buttons,
                inputEvent.Axis,
                inputEvent.DeltaNotches,
                InputEventSource.Semantic,
                _modifiers));
        }

        _pending.Enqueue(uiEvent);
    }

    private void EnsurePointerInitialized()
    {
        if (_pointerInitialized)
            return;

        _pointerX = _viewport.Width / 2;
        _pointerY = _viewport.Height / 2;
        _pointerInitialized = true;
    }

    private static KeyboardKeyEvent NormalizeKeyboardEvent(KeyboardKeyEvent inputEvent)
    {
        string name = NormalizeKeyName(inputEvent.Key.Name);
        int nativeKeyCode = VirtualKeyFromName(name, inputEvent.NativeKeyCode);
        return inputEvent with
        {
            Key = KeyboardKey.FromName(name),
            NativeKeyCode = nativeKeyCode,
            Source = InputEventSource.Semantic,
        };
    }

    private static string NormalizeKeyName(string name)
    {
        if (name.StartsWith("Key", StringComparison.Ordinal) && name.Length == 4)
            return name[3].ToString().ToUpperInvariant();

        if (name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6)
            return name[5].ToString();

        return name switch
        {
            "ArrowLeft" => "Left",
            "ArrowRight" => "Right",
            "ArrowUp" => "Up",
            "ArrowDown" => "Down",
            _ => name,
        };
    }

    private static class BVirtualKey
    {
        public const int Back = 0x08;
        public const int Tab = 0x09;
        public const int Enter = 0x0D;
        public const int Escape = 0x1B;
        public const int Space = 0x20;
        public const int PageUp = 0x21;
        public const int PageDown = 0x22;
        public const int End = 0x23;
        public const int Home = 0x24;
        public const int Left = 0x25;
        public const int Up = 0x26;
        public const int Right = 0x27;
        public const int Down = 0x28;
    }

    private static int VirtualKeyFromName(string name, int fallback)
    {
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z')
            return name[0];
        if (name.Length == 1 && name[0] is >= '0' and <= '9')
            return name[0];

        return name switch
        {
            "Backspace" => BVirtualKey.Back,
            "Tab" => BVirtualKey.Tab,
            "Enter" => BVirtualKey.Enter,
            "Escape" => BVirtualKey.Escape,
            "Space" => BVirtualKey.Space,
            "PageUp" => BVirtualKey.PageUp,
            "PageDown" => BVirtualKey.PageDown,
            "End" => BVirtualKey.End,
            "Home" => BVirtualKey.Home,
            "Left" => BVirtualKey.Left,
            "Up" => BVirtualKey.Up,
            "Right" => BVirtualKey.Right,
            "Down" => BVirtualKey.Down,
            "Delete" => 0x2E,
            _ => fallback,
        };
    }

    private static bool TryCreateTextInput(KeyboardKeyEvent inputEvent, out string text)
    {
        text = string.Empty;
        if (inputEvent.Transition != KeyboardKeyTransition.Down)
            return false;

        KeyboardModifierState blockedModifiers =
            KeyboardModifierState.Control |
            KeyboardModifierState.Alt |
            KeyboardModifierState.LeftWindows |
            KeyboardModifierState.RightWindows;
        if ((inputEvent.Modifiers & blockedModifiers) != 0)
            return false;

        bool shift = inputEvent.Modifiers.HasFlag(KeyboardModifierState.Shift);
        string name = inputEvent.Key.Name;
        if (name.Length == 1)
        {
            char character = name[0];
            if (character is >= 'A' and <= 'Z')
            {
                text = shift ? character.ToString() : char.ToLowerInvariant(character).ToString();
                return true;
            }

            if (character is >= '0' and <= '9')
            {
                text = shift ? ShiftedDigit(character).ToString() : character.ToString();
                return true;
            }
        }

        text = name switch
        {
            "Space" => " ",
            "Minus" => shift ? "_" : "-",
            "Equal" => shift ? "+" : "=",
            "BracketLeft" => shift ? "{" : "[",
            "BracketRight" => shift ? "}" : "]",
            "Semicolon" => shift ? ":" : ";",
            "Quote" => shift ? "\"" : "'",
            "Backquote" => shift ? "~" : "`",
            "Backslash" => shift ? "|" : "\\",
            "Comma" => shift ? "<" : ",",
            "Period" => shift ? ">" : ".",
            "Slash" => shift ? "?" : "/",
            _ => string.Empty,
        };

        return text.Length > 0;
    }

    private static char ShiftedDigit(char digit) =>
        digit switch
        {
            '1' => '!',
            '2' => '@',
            '3' => '#',
            '4' => '$',
            '5' => '%',
            '6' => '^',
            '7' => '&',
            '8' => '*',
            '9' => '(',
            '0' => ')',
            _ => digit,
        };

    private static string DescribeDescriptor(InputDeviceDescriptor descriptor)
    {
        string? eventName = descriptor.Capabilities
            .Where(static capability => capability.Name == "event-device")
            .Select(static capability => capability.Value)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(eventName)
            ? descriptor.DisplayName
            : descriptor.DisplayName + " (" + eventName + ")";
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
