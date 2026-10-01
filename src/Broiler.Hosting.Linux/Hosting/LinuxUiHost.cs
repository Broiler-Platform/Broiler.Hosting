using System;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.RenderList;
using Broiler.UI;

namespace Broiler.Hosting.Linux;

/// <summary>
/// UI-thread host adapter over a caller-owned renderer and surface for Linux desktop applications.
/// The outer window loop owns scheduling, resize notifications, device recovery, and disposal.
/// </summary>
public sealed class LinuxUiHost : IUiHost
{
    private readonly IBroilerRenderer _renderer;
    private readonly IBroilerSurface _surface;
    private long _frameIndex;

    public LinuxUiHost(IBroilerRenderer renderer, IBroilerSurface surface)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public BSize ViewportSize => _surface.Size;
    public double Scale => _surface.DpiScale;
    public bool IsInvalidated { get; private set; } = true;

    public BRenderList CreateRenderList(int capacity = 0) => new(capacity);
    public void Invalidate(UiInvalidation invalidation) => IsInvalidated = true;

    public void Present(BRenderList renderList)
    {
        ArgumentNullException.ThrowIfNull(renderList);
        // Clear before rendering so an invalidation raised during the frame is retained.
        IsInvalidated = false;
        try
        {
            _renderer.Render(_surface, renderList, BFrameContext.Default.WithFrameIndex(_frameIndex++));
        }
        catch
        {
            IsInvalidated = true;
            throw;
        }
    }
}
