using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.ComboBox;
using Broiler.UI.ComboBox.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;
using Broiler.UI.TabView.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

public sealed class WindowsAutomationBridgeTests
{
    private sealed class HeadlessUiHost : IUiHost
    {
        public BSize ViewportSize => new(800, 600);
        public double Scale => 1.0;
        public BRenderList CreateRenderList(int capacity = 0) => new();
        public void Invalidate(UiInvalidation invalidation) { }
        public void Present(BRenderList renderList) { }
    }

    private static (UiSession Session, WindowsAutomationBridge Bridge, StandardPanel Root) CreateTestEnvironment()
    {
        var host = new HeadlessUiHost();
        var dispatcher = new ImmediateUiDispatcher();
        var session = new StandardUiSessionBuilder().WithDispatcher(dispatcher).Build(host);
        var root = new StandardPanel();
        root.Arrange(new BRect(0, 0, 800, 600));
        session.AddRoot(root);
        var bridge = new WindowsAutomationBridge(nint.Zero, session, root);
        return (session, bridge, root);
    }

    private static List<IRawElementProviderFragment> FlattenTree(IRawElementProviderFragment root)
    {
        var list = new List<IRawElementProviderFragment>();
        void Walk(IRawElementProviderFragment node)
        {
            list.Add(node);
            for (var child = node.Navigate(NavigateDirection.FirstChild); child != null; child = child.Navigate(NavigateDirection.NextSibling))
            {
                Walk(child);
            }
        }
        Walk(root);
        return list;
    }

    [Fact]
    public void TreeNavigation_WalksChildrenAndSiblingsAcrossControls()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var sendButton = new StandardButton { Text = "Send" };
        sendButton.Arrange(new BRect(10, 10, 100, 30));
        var toEdit = new StandardEdit { PlaceholderText = "To" };
        toEdit.Arrange(new BRect(10, 50, 300, 30));
        var listView = new StandardListView();
        listView.Arrange(new BRect(10, 90, 400, 200));
        listView.SetItems([
            new UiListItem("msg1", "Welcome to Broiler.Hosting"),
            new UiListItem("msg2", "Important Update")
        ]);

        root.AddChild(sendButton);
        root.AddChild(toEdit);
        root.AddChild(listView);

