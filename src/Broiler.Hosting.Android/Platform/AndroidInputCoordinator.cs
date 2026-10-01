using System;
using System.Collections.Generic;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Broiler.Input;
using Broiler.Input.Android;
using Broiler.Input.Keyboard;
using Broiler.Input.Keyboard.Android;
using Broiler.Input.Pen;
using Broiler.Input.Pen.Android;
using Broiler.Input.Text;
using Broiler.Input.Text.Android;
using Broiler.Input.Touch;
using Broiler.Input.Touch.Android;
using Broiler.UI;

namespace Broiler.Hosting.Android;

/// <summary>Owns the real Android event objects and forwards primitive snapshots to Input.</summary>
public sealed class AndroidInputCoordinator : IDisposable, IAndroidEditorTextSource
{
    private readonly Action<UiInputEvent> _dispatch;
    private readonly Func<IUiTextEditor?> _getEditor;
    private readonly AndroidCoordinateSpace _coordinateSpace;
    private readonly AndroidTouchProvider _touchProvider;
    private readonly AndroidPenProvider _penProvider;
    private readonly AndroidKeyboardProvider _keyboardProvider;
    private readonly AndroidTextInputProvider _textProvider;
    private readonly AndroidTouchInputDevice _touch;
    private readonly AndroidPenInputDevice _pen;
    private readonly AndroidKeyboardInputDevice _keyboard;
    private readonly AndroidTextInputDevice _text;
    private bool _disposed;

    public AndroidInputCoordinator(
        double density,
        Action<UiInputEvent> dispatch,
        Func<IUiTextEditor?> getEditor)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _getEditor = getEditor ?? throw new ArgumentNullException(nameof(getEditor));
        _coordinateSpace = new AndroidCoordinateSpace(density);
        var clock = new AndroidUptimeInputClock();

        _touchProvider = new AndroidTouchProvider(_coordinateSpace, clock);
        _penProvider = new AndroidPenProvider(_coordinateSpace, clock);
        _keyboardProvider = new AndroidKeyboardProvider(clock);
        _textProvider = new AndroidTextInputProvider(clock) { EditorTextSource = this };

        _touch = (AndroidTouchInputDevice)_touchProvider.OpenAsync(
            _touchProvider.RegisterDefaultTouchScreen(), new TouchOpenOptions()).GetAwaiter().GetResult();
        _pen = (AndroidPenInputDevice)_penProvider.OpenAsync(
            _penProvider.RegisterDefaultStylus(supportsTilt: true, supportsEraser: true), new PenOpenOptions()).GetAwaiter().GetResult();
        _keyboard = (AndroidKeyboardInputDevice)_keyboardProvider.OpenAsync(
            _keyboardProvider.RegisterDefaultKeyboard(), new KeyboardOpenOptions()).GetAwaiter().GetResult();
        _text = (AndroidTextInputDevice)_textProvider.OpenAsync(
            _textProvider.RegisterInputMethod(), new TextInputOpenOptions()).GetAwaiter().GetResult();

        OpenAndStart(_touch);
        OpenAndStart(_pen);
        OpenAndStart(_keyboard);
        OpenAndStart(_text);

