using System;
using System.Collections.Generic;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.Standard;
using Broiler.UI.TabView.Standard;
using Broiler.UI.TreeView;
using Broiler.UI.TreeView.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>
/// Focus and selection of rows and tabs. UIA clients call SetFocus and then SelectionItem.Select: focus
/// must not select, and selecting must leave the focus where the application puts it, as a click does.
/// </summary>
public sealed class AutomationFocusTests
{
    [Fact]
    public void TabSetFocusFocusesTheTabViewWithoutSelecting()
    {
        var (session, bridge, root) = Create();
        var tabs = new StandardTabView();
        tabs.AddTab("inbox", "Inbox");
        tabs.AddTab("compose", "Compose");
        var elsewhere = new StandardButton { Text = "Elsewhere" };
        root.AddChild(tabs);
        root.AddChild(elsewhere);
        session.SetFocus(elsewhere);
        var inbox = bridge.GetOrCreateTabPeer(tabs, 0);
        var compose = bridge.GetOrCreateTabPeer(tabs, 1);
        // Only the tab the focus can be on says it takes the focus: the selected one.
        Assert.Equal(true, inbox.GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
        Assert.Equal(false, compose.GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));

        compose.SetFocus();

        Assert.Same(tabs, session.FocusedElement);
        Assert.Equal("inbox", tabs.SelectedTab?.Id);
        // The focused item of a tab view is its selected tab.
        Assert.Same(inbox, bridge.GetFocus());
        Assert.Equal(true, inbox.GetPropertyValue(UiaNative.UiaHasKeyboardFocusPropertyId));
        Assert.Equal(false, compose.GetPropertyValue(UiaNative.UiaHasKeyboardFocusPropertyId));
    }

