using System;
using System.IO;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.RenderList;
using Broiler.Graphics.Resources;
using Broiler.Hosting.Linux;
using Broiler.Input;
using Broiler.Input.Keyboard;
using Broiler.UI;
using Xunit;

namespace Broiler.Hosting.Linux.Tests;

public sealed class LinuxHostingTests
{
    private sealed class TestSurface : IBroilerSurface
    {
        public BSize Size { get; private set; } = new(1100, 720);
        public double DpiScale { get; private set; } = 1;
        public void Resize(BSize size, double dpiScale) { Size = size; DpiScale = dpiScale; }
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
    public void LinuxUiHost_ViewportUsesCurrentSurfaceSizeAndDpiWithoutDoubleScaling()
    {
        using var renderer = new RecordingRenderer();
        using var surface = new TestSurface();
        var host = new LinuxUiHost(renderer, surface);
        surface.Resize(new BSize(800, 600), 1.5);
        Assert.Equal(new BSize(800, 600), host.ViewportSize);
        Assert.Equal(1.5, host.Scale);
        surface.Resize(new BSize(1024, 768), 2);
        Assert.Equal(new BSize(1024, 768), host.ViewportSize);
        Assert.Equal(2, host.Scale);
    }

    [Fact]
    public void LinuxUiHost_FramesReachSelectedSurfaceAndInvalidationIsPreserved()
    {
        using var renderer = new RecordingRenderer();
        using var surface = new TestSurface();
        var host = new LinuxUiHost(renderer, surface);
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
    public void LinuxUiHost_RenderingFailureRemainsPendingAndPropagatesToWindowOwner()
    {
        using var renderer = new RecordingRenderer();
        using var surface = new TestSurface();
        var host = new LinuxUiHost(renderer, surface);
        var failure = new InvalidOperationException("Synthetic renderer failure");
        renderer.OnRender = () => throw failure;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => host.Present(host.CreateRenderList())));
        Assert.True(host.IsInvalidated);
        renderer.OnRender = null;
        host.Present(host.CreateRenderList());
        Assert.False(host.IsInvalidated);
        Assert.Equal(1, renderer.LastFrame.FrameIndex);
    }

    [Fact]
    public void LinuxX11Clipboard_TryOpen_SafeOnHostOperatingSystem()
    {
        // On non-Linux (e.g. Windows runner), TryOpen returns null safely without DllNotFoundException
        var clipboard = LinuxX11Clipboard.TryOpen();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(clipboard);
        }
    }

    [Fact]
    public void LinuxBackendDiagnostics_EnforcesLinuxOperatingSystemRequirement()
    {
        using var writer = new StringWriter();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => LinuxBackendDiagnostics.Run(writer));
        }
    }

    [Fact]
    public void LinuxInputCoordinator_CoordinatesExternalPointerAndDrainsEvents()
    {
        var logs = new System.Collections.Generic.List<string>();
        var coordinator = new LinuxInputCoordinator(enabled: true, log: logs.Add, externalPointer: true, applicationName: "TestApp");

        coordinator.SetViewport(new BSize(1000, 800));
        coordinator.SetExternalPointer(250, 300);

        var events = new System.Collections.Generic.List<UiInputEvent>();
        int drained = coordinator.Drain(events.Add);

        Assert.True(drained > 0);
        Assert.Single(events);
        Assert.Equal(UiInputEventKind.PointerMove, events[0].Kind);

        LinuxInputSnapshot snapshot = coordinator.Snapshot;
        Assert.True(snapshot.Enabled);
        Assert.Equal(250, snapshot.PointerX);
        Assert.Equal(300, snapshot.PointerY);
    }

    [Fact]
    public void LinuxInputCoordinator_EscapeRequestsQuitOnlyWhenOptedIn()
    {
        var defaultCoordinator = new LinuxInputCoordinator(enabled: true, log: _ => { });
        defaultCoordinator.OnKeyChanged(Key("Escape", KeyboardKeyTransition.Down));
        Assert.False(defaultCoordinator.QuitOnEscape);
        Assert.False(defaultCoordinator.QuitRequested);

        var coordinator = new LinuxInputCoordinator(enabled: true, log: _ => { }) { QuitOnEscape = true };
        coordinator.OnKeyChanged(Key("A", KeyboardKeyTransition.Down));
        coordinator.OnKeyChanged(Key("Escape", KeyboardKeyTransition.Up));
        Assert.False(coordinator.QuitRequested);

        coordinator.OnKeyChanged(Key("Escape", KeyboardKeyTransition.Down));
        Assert.True(coordinator.QuitRequested);

        // The key still reaches the UI; quitting is the host's decision.
        var events = new System.Collections.Generic.List<UiInputEvent>();
        coordinator.Drain(events.Add);
        Assert.Contains(events, e => e.Kind == UiInputEventKind.KeyboardKey);
    }

    private static KeyboardKeyEvent Key(string name, KeyboardKeyTransition transition) => new(
        new InputEventHeader(InputDeviceId.FromOpaqueValue("test:keyboard"), new InputTimestamp(0, 1, "test"), 0),
        KeyboardKey.FromName(name),
        transition,
        default,
        NativeKeyCode: 0,
        ScanCode: 0,
        RepeatCount: 0,
        IsExtended: false,
        WasDown: false);
}
