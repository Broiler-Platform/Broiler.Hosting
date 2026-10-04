using System;
using System.Linq;
using System.Runtime.InteropServices;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.Native.Windows;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.ComboBox;
using Broiler.UI.ComboBox.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.ScrollView.Standard;
using Broiler.UI.Standard;
using Broiler.UI.TabView.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>Bounding rectangles, offscreen state and hit testing from the geometry Broiler.UI reports.</summary>
public sealed class AutomationGeometryTests
{
    [Fact]
    public void ElementsInAScrollViewAreClippedToItsViewportAndOffscreenOnceScrolledOut()
    {
        var (_, bridge, root) = Create();
        var scroll = new StandardScrollView();
        var content = new StandardPanel();
        var topEdit = new StandardEdit { Text = "top" };
        var fields = Enumerable.Range(0, 20).Select(i => new StandardButton { Text = $"Field {i}" }).ToList();
        var bottomEdit = new StandardEdit { Text = "bottom" };
        content.AddChild(topEdit);
        foreach (var field in fields) content.AddChild(field);
        content.AddChild(bottomEdit);
        scroll.AddChild(content);
        root.AddChild(scroll);
        Layout(scroll, new BRect(10, 20, 300, 100));
        scroll.SetOffset(new BPoint(0, 10));
        Layout(scroll, new BRect(10, 20, 300, 100));

        int clipped = 0, hidden = 0;
        foreach (var field in fields)
        {
            var peer = bridge.GetOrCreatePeer(field);
            BRect expected = field.Bounds.Intersect(scroll.ContentBounds);
            if (expected.IsEmpty)
            {
                hidden++;
                Assert.Equal(true, peer.GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
                Assert.Equal(Rect(BRect.Empty), Rect(peer.BoundingRectangle));
            }
            else
            {
                if (expected != field.Bounds) clipped++;
                Assert.Equal(false, peer.GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
                AssertRect(expected, peer.BoundingRectangle);
            }
        }

        Assert.True(clipped > 0, "No field was partly scrolled out of view.");
        Assert.True(hidden > 0, "No field was scrolled entirely out of view.");
        // The rectangle is a property as well.
        var lastField = bridge.GetOrCreatePeer(fields[^1]);
        Assert.Equal(Rect(BRect.Empty), Rect((UiaRect)lastField.GetPropertyValue(UiaNative.UiaBoundingRectanglePropertyId)!));

        // A text range has the visible part of its field as its one rectangle, and none once scrolled out.
        Assert.Equal(4, RangeRectangleValues(bridge.GetOrCreatePeer(topEdit)));
        Assert.Equal(0, RangeRectangleValues(bridge.GetOrCreatePeer(bottomEdit)));
    }

    private static int RangeRectangleValues(WindowsElementAutomationPeer edit)
    {
        var range = ((INativeText)NativeProviderAdapter.For(edit)!).GetDocumentRange()!;
        nint array = range.GetBoundingRectangles();
        try
        {
            Assert.Equal(0, SafeArrayGetLBound(array, 1, out int lower));
            Assert.Equal(0, SafeArrayGetUBound(array, 1, out int upper));
            return upper - lower + 1;
        }
        finally { OleAutNative.SafeArrayDestroy(array); }
    }

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint array, uint dimension, out int bound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint array, uint dimension, out int bound);

    [Fact]
    public void ElementsAreClippedToTheWindowSurface()
    {
        var (_, bridge, root) = Create();
        var wide = new StandardButton { Text = "Wide" };
        var outside = new StandardButton { Text = "Outside" };
        root.AddChild(wide);
        root.AddChild(outside);
        wide.Arrange(new BRect(700, 10, 300, 30));
        outside.Arrange(new BRect(900, 10, 100, 30));

        Assert.Equal(Rect(new BRect(700, 10, 100, 30)), Rect(bridge.GetOrCreatePeer(wide).BoundingRectangle));
        Assert.Equal(true, bridge.GetOrCreatePeer(outside).GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
    }

    [Fact]
    public void ListItemsUseThePresentersNameAndTheListContentArea()
    {
        var (_, bridge, root) = Create();
        var list = new StandardListView { ItemPresenter = new MailRowPresenter() };
        list.SetItems(Enumerable.Range(0, 10).Select(i => new UiListItem($"m{i}", $"Subject {i}") { SecondaryText = $"sender{i}@example.test" }));
        root.AddChild(list);
        Layout(list, new BRect(0, 0, 300, 100));

        var first = bridge.GetOrCreateItemPeer(list, 0);
        Assert.Equal("From: sender0@example.test, Subject: Subject 0, Received: Sunday, 4 October 2026 09:15", first.GetPropertyValue(UiaNative.UiaNamePropertyId));
        // The name already carries the secondary text, so it is not read a second time as help.
        Assert.Equal("", first.GetPropertyValue(UiaNative.UiaHelpTextPropertyId));

        int partial = 0, hidden = 0;
        for (int index = 0; index < list.Items.Count; index++)
        {
            var peer = bridge.GetOrCreateItemPeer(list, index);
            UiSemanticNode node = list.GetItemSemanticNode(index)!;
            if (node.State.HasFlag(UiSemanticState.Offscreen))
            {
                hidden++;
                Assert.Equal(true, peer.GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
                Assert.Equal(Rect(BRect.Empty), Rect(peer.BoundingRectangle));
                continue;
            }

            Assert.Equal(false, peer.GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
            AssertRect(node.Bounds, peer.BoundingRectangle);
            Assert.Equal(node.Bounds, node.Bounds.Intersect(list.ContentBounds));
            if (node.Bounds.Height < MailRowPresenter.RowHeight) partial++;
        }

        Assert.Equal(1, partial);
        Assert.True(hidden > 0);
    }

    [Fact]
    public void TabItemsAreTheirHeadersNotEqualSharesOfTheStrip()
    {
        var (_, bridge, root) = Create();
        var tabs = new StandardTabView();
        tabs.AddTab("inbox", "Inbox");
        tabs.AddTab("compose", "Compose");
        tabs.AddTab("account", "Account settings");
        root.AddChild(tabs);
        Layout(tabs, new BRect(0, 0, 780, 400));

        for (int index = 0; index < tabs.Tabs.Count; index++)
        {
            BRect header = tabs.GetTabHeaderBounds(index);
            var peer = bridge.GetOrCreateTabPeer(tabs, index);
            AssertRect(header, peer.BoundingRectangle);
            Assert.Equal(tabs.EffectiveHeaderHeight, peer.BoundingRectangle.Height, 6);
            Assert.True(header.Width < 780 / 3.0);
            // A point on the header finds that tab.
            Assert.Same(peer, bridge.ElementProviderFromPoint(header.Left + (header.Width / 2), header.Top + (header.Height / 2)));
        }

        // Beyond the last header the strip belongs to the tab view itself.
        Assert.Same(bridge.GetOrCreatePeer(tabs), bridge.ElementProviderFromPoint(760, 10));
    }

    [Fact]
    public void HitTestingFindsAnOpenDropDownOverTheFieldBelowIt()
    {
        var (_, bridge, root) = Create();
        var combo = new StandardComboBox();
        combo.SetItems([new UiComboBoxItem("a", "Alpha"), new UiComboBoxItem("b", "Bravo"), new UiComboBoxItem("c", "Charlie")]);
        var below = new StandardEdit { PlaceholderText = "Below" };
        root.AddChild(below);
        root.AddChild(combo);
        Layout(combo, new BRect(0, 0, 200, 32));
        Layout(below, new BRect(0, 40, 200, 32));

        Assert.Same(bridge.GetOrCreatePeer(below), bridge.ElementProviderFromPoint(10, 50));
        combo.OpenDropDown();
        Assert.True(combo.OverlayBounds.Contains(new BPoint(10, 50)));
        Assert.Same(bridge.GetOrCreatePeer(combo), bridge.ElementProviderFromPoint(10, 50));
    }

    [Fact]
    public void ListItemHitTestingUsesTheReportedRows()
    {
        var (_, bridge, root) = Create();
        var list = new StandardListView { ItemPresenter = new MailRowPresenter() };
        list.SetItems(Enumerable.Range(0, 10).Select(i => new UiListItem($"m{i}", $"Subject {i}")));
        root.AddChild(list);
        Layout(list, new BRect(0, 20, 300, 100));
        list.ScrollIntoView("m5");

        for (int index = 0; index < list.Items.Count; index++)
        {
            UiSemanticNode node = list.GetItemSemanticNode(index)!;
            if (node.State.HasFlag(UiSemanticState.Offscreen)) continue;
            BPoint center = new(node.Bounds.Left + 5, node.Bounds.Top + (node.Bounds.Height / 2));
            Assert.Same(bridge.GetOrCreateItemPeer(list, index), bridge.ElementProviderFromPoint(center.X, center.Y));
        }
    }

    private static (double, double, double, double) Rect(BRect rect) => (rect.X, rect.Y, rect.Width, rect.Height);

    private static void AssertRect(BRect expected, UiaRect actual)
    {
        Assert.Equal(expected.X, actual.Left, 6);
        Assert.Equal(expected.Y, actual.Top, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    private static (double, double, double, double) Rect(UiaRect rect) => (rect.Left, rect.Top, rect.Width, rect.Height);

    private static void Layout(UiElement element, BRect bounds)
    {
        element.Measure(bounds.Size);
        element.Arrange(bounds);
    }

    private static (UiSession Session, WindowsAutomationBridge Bridge, StandardPanel Root) Create()
    {
        var session = new StandardUiSessionBuilder().WithDispatcher(new ImmediateUiDispatcher()).Build(new Host());
        var root = new StandardPanel();
        root.Arrange(new BRect(0, 0, 800, 600));
        session.AddRoot(root);
        return (session, new WindowsAutomationBridge(nint.Zero, session, root), root);
    }

    /// <summary>Names a row as Broiler.Mail's message presenter does, with the full received date.</summary>
    private sealed class MailRowPresenter : IUiListItemPresenter
    {
        public const double RowHeight = 28;

        public double GetItemHeight(UiListItem? item, UiDensity density, double availableWidth) => RowHeight;

        public void Render(UiListItemRenderContext context) { }

        public UiSemanticNode CreateSemanticNode(UiListItemSemanticContext context) => new(
            UiSemanticRole.ListItem,
            $"From: {context.Item.SecondaryText}, Subject: {context.Item.Text}, Received: Sunday, 4 October 2026 09:15",
            context.Bounds,
            UiSemanticState.Visible | UiSemanticState.Enabled,
            []);
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