        _touch.ContactChanged += OnTouch;
        _pen.ContactChanged += OnPen;
        _keyboard.KeyChanged += OnKey;
        _keyboard.TextInput += OnKeyboardText;
        _text.TextInput += OnText;
        _text.CompositionChanged += OnComposition;
        _text.EditRequested += OnEditRequested;
    }

    public AndroidTextInputDevice TextDevice => _text;

    public double Density
    {
        get => _coordinateSpace.Density;
        set => _coordinateSpace.Density = value;
    }

    public bool ProcessMotionEvent(MotionEvent motionEvent)
    {
        ArgumentNullException.ThrowIfNull(motionEvent);
        AndroidMotionSample sample = CreateMotionSample(motionEvent);
        bool handled = _touch.ProcessMotionEvent(sample);
        handled = _pen.ProcessMotionEvent(sample) || handled;
        return handled;
    }

    public bool ProcessKeyEvent(KeyEvent keyEvent)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        if (keyEvent.IsSystem)
            return false;

        int unicode = keyEvent.Action == KeyEventActions.Down
            ? keyEvent.GetUnicodeChar(keyEvent.MetaState)
            : 0;
        var sample = new AndroidKeyEventSample(
            (int)keyEvent.Action,
            (int)keyEvent.KeyCode,
            keyEvent.ScanCode,
            (int)keyEvent.MetaState,
            keyEvent.RepeatCount,
            unicode,
            keyEvent.EventTime,
            (int)keyEvent.Source,
            keyEvent.IsSystem);
        return _keyboard.ProcessKeyEvent(sample);
    }

    public void NotifyFocusLost()
    {
        _touch.CancelActiveContacts("activity paused");
        _pen.CancelActiveContact("activity paused");
        _keyboard.ReleaseHeldKeys("activity paused");
        _text.NotifyFocusLost();
    }

    public string GetTextBeforeCursor(int maxLength)
    {
        IUiTextEditor? editor = _getEditor();
        if (editor is null)
            return string.Empty;

        UiTextEditorMetrics metrics = editor.GetTextEditorMetrics();
        int cursor = Math.Clamp(metrics.SelectionEnd, 0, metrics.TextLength);
        int start = Math.Max(0, cursor - Math.Max(0, maxLength));
        return editor.GetTextEditorRange(start, cursor - start);
    }

    public string GetTextAfterCursor(int maxLength)
    {
        IUiTextEditor? editor = _getEditor();
        if (editor is null)
            return string.Empty;

        UiTextEditorMetrics metrics = editor.GetTextEditorMetrics();
        int cursor = Math.Clamp(metrics.SelectionEnd, 0, metrics.TextLength);
        return editor.GetTextEditorRange(cursor, Math.Max(0, maxLength));
    }

    public string GetSelectedText()
    {
        IUiTextEditor? editor = _getEditor();
        if (editor is null)
            return string.Empty;

        UiTextEditorMetrics metrics = editor.GetTextEditorMetrics();
        int start = Math.Clamp(Math.Min(metrics.SelectionStart, metrics.SelectionEnd), 0, metrics.TextLength);
        int end = Math.Clamp(Math.Max(metrics.SelectionStart, metrics.SelectionEnd), start, metrics.TextLength);
        return editor.GetTextEditorRange(start, end - start);
    }

    public AndroidEditorSelection GetSelection()
    {
        UiTextEditorMetrics metrics = EditorMetrics();
        return new AndroidEditorSelection(
            metrics.SelectionStart,
            metrics.SelectionEnd,
            metrics.ComposingStart,
            metrics.ComposingEnd);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _touch.ContactChanged -= OnTouch;
        _pen.ContactChanged -= OnPen;
        _keyboard.KeyChanged -= OnKey;
        _keyboard.TextInput -= OnKeyboardText;
        _text.TextInput -= OnText;
        _text.CompositionChanged -= OnComposition;
        _text.EditRequested -= OnEditRequested;

        Stop(_touch);
        Stop(_pen);
        Stop(_keyboard);
        Stop(_text);
        _touch.Dispose();
        _pen.Dispose();
        _keyboard.Dispose();
        _text.Dispose();
    }

    private static void OpenAndStart(Broiler.Input.InputDevice device)
    {
        device.OpenAsync().GetAwaiter().GetResult();
        device.StartAsync().GetAwaiter().GetResult();
    }

    private static void Stop(Broiler.Input.InputDevice device)
    {
        try
        {
            device.StopAsync().GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private AndroidMotionSample CreateMotionSample(MotionEvent input)
    {
        var pointers = new List<AndroidPointerSample>(input.PointerCount);
        for (int index = 0; index < input.PointerCount; index++)
        {
            pointers.Add(new AndroidPointerSample(
                input.GetPointerId(index),
                input.GetX(index),
                input.GetY(index),
                input.GetPressure(index),
                (int)input.GetToolType(index),
                input.GetOrientation(index),
                input.GetAxisValue(Axis.Tilt, index)));
        }

        return new AndroidMotionSample(
            (int)input.Action,
            pointers,
            input.EventTime,
            input.DownTime,
            (int)input.MetaState,
            (int)input.ButtonState,
            (int)input.Source);
    }

    private UiTextEditorMetrics EditorMetrics() =>
        _getEditor()?.GetTextEditorMetrics() ?? new UiTextEditorMetrics(0, 0, 0);

    private void OnEditRequested(AndroidTextEditRequest request)
    {
        IUiTextEditor? editor = _getEditor();
        if (editor is null)
            return;

        _ = request.Kind switch
        {
            AndroidTextEditRequestKind.DeleteSurroundingText => editor.DeleteSurroundingText(request.Start, request.End),
            AndroidTextEditRequestKind.SetSelection => editor.SetEditorSelection(request.Start, request.End),
            AndroidTextEditRequestKind.SetComposingRegion => editor.SetComposingRegion(request.Start, request.End),
            AndroidTextEditRequestKind.EditorAction => editor.PerformEditorAction(MapEditorAction(request.Start)),
            _ => false,
        };
    }

    private static UiTextEditorAction MapEditorAction(int action) => (ImeAction)action switch
    {
        ImeAction.Done => UiTextEditorAction.Done,
        ImeAction.Go => UiTextEditorAction.Go,
        ImeAction.Next => UiTextEditorAction.Next,
        ImeAction.Previous => UiTextEditorAction.Previous,
        ImeAction.Search => UiTextEditorAction.Search,
        ImeAction.Send => UiTextEditorAction.Send,
        _ => UiTextEditorAction.None,
    };

    private void OnTouch(TouchContactEvent input) => _dispatch(UiInputEvent.FromTouchContact(input));
    private void OnPen(PenContactEvent input) => _dispatch(UiInputEvent.FromPenContact(input));
    private void OnKey(KeyboardKeyEvent input) => _dispatch(UiInputEvent.FromKeyboardKey(input));
    private void OnKeyboardText(KeyboardTextEvent input) => _dispatch(UiInputEvent.FromKeyboardText(input));
    private void OnText(TextInputEvent input) => _dispatch(UiInputEvent.FromTextInput(input));
    private void OnComposition(TextCompositionEvent input) => _dispatch(UiInputEvent.FromTextComposition(input));
}
