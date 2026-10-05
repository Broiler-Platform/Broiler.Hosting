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
using Broiler.UI.TreeView;
using Broiler.UI.TreeView.Standard;
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

    [Fact]
    public void MovingTheFocusDownATreeInvalidatesItsRowsOnceItHasScrolled()
    {
        var (dispatcher, bridge, root) = Create();
        bridge.ClientsListening = () => true;
        var tree = new StandardTreeView { DataSource = new Numbered(60), VisibleRowCapacity = 6 };
        root.AddChild(tree);
        dispatcher.Drain();
        var treePeer = bridge.GetOrCreatePeer(tree);
        var first = Assert.IsType<WindowsElementAutomationPeer>(treePeer.Navigate(NavigateDirection.FirstChild));
        _ = NativeProviderAdapter.For(first);
        var raised = new List<IReadOnlyList<IRawElementProviderSimple>>();
        var disconnected = new List<IRawElementProviderSimple>();
        bridge.StructureInvalidated += raised.Add;
        bridge.ProviderDisconnected += disconnected.Add;

        // As the arrow keys do: the focus moves to row 9, and then the tree scrolls it into view.
        tree.MoveFocus(10, extendSelection: false);
        Assert.Equal(4, tree.FirstVisibleRow);

        dispatcher.Drain();
        Assert.Equal([treePeer], Assert.Single(raised));
        // The row that left the view is gone for clients, and the tree's children start at the view.
        Assert.False(first.IsAlive);
        Assert.Equal([first], disconnected);
        Assert.StartsWith("n4,", Name(treePeer.Navigate(NavigateDirection.FirstChild)));
    }

    [Fact]
    public void TheFocusedRowStaysATreeChildWhileTheTreeScrollsAwayFromIt()
    {
        var (dispatcher, bridge, root) = Create();
        var tree = new StandardTreeView { DataSource = new Numbered(60), VisibleRowCapacity = 6, SelectionMode = TreeSelectionMode.Extended };
        root.AddChild(tree);
        tree.SetSelection([new("n0"), new("n2")]);
        bridge.Session.SetFocus(tree);
        dispatcher.Drain();
        var treePeer = bridge.GetOrCreatePeer(tree);
        var focused = Assert.IsType<WindowsElementAutomationPeer>(bridge.GetFocus());
        Assert.StartsWith("n2,", Name(focused));

        // A wheel scrolls without moving the focus.
        tree.FirstVisibleRow = 20;
        Assert.True(focused.IsAlive);
        Assert.Same(focused, bridge.GetFocus());
        Assert.Same(focused, treePeer.Navigate(NavigateDirection.FirstChild));
        Assert.Same(treePeer, focused.Navigate(NavigateDirection.Parent));
        Assert.StartsWith("n20,", Name(focused.Navigate(NavigateDirection.NextSibling)));
        Assert.Null(focused.Navigate(NavigateDirection.PreviousSibling));
        Assert.Equal(true, focused.GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
        // A selected row that is neither in view nor focused is no child, so it is not in the selection.
        var selection = Assert.IsAssignableFrom<ISelectionProvider>(treePeer.GetPatternProvider(UiaNative.UiaSelectionPatternId)).GetSelection();
        Assert.Same(focused, Assert.Single(selection!));

        // Scrolling it back into view tells clients that the rows changed.
        var raised = new List<IReadOnlyList<IRawElementProviderSimple>>();
        bridge.StructureInvalidated += raised.Add;
        Assert.IsAssignableFrom<IScrollItemProvider>(focused.GetPatternProvider(UiaNative.UiaScrollItemPatternId)).ScrollIntoView();
        Assert.Equal(2, tree.FirstVisibleRow);
        dispatcher.Drain();
        Assert.Equal([treePeer], Assert.Single(raised));
    }

    private static string? Name(IRawElementProviderSimple? provider) => provider?.GetPropertyValue(UiaNative.UiaNamePropertyId) as string;

    /// <summary>A flat tree of <c>n0</c> to <c>n(count - 1)</c>.</summary>
    private sealed class Numbered(int count) : ITreeDataSource
    {
        public TreeNodeId Root => new("root");
        public int GetChildCount(TreeNodeId node) => node.Value == "root" ? count : 0;
        public TreeNodeId GetChild(TreeNodeId node, int index) => new($"n{index}");
        public bool CanExpand(TreeNodeId node) => false;
        public TreeNodePresentation GetPresentation(TreeNodeId node) => new(node, node.Value);
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
