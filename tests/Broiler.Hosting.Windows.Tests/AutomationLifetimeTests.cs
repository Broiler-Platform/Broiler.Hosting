using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.Native.Windows;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>
/// Provider lifetime at the native boundary: removed, disposed and detached elements, bridge disposal,
/// and a real window that is destroyed while clients still hold its elements.
/// </summary>
public sealed class AutomationLifetimeTests
{
    private const int ElementNotAvailable = unchecked((int)0x80040201);

    [Fact]
    public void RemovedElementIsNotAvailableAndReportsNoParent()
    {
        var (_, bridge, root) = Create();
        var panel = new StandardPanel();
        var button = new StandardButton { Text = "Open HTML preview" };
        panel.AddChild(button);
        root.AddChild(panel);
        var peer = bridge.GetOrCreatePeer(button);
        Assert.Same(bridge.GetOrCreatePeer(panel), peer.Navigate(NavigateDirection.Parent));

        // Removed from the tree but not disposed, as a view that is replaced and kept for later.
        root.RemoveChild(panel);
        Assert.False(button.IsDisposed);
        Assert.False(peer.IsAlive);
        Assert.Null(peer.Navigate(NavigateDirection.Parent));
        AssertNotAvailable(() => Native(peer).GetPropertyValue(UiaNative.UiaNamePropertyId));
        AssertNotAvailable(() => ((INativeFragment)Native(peer)).Navigate(NavigateDirection.Parent));

        // Back in the tree, the same peer answers again.
        root.AddChild(panel);
        Assert.True(peer.IsAlive);
        Assert.Equal("Open HTML preview", ReadName(peer));
    }

    [Fact]
    public void DisposedElementRemovedItemAndItsTextRangeAreNotAvailable()
    {
        var (_, bridge, root) = Create();
        var button = new StandardButton { Text = "Send" };
        var edit = new StandardEdit { Text = "draft" };
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo")]);
        root.AddChild(button);
        root.AddChild(edit);
        root.AddChild(list);
        var buttonPeer = bridge.GetOrCreatePeer(button);
        var bravo = bridge.GetOrCreateItemPeer(list, 1);
        var range = ((INativeText)Native(bridge.GetOrCreatePeer(edit))).GetDocumentRange()!;
        Assert.Equal("draft", range.GetText(-1));

        button.Dispose();
        list.SetItems([new UiListItem("a", "Alpha")]);
        root.RemoveChild(edit);

        AssertNotAvailable(() => Native(buttonPeer).GetPropertyValue(UiaNative.UiaNamePropertyId));
        AssertNotAvailable(() => ((INativeSelectionItem)Native(bravo)).Select());
        AssertNotAvailable(() => range.GetText(-1));
    }

    [Fact]
    public void DisposingTheBridgeDisconnectsEveryWrapperItHandedOut()
    {
        var (_, bridge, root) = Create();
        var button = new StandardButton { Text = "Send" };
        var other = new StandardButton { Text = "Cancel" };
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha")]);
        root.AddChild(button);
        root.AddChild(other);
        root.AddChild(list);
        var buttonPeer = bridge.GetOrCreatePeer(button);
        var itemPeer = bridge.GetOrCreateItemPeer(list, 0);
        var otherPeer = bridge.GetOrCreatePeer(other); // never handed to UIA
        INativeSimple rootWrapper = Native(bridge), buttonWrapper = Native(buttonPeer);
        _ = Native(itemPeer);

        var disconnected = new List<IRawElementProviderSimple>();
        bridge.ProviderDisconnected += disconnected.Add;
        bridge.Dispose();

        Assert.Contains(bridge, disconnected);
        Assert.Contains(buttonPeer, disconnected);
        Assert.Contains(itemPeer, disconnected);
        Assert.DoesNotContain(otherPeer, disconnected);
        AssertNotAvailable(() => buttonWrapper.GetPropertyValue(UiaNative.UiaNamePropertyId));
        AssertNotAvailable(() => ((INativeFragment)rootWrapper).GetRuntimeId());
    }

    [Fact]
    public unsafe void NotAvailableCrossesTheComBoundaryAsItsHResult()
    {
        var (_, bridge, root) = Create();
        var button = new StandardButton { Text = "Send" };
        root.AddChild(button);
        void* unknown = ComInterfaceMarshaller<INativeSimple>.ConvertToUnmanaged(Native(bridge.GetOrCreatePeer(button)));
        try
        {
            Guid fragmentIid = typeof(INativeFragment).GUID;
            Assert.Equal(0, Marshal.QueryInterface((nint)unknown, in fragmentIid, out nint fragment));
            try
            {
                // IRawElementProviderFragment: the three IUnknown slots, Navigate, then GetRuntimeId.
                var getRuntimeId = (delegate* unmanaged<nint, nint*, int>)(*(void***)fragment)[4];
                nint array;
                Assert.Equal(0, getRuntimeId(fragment, &array));
                Assert.NotEqual(0, array);
                OleAutNative.SafeArrayDestroy(array);

                root.RemoveChild(button);
                array = 0;
                Assert.Equal(ElementNotAvailable, getRuntimeId(fragment, &array));
                Assert.Equal(0, array);
            }
            finally { Marshal.Release(fragment); }
        }
        finally { ComInterfaceMarshaller<INativeSimple>.Free(unknown); }
    }