        // 1. Root to First Child
        var firstChild = bridge.Navigate(NavigateDirection.FirstChild);
        Assert.NotNull(firstChild);
        Assert.Equal("Send", firstChild.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Equal(UiaNative.UiaButtonControlTypeId, firstChild.GetPropertyValue(UiaNative.UiaControlTypePropertyId));

        // 2. Next Sibling -> To edit
        var secondChild = firstChild.Navigate(NavigateDirection.NextSibling);
        Assert.NotNull(secondChild);
        Assert.Equal("To", secondChild.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Equal(UiaNative.UiaEditControlTypeId, secondChild.GetPropertyValue(UiaNative.UiaControlTypePropertyId));

        // 3. Next Sibling -> ListView
        var thirdChild = secondChild.Navigate(NavigateDirection.NextSibling);
        Assert.NotNull(thirdChild);
        Assert.Equal(UiaNative.UiaListControlTypeId, thirdChild.GetPropertyValue(UiaNative.UiaControlTypePropertyId));

        // 4. ListView First Child -> First virtual list item
        var firstItem = thirdChild.Navigate(NavigateDirection.FirstChild);
        Assert.NotNull(firstItem);
        Assert.Equal("Welcome to Broiler.Hosting", firstItem.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Equal(UiaNative.UiaListItemControlTypeId, firstItem.GetPropertyValue(UiaNative.UiaControlTypePropertyId));

        // 5. Item Next Sibling -> Second list item
        var secondItem = firstItem.Navigate(NavigateDirection.NextSibling);
        Assert.NotNull(secondItem);
        Assert.Equal("Important Update", secondItem.GetPropertyValue(UiaNative.UiaNamePropertyId));

        // 6. Navigation backwards
        var prevItem = secondItem.Navigate(NavigateDirection.PreviousSibling);
        Assert.Same(firstItem, prevItem);

        var prevChild = thirdChild.Navigate(NavigateDirection.PreviousSibling);
        Assert.Same(secondChild, prevChild);

        // 7. Parent navigation
        Assert.Same(thirdChild, firstItem.Navigate(NavigateDirection.Parent));
        Assert.Same(bridge, firstChild.Navigate(NavigateDirection.Parent));
    }

    [Fact]
    public void LocateControlsByNameAndRole_WithoutCoordinates()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var tabView = new StandardTabView();
        tabView.Arrange(new BRect(0, 0, 600, 400));
        var inboxPanel = new StandardPanel();
        var composePanel = new StandardPanel();

        var olderButton = new StandardButton { Text = "Older" };
        var readButton = new StandardButton { Text = "Read" };
        inboxPanel.AddChild(olderButton);
        inboxPanel.AddChild(readButton);

        var toEdit = new StandardEdit { PlaceholderText = "To" };
        var sendButton = new StandardButton { Text = "Send" };
        composePanel.AddChild(toEdit);
        composePanel.AddChild(sendButton);

        tabView.AddTab("inbox", "Inbox", inboxPanel);
        tabView.AddTab("compose", "Compose", composePanel);
        root.AddChild(tabView);

        var allNodes = FlattenTree(bridge);

        // Locate "Inbox" tab
        var inboxTab = allNodes.FirstOrDefault(n =>
            (int?)n.GetPropertyValue(UiaNative.UiaControlTypePropertyId) == UiaNative.UiaTabItemControlTypeId &&
            (string?)n.GetPropertyValue(UiaNative.UiaNamePropertyId) == "Inbox");
        Assert.NotNull(inboxTab);

        // The unselected Compose tab's content is not exposed; selecting the tab exposes it.
        Assert.DoesNotContain(allNodes, n => (string?)n.GetPropertyValue(UiaNative.UiaNamePropertyId) == "Send");
        tabView.SelectTab("compose");
        allNodes = FlattenTree(bridge);
        Assert.DoesNotContain(allNodes, n => (string?)n.GetPropertyValue(UiaNative.UiaNamePropertyId) == "Older");

        // Locate "To" field
        var toNode = allNodes.FirstOrDefault(n =>
            (int?)n.GetPropertyValue(UiaNative.UiaControlTypePropertyId) == UiaNative.UiaEditControlTypeId &&
            (string?)n.GetPropertyValue(UiaNative.UiaNamePropertyId) == "To");
        Assert.NotNull(toNode);

        // Locate "Send" button
        var sendNode = allNodes.FirstOrDefault(n =>
            (int?)n.GetPropertyValue(UiaNative.UiaControlTypePropertyId) == UiaNative.UiaButtonControlTypeId &&
            (string?)n.GetPropertyValue(UiaNative.UiaNamePropertyId) == "Send");
        Assert.NotNull(sendNode);
    }

    [Fact]
    public void Button_InvokePattern_ExecutesClick()
    {
        var (session, bridge, root) = CreateTestEnvironment();
        bool clicked = false;

        var button = new StandardButton { Text = "Send" };
        button.Clicked += (_, _) => clicked = true;
        root.AddChild(button);

        var peer = bridge.GetOrCreatePeer(button);
        var invoke = peer.GetPatternProvider(UiaNative.UiaInvokePatternId) as IInvokeProvider;
        Assert.NotNull(invoke);

        invoke.Invoke();
        Assert.True(clicked);
    }

    [Fact]
    public void ValuePattern_StandardEdit_ReadWrite()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var edit = new StandardEdit { Text = "initial@example.com" };
        root.AddChild(edit);

        var peer = bridge.GetOrCreatePeer(edit);
        var valueProvider = peer.GetPatternProvider(UiaNative.UiaValuePatternId) as IValueProvider;
        Assert.NotNull(valueProvider);

        Assert.Equal("initial@example.com", valueProvider.Value);
        Assert.False(valueProvider.IsReadOnly);

        valueProvider.SetValue("updated@example.com");
        Assert.Equal("updated@example.com", edit.Text);
        Assert.Equal("updated@example.com", valueProvider.Value);
    }

