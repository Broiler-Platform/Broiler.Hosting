using System;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.RenderList;
using Broiler.UI;

namespace Broiler.Hosting.Android;

/// <summary>
/// Reusable <see cref="IUiHost"/> implementation for Android platforms.
/// Bridges viewport geometry, high-DPI scaling, frame presentation, clipboard access, and IME caret tracking.
/// </summary>
public class AndroidUiHost : IUiHost, IUiClipboardHost, IUiTextInputHost, IDisposable
{
    private readonly Func<BSize> _getViewportSize;
    private readonly Func<double> _getScale;
    private readonly Action<UiInvalidation> _invalidate;
    private readonly Action<BRenderList> _present;
    private readonly Func<string?>? _getClipboardText;
    private readonly Action<string>? _setClipboardText;
    private readonly Action<UiTextCaretInfo?>? _caretChanged;
    private readonly Action? _onDispose;
    private readonly IBroilerRenderer? _renderer;
    private readonly IBroilerSurface? _surface;
    private long _frameIndex;
    private bool _disposed;

    public AndroidUiHost(
        Func<BSize> getViewportSize,
        Func<double> getScale,
        Action<UiInvalidation> invalidate,
        Action<BRenderList> present,
        Func<string?>? getClipboardText = null,
        Action<string>? setClipboardText = null,
        Action<UiTextCaretInfo?>? caretChanged = null,
        Action? onDispose = null)
    {
        _getViewportSize = getViewportSize ?? throw new ArgumentNullException(nameof(getViewportSize));
        _getScale = getScale ?? throw new ArgumentNullException(nameof(getScale));
        _invalidate = invalidate ?? throw new ArgumentNullException(nameof(invalidate));
        _present = present ?? throw new ArgumentNullException(nameof(present));
        _getClipboardText = getClipboardText;
        _setClipboardText = setClipboardText;
        _caretChanged = caretChanged;
        _onDispose = onDispose;
    }

    public AndroidUiHost(IBroilerRenderer renderer, IBroilerSurface surface)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _getViewportSize = () => _surface.Size;
        _getScale = () => _surface.DpiScale;
        _invalidate = _ => IsInvalidated = true;
        _present = list =>
        {
            _renderer.Render(_surface, list, BFrameContext.Default.WithFrameIndex(_frameIndex++));
        };
    }

#if ANDROID
    public AndroidUiHost(AndroidBroilerView view)
        : this(
            () => (view ?? throw new ArgumentNullException(nameof(view))).ViewportSize,
            () => view.Scale,
            _ => view.InvalidateFrame(),
            view.Present,
            view.GetClipboardText,
            view.SetClipboardText,
            view.NotifyCaretChanged,
            view.Dispose)
    {
    }
#endif

    public BSize ViewportSize => _getViewportSize();

    public double Scale => _getScale();

    public bool IsInvalidated { get; private set; } = true;

    public BRenderList CreateRenderList(int capacity = 0) => new(capacity);

    public void Invalidate(UiInvalidation invalidation)
    {
        IsInvalidated = true;
        _invalidate(invalidation);
    }

    public void Present(BRenderList renderList)
    {
        ArgumentNullException.ThrowIfNull(renderList);
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsInvalidated = false;
        try
        {
            _present(renderList);
        }
        catch
        {
            IsInvalidated = true;
            throw;
        }
    }

    public bool TryGetText(out string text)
    {
        text = _getClipboardText?.Invoke() ?? string.Empty;
        return !string.IsNullOrEmpty(text);
    }

    public void SetText(string text) => _setClipboardText?.Invoke(text ?? string.Empty);

    public void PublishCaret(UiTextCaretInfo caret) => _caretChanged?.Invoke(caret);

    public void ClearCaret(UiElement owner) => _caretChanged?.Invoke(null);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _onDispose?.Invoke();
        _renderer?.Dispose();
        _surface?.Dispose();
    }
}
