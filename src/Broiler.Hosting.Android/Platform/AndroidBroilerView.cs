using System;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Broiler.Graphics;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Graphics.Rendering;
using Broiler.Input.Text.Android;
using Broiler.UI;

namespace Broiler.Hosting.Android;

/// <summary>One Broiler-owned SurfaceView with vsync, lifecycle, input, IME, and clipboard glue.</summary>
public sealed class AndroidBroilerView : SurfaceView, ISurfaceHolderCallback, Choreographer.IFrameCallback
{
    private readonly AndroidCanvasRenderer _renderer = new();
    private readonly AndroidInputCoordinator _input;
    private readonly BColor _clearColor;
    private BSize _viewportSize = new(1, 1);
    private bool _surfaceReady;
    private bool _resumed;
    private bool _frameScheduled;
    private bool _invalidated = true;
    private bool _disposed;
    private double _density;
    private UiElement? _textOwner;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;

    public AndroidBroilerView(Context context, BColor clearColor)
        : base(context)
    {
        _clearColor = clearColor;
        _density = ResolveDensity(context);
        _input = new AndroidInputCoordinator(_density, DispatchInputCore, ResolveTextEditor);

        Holder?.AddCallback(this);
        Focusable = true;
        FocusableInTouchMode = true;
        KeepScreenOn = false;
    }

    public Func<BRenderList?>? RenderFrame { get; set; }

    public Action<UiInputEvent>? DispatchInput { get; set; }

    public Func<UiSession?>? GetSession { get; set; }

    public Action? StepAnimation { get; set; }

    public Action? ReleaseGraphicsResources { get; set; }

    public bool AnimationActive { get; set; }

    public BSize ViewportSize => _viewportSize;

    public double Scale => _density;

    public AndroidCanvasRenderer Renderer => _renderer;

    public string PresentationDiagnostic => "Presented through Android's hardware-accelerated Canvas.";

    public void InvalidateFrame()
    {
        _invalidated = true;
        ScheduleFrame();
    }

    public void Present(BRenderList renderList)
    {
        ArgumentNullException.ThrowIfNull(renderList);
        _invalidated = !_renderer.Present(Holder!, renderList, _density, _clearColor);
    }

    public bool PostToUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Looper.MyLooper() == Looper.MainLooper)
        {
            action();
            return true;
        }

        return Post(action);
    }

    public string? GetClipboardText()
    {
        if (Context?.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard || !clipboard.HasPrimaryClip)
            return null;

        ClipData.Item? item = clipboard.PrimaryClip?.GetItemAt(0);
        return item?.CoerceToText(Context)?.ToString();
    }

    public void SetClipboardText(string text)
    {
        if (Context?.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard)
            clipboard.PrimaryClip = ClipData.NewPlainText("Broiler", text ?? string.Empty);
    }

    public void NotifyCaretChanged(UiTextCaretInfo? caret)
    {
        if (Context?.GetSystemService(Context.InputMethodService) is not InputMethodManager inputMethod)
            return;

        if (caret is null)
        {
            _textOwner = null;
            _selectionStart = -1;
            _selectionEnd = -1;
            inputMethod.HideSoftInputFromWindow(WindowToken, HideSoftInputFlags.None);
            _input.TextDevice.NotifyFocusLost();
            return;
        }

        RequestFocus();
        int selectionEnd = caret.SelectionStart + caret.SelectionLength;
        if (!ReferenceEquals(_textOwner, caret.Owner))
        {
            _textOwner = caret.Owner;
            _selectionStart = caret.SelectionStart;
            _selectionEnd = selectionEnd;
            inputMethod.RestartInput(this);
            inputMethod.ShowSoftInput(this, ShowFlags.Implicit);
        }
        else if (_selectionStart != caret.SelectionStart || _selectionEnd != selectionEnd)
        {
            _selectionStart = caret.SelectionStart;
            _selectionEnd = selectionEnd;
            inputMethod.UpdateSelection(this, _selectionStart, _selectionEnd, -1, -1);
        }
    }

    public void SetResumed(bool resumed)
    {
        _resumed = resumed;
        if (!resumed)
        {
            Choreographer.Instance?.RemoveFrameCallback(this);
            _frameScheduled = false;
            _input.NotifyFocusLost();
            return;
        }

        InvalidateFrame();
    }

    public override bool OnTouchEvent(MotionEvent? e) =>
        e is not null && (_input.ProcessMotionEvent(e) || base.OnTouchEvent(e));

    public override bool OnHoverEvent(MotionEvent? e) =>
        e is not null && (_input.ProcessMotionEvent(e) || base.OnHoverEvent(e));

    public bool ProcessKeyEvent(KeyEvent keyEvent) => _input.ProcessKeyEvent(keyEvent);

    public override bool OnCheckIsTextEditor() => true;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        if (outAttrs is null)
            return null;

        outAttrs.InputType = global::Android.Text.InputTypes.ClassText |
            global::Android.Text.InputTypes.TextFlagCapSentences |
            global::Android.Text.InputTypes.TextFlagMultiLine;
        outAttrs.ImeOptions = ImeFlags.NoExtractUi;
        AndroidEditorSelection selection = _input.TextDevice.GetSelection();
        outAttrs.InitialSelStart = selection.SelectionStart;
        outAttrs.InitialSelEnd = selection.SelectionEnd;
        return new BroilerInputConnection(this, _input);
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        _surfaceReady = true;
        InvalidateFrame();
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        UpdateGeometry(width, height);
        _surfaceReady = true;
        InvalidateFrame();
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _surfaceReady = false;
        ReleaseGraphicsResources?.Invoke();
    }

    public void DoFrame(long frameTimeNanos)
    {
        _frameScheduled = false;
        if (_disposed || !_resumed || !_surfaceReady)
            return;

        try
        {
            if (AnimationActive)
                StepAnimation?.Invoke();
            if (_invalidated || AnimationActive)
            {
                RenderFrame?.Invoke();
            }
        }
        catch (BDeviceLostException)
        {
            ReleaseGraphicsResources?.Invoke();
            _invalidated = true;
        }

        if (_invalidated || AnimationActive)
            ScheduleFrame();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Choreographer.Instance?.RemoveFrameCallback(this);
            if (_surfaceReady)
                SurfaceDestroyed(Holder!);
            _input.Dispose();
            _renderer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DispatchInputCore(UiInputEvent input)
    {
        DispatchInput?.Invoke(input);
        InvalidateFrame();
    }

    private IUiTextEditor? ResolveTextEditor() => GetSession?.Invoke()?.FocusedElement as IUiTextEditor;

    private void UpdateGeometry(int physicalWidth, int physicalHeight)
    {
        _density = ResolveDensity(Context!);
        _input.Density = _density;
        _viewportSize = new BSize(
            Math.Max(1, physicalWidth) / _density,
            Math.Max(1, physicalHeight) / _density);
    }

    private void ScheduleFrame()
    {
        if (_frameScheduled || !_resumed || !_surfaceReady || _disposed)
            return;

        _frameScheduled = true;
        Choreographer.Instance?.PostFrameCallback(this);
    }

    private static double ResolveDensity(Context context)
    {
        double density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        return double.IsFinite(density) && density > 0 ? density : 1;
    }
}