    [Fact]
    public void ValuePattern_PasswordProtection_NeverExposesPlainText()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var passwordEdit = new StandardEdit
        {
            PlaceholderText = "Password / app password",
            IsPassword = true,
            Text = "SuperSecret123!"
        };
        root.AddChild(passwordEdit);

        var peer = bridge.GetOrCreatePeer(passwordEdit);
        var valueProvider = peer.GetPatternProvider(UiaNative.UiaValuePatternId) as IValueProvider;
        Assert.NotNull(valueProvider);

        // Crucial security invariant: Password fields MUST NOT expose plain text through UIA ValuePattern!
        Assert.Equal(string.Empty, valueProvider.Value);
        Assert.True((bool?)peer.GetPropertyValue(UiaNative.UiaIsPasswordPropertyId));
    }

    [Fact]
    public void ValuePattern_StandardRichEdit_GetAndSetPlainText()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var richEdit = new StandardRichEdit();
        richEdit.SetPlainText("Line 1\nLine 2");
        root.AddChild(richEdit);

        var peer = bridge.GetOrCreatePeer(richEdit);
        var valueProvider = peer.GetPatternProvider(UiaNative.UiaValuePatternId) as IValueProvider;
        Assert.NotNull(valueProvider);

        Assert.Equal("Line 1\nLine 2", valueProvider.Value);
        Assert.False(valueProvider.IsReadOnly);

        valueProvider.SetValue("Replaced text message body");
        Assert.Equal("Replaced text message body", richEdit.GetPlainText());
    }

    [Fact]
    public void ListView_SelectionAndScrollItemPatterns()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var listView = new StandardListView();
        listView.Arrange(new BRect(0, 0, 300, 100));
        listView.SetItems([
            new UiListItem("msg1", "Subject 1"),
            new UiListItem("msg2", "Subject 2"),
            new UiListItem("msg3", "Subject 3")
        ]);
        root.AddChild(listView);

        // List selection provider
        var listPeer = bridge.GetOrCreatePeer(listView);
        var selectionProvider = listPeer.GetPatternProvider(UiaNative.UiaSelectionPatternId) as ISelectionProvider;
        Assert.NotNull(selectionProvider);
        Assert.Empty(selectionProvider.GetSelection() ?? []);

        // Item 1 SelectionItem pattern
        var item1Peer = bridge.GetOrCreateItemPeer(listView, 1);
        var selItem1 = item1Peer.GetPatternProvider(UiaNative.UiaSelectionItemPatternId) as ISelectionItemProvider;
        Assert.NotNull(selItem1);
        Assert.False(selItem1.IsSelected);

        selItem1.Select();
        Assert.True(selItem1.IsSelected);
        Assert.Equal("msg2", listView.SelectedItemId);

        var activeSelection = selectionProvider.GetSelection();
        Assert.NotNull(activeSelection);
        Assert.Single(activeSelection);
        Assert.Same(item1Peer, activeSelection[0]);

        // Item ScrollItem pattern
        var scrollItem = item1Peer.GetPatternProvider(UiaNative.UiaScrollItemPatternId) as IScrollItemProvider;
        Assert.NotNull(scrollItem);
        scrollItem.ScrollIntoView();
    }

    [Fact]
    public void TabView_TabItemSelection_SwitchesTab()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var tabView = new StandardTabView();
        tabView.Arrange(new BRect(0, 0, 400, 300));
        tabView.AddTab("tab1", "Inbox");
        tabView.AddTab("tab2", "Account");
        root.AddChild(tabView);

        Assert.Equal(0, tabView.SelectedIndex);

        var tab2Peer = bridge.GetOrCreateTabPeer(tabView, 1);
        var selItem = tab2Peer.GetPatternProvider(UiaNative.UiaSelectionItemPatternId) as ISelectionItemProvider;
        Assert.NotNull(selItem);
        Assert.False(selItem.IsSelected);

        selItem.Select();
        Assert.True(selItem.IsSelected);
        Assert.Equal(1, tabView.SelectedIndex);
        Assert.Equal("tab2", tabView.SelectedTab?.Id);
    }

    [Fact]
    public void ComboBox_ExpandCollapsePattern()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var comboBox = new StandardComboBox();
        comboBox.SetItems([
            new UiComboBoxItem("item1", "Item 1"),
            new UiComboBoxItem("item2", "Item 2")
        ]);
        root.AddChild(comboBox);

        var peer = bridge.GetOrCreatePeer(comboBox);
        var expandCollapse = peer.GetPatternProvider(UiaNative.UiaExpandCollapsePatternId) as IExpandCollapseProvider;
        Assert.NotNull(expandCollapse);

        Assert.Equal(ExpandCollapseState.Collapsed, expandCollapse.ExpandCollapseState);

        expandCollapse.Expand();
        Assert.True(comboBox.IsDropDownOpen);
        Assert.Equal(ExpandCollapseState.Expanded, expandCollapse.ExpandCollapseState);

        expandCollapse.Collapse();
        Assert.False(comboBox.IsDropDownOpen);
        Assert.Equal(ExpandCollapseState.Collapsed, expandCollapse.ExpandCollapseState);
    }

    [Fact]
    public void ElementProviderFromPoint_HitTestsElementsAndListItems()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var button = new StandardButton { Text = "Button" };
        button.Arrange(new BRect(10, 10, 100, 40));
        var listView = new StandardListView();
        listView.Arrange(new BRect(10, 60, 200, 150));
        listView.SetItems([
            new UiListItem("row0", "Row 0"),
            new UiListItem("row1", "Row 1"),
            new UiListItem("row2", "Row 2")
        ]);

        root.AddChild(button);
        root.AddChild(listView);

        // Hit test button
        var hitButton = bridge.ElementProviderFromPoint(20, 20);
        Assert.NotNull(hitButton);
        Assert.Equal("Button", hitButton.GetPropertyValue(UiaNative.UiaNamePropertyId));

        // Hit test list item row 1 (height is ~28, row 1 starts at 60 + 28 = 88)
        double itemHeight = WindowsElementAutomationPeer.GetItemHeight(listView);
        double targetY = 60 + (itemHeight * 1.5);
        var hitRow1 = bridge.ElementProviderFromPoint(20, targetY);
        Assert.NotNull(hitRow1);
        Assert.Equal("Row 1", hitRow1.GetPropertyValue(UiaNative.UiaNamePropertyId));
    }

    [Fact]
    public void FocusManagement_GetFocusReturnsFocusedElementOrItem()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var edit = new StandardEdit();
        edit.Arrange(new BRect(10, 10, 100, 30));
        var listView = new StandardListView();
        listView.Arrange(new BRect(10, 50, 100, 100));
        listView.SetItems([new UiListItem("i1", "Item 1"), new UiListItem("i2", "Item 2")]);

        root.AddChild(edit);
        root.AddChild(listView);

        // Focus edit
        session.SetFocus(edit);
        var focusedPeer = bridge.GetFocus();
        Assert.NotNull(focusedPeer);
        Assert.Equal((int)edit.SemanticId, focusedPeer.GetRuntimeId()?[2]);

        // Focus list view and select item 1
        listView.SelectIndex(1);
        session.SetFocus(listView);
        var focusedItemPeer = bridge.GetFocus();
        Assert.NotNull(focusedItemPeer);
        Assert.Equal("Item 2", focusedItemPeer.GetPropertyValue(UiaNative.UiaNamePropertyId));
    }

    [Fact]
    public void DeadPeerCleanup_DoesNotThrowOrLeakOnTreeMutation()
    {
        var (session, bridge, root) = CreateTestEnvironment();

        var button = new StandardButton { Text = "Temporary" };
        root.AddChild(button);

        var peer = bridge.GetOrCreatePeer(button);
        Assert.True(peer.IsAlive);

        // Dispose / remove button
        button.Dispose();
        Assert.False(peer.IsAlive);

        // Simulate status announcement
        session.AnnounceStatus(root, "Cleaned");
        root.Invalidate(UiInvalidationKind.Semantic);

        // Disposing bridge cleans up completely
        bridge.Dispose();
    }
}