    [Fact]
    public void CrossThreadCallsRunOnTheWindowThread()
    {
        using var window = new AutomationWindowHarness(root => root.AddChild(new ThreadProbe()));
        var probe = (ThreadProbe)window.Root.Children[0];
        var peer = window.Invoke(() => window.Bridge.GetOrCreatePeer(probe));

        Assert.Equal("Probe", ReadName(peer));
        Assert.Equal(window.UiThreadId, probe.LastThread);
        // A fragment root hosted in a window leaves its runtime ID to the window.
        Assert.Null(window.Invoke(() => window.Bridge.GetRuntimeId()));
    }

    [Fact]
    public void AfterTheWindowIsDestroyedHeldElementsFailAtOnceAsNotAvailable()
    {
        using var window = new AutomationWindowHarness(root => root.AddChild(new StandardButton { Text = "Send" }));
        var peer = window.Invoke(() => window.Bridge.GetOrCreatePeer(window.Root.Children[0]));
        INativeSimple element = Native(peer);
        var rootFragment = (INativeFragment)Native(window.Bridge);
        Assert.Equal("Send", ReadName(peer));

        window.DestroyWindow();

        var elapsed = Stopwatch.StartNew();
        AssertNotAvailable(() => element.GetPropertyValue(UiaNative.UiaNamePropertyId));
        AssertNotAvailable(() => rootFragment.GetRuntimeId());
        Assert.True(elapsed.ElapsedMilliseconds < 500, $"The calls took {elapsed.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void DestroyingTheWindowReleasesItsProviders()
    {
        using var window = new AutomationWindowHarness(root => root.AddChild(new StandardButton { Text = "Send" }));
        var disconnected = new ConcurrentBag<IRawElementProviderSimple>();
        var peer = window.Invoke(() =>
        {
            window.Bridge.ProviderDisconnected += disconnected.Add;
            return window.Bridge.GetOrCreatePeer(window.Root.Children[0]);
        });
        _ = Native(peer);
        _ = Native(window.Bridge);

        window.DestroyWindow();

        Assert.True(window.Bridge.IsTornDown);
        Assert.Contains(window.Bridge, disconnected);
        Assert.Contains(peer, disconnected);
        Assert.False(peer.IsAlive);
    }

    private static INativeSimple Native(IRawElementProviderSimple provider) => NativeProviderAdapter.For(provider)!;

    private static string? ReadName(IRawElementProviderSimple provider)
    {
        using ComVariant name = Native(provider).GetPropertyValue(UiaNative.UiaNamePropertyId).ToVariant();
        return name.As<string>();
    }

    private static void AssertNotAvailable(Action call)
    {
        var error = Assert.Throws<COMException>(call);
        Assert.Equal(ElementNotAvailable, error.HResult);
    }

    private static void AssertNotAvailable<T>(Func<T> call) => AssertNotAvailable(() => { _ = call(); });

    private static (UiSession Session, WindowsAutomationBridge Bridge, StandardPanel Root) Create()
    {
        var session = new StandardUiSessionBuilder().WithDispatcher(new ImmediateUiDispatcher()).Build(new Host());
        var root = new StandardPanel();
        root.Arrange(new BRect(0, 0, 800, 600));
        session.AddRoot(root);
        return (session, new WindowsAutomationBridge(nint.Zero, session, root), root);
    }

    /// <summary>Records the thread its semantic node was last built on.</summary>
    private sealed class ThreadProbe : UiElement
    {
        public int LastThread { get; private set; }

        protected override UiSemanticNode GetSemanticNodeCore()
        {
            LastThread = Environment.CurrentManagedThreadId;
            return new(UiSemanticRole.Label, "Probe", Bounds, UiSemanticState.Visible | UiSemanticState.Enabled, [], Id: SemanticId);
        }
    }

    private sealed class Host : IUiHost
    {
        public BSize ViewportSize => new(800, 600);
        public double Scale => 1.0;
        public BRenderList CreateRenderList(int capacity = 0) => new();
        public void Invalidate(UiInvalidation invalidation) { }
        public void Present(BRenderList renderList) { }
    }
}
