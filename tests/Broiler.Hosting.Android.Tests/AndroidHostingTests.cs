using System;
using System.IO;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.RenderList;
using Broiler.Graphics.Resources;
using Broiler.Hosting.Android;
using Broiler.UI;
using Xunit;

namespace Broiler.Hosting.Android.Tests;

public sealed class AndroidHostingTests
{
    private sealed class TestSurface : IBroilerSurface
    {
        public BSize Size { get; private set; } = new(1080, 2400);
        public double DpiScale { get; private set; } = 2.75;
        public void Resize(BSize size, double dpiScale)
        {
            Size = size;
            DpiScale = dpiScale;
        }
        public void Dispose() { }
    }

    private sealed class RecordingRenderer : IBroilerRenderer
    {
        public Action? OnRender { get; set; }
        public IBroilerSurface? LastSurface { get; private set; }
        public BRenderList? LastList { get; private set; }
        public BFrameContext LastFrame { get; private set; }

        public void Render(IBroilerSurface surface, BRenderList renderList, BFrameContext frameContext)
        {
            LastSurface = surface;
            LastList = renderList;
            LastFrame = frameContext;
            OnRender?.Invoke();
        }

        public IBroilerSurface CreateSurface(BSurfaceDescriptor descriptor) => throw new NotSupportedException();
        public BImageHandle CreateImage(ReadOnlySpan<byte> encodedImage) => throw new NotSupportedException();
        public BImageHandle CreateImage(BPixelBuffer pixels) => throw new NotSupportedException();
        public void ReleaseImage(BImageHandle image) => throw new NotSupportedException();
        public BBitmap RenderToImage(BRenderList renderList, BSurfaceDescriptor descriptor, BFrameContext frameContext) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public void AndroidUiHost_ViewportUsesCurrentSurfaceSizeAndDpiWithoutDoubleScaling()
    {
        using var renderer = new RecordingRenderer();
        using var surface = new TestSurface();
        using var host = new AndroidUiHost(renderer, surface);

        Assert.Equal(new BSize(1080, 2400), host.ViewportSize);
        Assert.Equal(2.75, host.Scale);

        surface.Resize(new BSize(1440, 3120), 3.5);
        Assert.Equal(new BSize(1440, 3120), host.ViewportSize);
        Assert.Equal(3.5, host.Scale);
    }

    [Fact]
    public void AndroidUiHost_FramesReachSelectedSurfaceAndInvalidationIsPreserved()
    {
        using var renderer = new RecordingRenderer();
        using var surface = new TestSurface();
        using var host = new AndroidUiHost(renderer, surface);

        var list = host.CreateRenderList();
        Assert.True(host.IsInvalidated);

        host.Present(list);
        Assert.False(host.IsInvalidated);
        Assert.Same(surface, renderer.LastSurface);
        Assert.Same(list, renderer.LastList);
        Assert.Equal(0, renderer.LastFrame.FrameIndex);

        host.Invalidate(default);
        Assert.True(host.IsInvalidated);

        renderer.OnRender = () => host.Invalidate(default);
        host.Present(list);
        Assert.True(host.IsInvalidated);
        Assert.Equal(1, renderer.LastFrame.FrameIndex);

        renderer.OnRender = null;
        host.Present(list);
        Assert.False(host.IsInvalidated);
        Assert.Equal(2, renderer.LastFrame.FrameIndex);
    }

    [Fact]
    public void AndroidUiHost_RenderingFailureRemainsPendingAndPropagates()
    {
        using var renderer = new RecordingRenderer();
        using var surface = new TestSurface();
        using var host = new AndroidUiHost(renderer, surface);

        var failure = new InvalidOperationException("Synthetic Android surface failure");
        renderer.OnRender = () => throw failure;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => host.Present(host.CreateRenderList())));
        Assert.True(host.IsInvalidated);

        renderer.OnRender = null;
        host.Present(host.CreateRenderList());
        Assert.False(host.IsInvalidated);
        Assert.Equal(1, renderer.LastFrame.FrameIndex);
    }

    [Fact]
    public void AndroidUiHost_Clipboard_ReadWriteCycle()
    {
        string? stored = null;
        using var host = new AndroidUiHost(
            getViewportSize: () => new BSize(400, 800),
            getScale: () => 2.0,
            invalidate: _ => { },
            present: _ => { },
            getClipboardText: () => stored,
            setClipboardText: text => stored = text);

        Assert.False(host.TryGetText(out string text));
        Assert.Equal(string.Empty, text);

        host.SetText("Android clip text");
        Assert.True(host.TryGetText(out text));
        Assert.Equal("Android clip text", text);
    }

    [Fact]
    public void AndroidUiHost_CaretNotifications_DispatchesToListeners()
    {
        UiTextCaretInfo? received = null;
        using var host = new AndroidUiHost(
            getViewportSize: () => new BSize(400, 800),
            getScale: () => 2.0,
            invalidate: _ => { },
            present: _ => { },
            caretChanged: caret => received = caret);

        var caret = new UiTextCaretInfo(new UiElementMock(), new BRect(10, 20, 2, 16), CaretIndex: 5, SelectionStart: 3, SelectionLength: 2, IsCompositionActive: false);
        host.PublishCaret(caret);
        Assert.Same(caret, received);

        host.ClearCaret(caret.Owner);
        Assert.Null(received);
    }

    [Fact]
    public void AndroidBackendDiagnostics_SafeDegradationOnHostOS()
    {
        AndroidDiagnosticsReport report = AndroidBackendDiagnostics.Capture();
        Assert.NotNull(report);
        Assert.NotNull(report.OperatingSystemDescription);
        Assert.NotNull(report.Architecture);
        Assert.NotEmpty(report.Libraries);

        using var writer = new StringWriter();
        bool success = AndroidBackendDiagnostics.Run(writer);
        string text = writer.ToString();

        Assert.True(success);
        Assert.Contains("Broiler Android Backend Diagnostics", text);
        Assert.Contains("Native Libraries:", text);
    }

    private sealed class UiElementMock : UiElement
    {
    }
}