    [Fact]
    public void OnlyTheItemTheFocusCanBeOnIsKeyboardFocusable()
    {
        var (_, bridge, root) = Create();
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo")]);
        var fixedList = new StandardListView { Focusable = false };
        fixedList.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo")]);
        var tree = new StandardTreeView { DataSource = new Folders() };
        root.AddChild(list);
        root.AddChild(fixedList);
        root.AddChild(tree);

        // Nothing selected: the list itself takes the focus, and no row can.
        Assert.Equal(false, bridge.GetOrCreateItemPeer(list, 0).GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
        list.SelectIndex(1);
        Assert.Equal(false, bridge.GetOrCreateItemPeer(list, 0).GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
        Assert.Equal(true, bridge.GetOrCreateItemPeer(list, 1).GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));

        // A list that takes no focus has no row that can, selected or not.
        fixedList.SelectIndex(0);
        Assert.False(fixedList.CanFocus);
        Assert.Equal(false, bridge.GetOrCreateItemPeer(fixedList, 0).GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));

        // In a tree it is the focused row.
        tree.MoveFocusToFirst();
        var rows = Assert.IsType<WindowsElementAutomationPeer>(bridge.GetOrCreatePeer(tree).Navigate(NavigateDirection.FirstChild));
        var next = Assert.IsType<WindowsElementAutomationPeer>(rows.Navigate(NavigateDirection.NextSibling));
        Assert.Equal(true, rows.GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
        Assert.Equal(false, next.GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
    }

    private sealed class Folders : ITreeDataSource
    {
        public TreeNodeId Root => new("root");
        public int GetChildCount(TreeNodeId node) => node.Value == "root" ? 2 : 0;
        public TreeNodeId GetChild(TreeNodeId node, int index) => new(index == 0 ? "inbox" : "archive");
        public bool CanExpand(TreeNodeId node) => false;
        public TreeNodePresentation GetPresentation(TreeNodeId node) => new(node, node.Value);
    }

    [Fact]
    public void SelectingATabThroughAutomationLeavesTheFocusWhereTheApplicationPutsIt()
    {
        var (session, bridge, root) = Create();
        var reply = new StandardButton { Text = "Reply" };
        var body = new StandardEdit();
        var tabs = new StandardTabView();
        tabs.AddTab("inbox", "Inbox", reply);
        tabs.AddTab("compose", "Compose", body);
        root.AddChild(tabs);
        tabs.SelectTab("compose");
        session.SetFocus(body);
        // As Broiler.Mail does: choosing the Inbox tab returns to the reader.
        tabs.SelectionChanged += (_, _) => session.SetFocus(tabs.SelectedTab?.Id == "inbox" ? reply : tabs);

        var inbox = bridge.GetOrCreateTabPeer(tabs, 0);
        inbox.SetFocus();
        Assert.Equal("compose", tabs.SelectedTab?.Id);
        Assert.IsAssignableFrom<ISelectionItemProvider>(inbox.GetPatternProvider(UiaNative.UiaSelectionItemPatternId)).Select();

        Assert.Equal("inbox", tabs.SelectedTab?.Id);
        Assert.Same(reply, session.FocusedElement);
    }

    [Fact]
    public void ListRowSetFocusDoesNotSelectAndSelectActsLikeAClick()
    {
        var (session, bridge, root) = Create();
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo"), new UiListItem("c", "Charlie")]);
        var elsewhere = new StandardButton { Text = "Elsewhere" };
        root.AddChild(list);
        root.AddChild(elsewhere);
        session.SetFocus(elsewhere);

        bridge.GetOrCreateItemPeer(list, 1).SetFocus();
        Assert.Same(list, session.FocusedElement);
        Assert.Null(list.SelectedItemId);

        session.SetFocus(elsewhere);
        var charlie = bridge.GetOrCreateItemPeer(list, 2);
        Assert.IsAssignableFrom<ISelectionItemProvider>(charlie.GetPatternProvider(UiaNative.UiaSelectionItemPatternId)).Select();
        // A click focuses the list and selects the row; so does Select.
        Assert.Same(list, session.FocusedElement);
        Assert.Equal("c", list.SelectedItemId);
        Assert.Same(charlie, bridge.GetFocus());
    }

    [Fact]
    public void FocusEventsNameWhatGetFocusReports()
    {
        var (session, bridge, root) = Create();
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo")]);
        root.AddChild(list);
        list.SelectIndex(0);
        var listPeer = bridge.GetOrCreatePeer(list);

        session.SetFocus(list);
        Assert.Same(bridge.GetOrCreateItemPeer(list, 0), bridge.FocusTarget());

        // In the focused list the focus moves with the selection.
        var changes = Observe(bridge, list, listPeer, () => list.SelectIndex(1));
        var bravo = bridge.GetOrCreateItemPeer(list, 1);
        Assert.Contains(changes, change => !change.IsProperty && change.Id == UiaNative.UiaSelectionItem_ElementSelectedEventId && ReferenceEquals(change.Target, bravo));
        Assert.Contains(changes, change => !change.IsProperty && change.Id == UiaNative.UiaAutomationFocusChangedEventId && ReferenceEquals(change.Target, bravo));

        // Focus that goes nowhere is on the window, not on the element that lost it.
        session.SetFocus(null);
        Assert.Same(bridge, bridge.FocusTarget());
    }

    [Fact]
    public void AListReachedOnlyThroughTheFocusStillReportsItsSelectionMoving()
    {
        var (session, bridge, root) = Create();
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo")]);
        root.AddChild(list);
        list.SelectIndex(0);
        var raised = Listen(bridge);

        // A focus-tracking client, such as Magnifier, learns of the list only from the focus event on its row.
        session.SetFocus(list);
        Assert.Contains((bridge.GetOrCreateItemPeer(list, 0), UiaNative.UiaAutomationFocusChangedEventId), raised);

        list.SelectIndex(1);
        var bravo = bridge.GetOrCreateItemPeer(list, 1);
        Assert.Contains((bravo, UiaNative.UiaSelectionItem_ElementSelectedEventId), raised);
        Assert.Contains((bravo, UiaNative.UiaAutomationFocusChangedEventId), raised);
    }

    // Pins "a client listens", which UIA answers for the whole machine, and collects the events raised.
    private static List<(IRawElementProviderSimple Target, int EventId)> Listen(WindowsAutomationBridge bridge)
    {
        var raised = new List<(IRawElementProviderSimple, int)>();
        bridge.ClientsListening = () => true;
        bridge.EventRaised += (target, eventId) => raised.Add((target, eventId));
        return raised;
    }

    // Whether a UIA client is listening is machine-wide; collect what the bridge raised and what is still pending.
    private static List<WindowsAutomationBridge.AutomationChange> Observe(
        WindowsAutomationBridge bridge, UiElement element, WindowsElementAutomationPeer peer, Action change)
    {
        var observed = new List<WindowsAutomationBridge.AutomationChange>();
        void Collect(IReadOnlyList<WindowsAutomationBridge.AutomationChange> raised) => observed.AddRange(raised);
        bridge.ChangesRaised += Collect;
        try { change(); }
        finally { bridge.ChangesRaised -= Collect; }
        observed.AddRange(bridge.DetectChanges(element, peer));
        return observed;
    }

    private static (UiSession Session, WindowsAutomationBridge Bridge, StandardPanel Root) Create()
    {
        var session = new StandardUiSessionBuilder().WithDispatcher(new ImmediateUiDispatcher()).Build(new Host());
        var root = new StandardPanel();
        root.Arrange(new BRect(0, 0, 800, 600));
        session.AddRoot(root);
        return (session, new WindowsAutomationBridge(nint.Zero, session, root), root);
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
