using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>Structure changes from Broiler.UI: coalesced until the dispatcher runs, raised on the changed parent's peer.</summary>
public sealed class AutomationStructureTests
{
    [Fact]
    public void RemovedElementsAreDisconnectedWhenTheDispatcherNextRuns()
    {
        var (dispatcher, bridge, root) = Create();
        var panel = new StandardPanel();
        var removed = new StandardButton { Text = "Open HTML preview" };
        var kept = new StandardButton { Text = "Reply" };
        panel.AddChild(removed);
        panel.AddChild(kept);
        root.AddChild(panel);
        dispatcher.Drain();
        var removedPeer = bridge.GetOrCreatePeer(removed);
        _ = NativeProviderAdapter.For(removedPeer);
        _ = NativeProviderAdapter.For(bridge.GetOrCreatePeer(kept));
        var disconnected = new List<IRawElementProviderSimple>();
        bridge.ProviderDisconnected += disconnected.Add;

        panel.RemoveChild(removed);
        Assert.Empty(disconnected);

        dispatcher.Drain();
        Assert.Equal([removedPeer], disconnected);
    }

    [Fact]
    public void ChangesAreCoalescedIntoOneEventOnEachChangedParentsPeer()
    {
        var (dispatcher, bridge, root) = Create();
        var form = new StandardPanel();
        var section = new StandardPanel();
        var actions = new StandardPanel();
        var unvisited = new StandardPanel();
        form.AddChild(section);
        root.AddChild(form);
        root.AddChild(actions);
        root.AddChild(unvisited);
        dispatcher.Drain();
        WindowsElementAutomationPeer formPeer = bridge.GetOrCreatePeer(form), sectionPeer = bridge.GetOrCreatePeer(section), actionsPeer = bridge.GetOrCreatePeer(actions);
        var raised = new List<IReadOnlyList<IRawElementProviderSimple>>();
        bridge.StructureInvalidated += raised.Add;

        // Changes made between frames arrive from Broiler.UI one by one.
        for (int i = 0; i < 50; i++) actions.AddChild(new StandardButton { Text = $"Action {i}" });
        section.AddChild(new StandardButton { Text = "Cc" });
        form.AddChild(new StandardButton { Text = "Bcc" });
        unvisited.AddChild(new StandardButton { Text = "Never reached" });
        Assert.Empty(raised);

        dispatcher.Drain();
        // One event for the actions and one for the form, which covers its section; none for the window
        // as a whole, and none for a container no client has reached.
        var only = Assert.Single(raised);
        Assert.Equal(2, only.Count);
        Assert.Contains(actionsPeer, only);
        Assert.Contains(formPeer, only);
        Assert.DoesNotContain(sectionPeer, only);
        Assert.DoesNotContain(bridge, only);
    }

    [Fact]
    public void ReplacedListItemsInvalidateTheListAndDropTheirPeers()
    {
        var (dispatcher, bridge, root) = Create();
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo")]);
        root.AddChild(list);
        dispatcher.Drain();
        var listPeer = bridge.GetOrCreatePeer(list);
        var bravo = bridge.GetOrCreateItemPeer(list, 1);
        _ = NativeProviderAdapter.For(bravo);
        var raised = new List<IReadOnlyList<IRawElementProviderSimple>>();
        var disconnected = new List<IRawElementProviderSimple>();
        bridge.StructureInvalidated += raised.Add;
        bridge.ProviderDisconnected += disconnected.Add;

        list.SetItems([new UiListItem("new", "Newest"), new UiListItem("a", "Alpha")]);
        dispatcher.Drain();

        Assert.Equal([listPeer], Assert.Single(raised));
        Assert.Equal([bravo], disconnected);
    }

    [Fact]
    public void AChangeToTheRootInvalidatesTheWindow()
    {
        var (dispatcher, bridge, root) = Create();
        var raised = new List<IReadOnlyList<IRawElementProviderSimple>>();
        bridge.StructureInvalidated += raised.Add;

        root.AddChild(new StandardButton { Text = "Send" });
        dispatcher.Drain();

        Assert.Equal([bridge], Assert.Single(raised));
    }

    private static (StandardQueuedUiDispatcher Dispatcher, WindowsAutomationBridge Bridge, StandardPanel Root) Create()
    {
        var dispatcher = new StandardQueuedUiDispatcher();
        var session = new StandardUiSessionBuilder().WithDispatcher(dispatcher).Build(new Host());
        var root = new StandardPanel();
        root.Arrange(new BRect(0, 0, 800, 600));
        session.AddRoot(root);
        return (dispatcher, new WindowsAutomationBridge(nint.Zero, session, root), root);
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
