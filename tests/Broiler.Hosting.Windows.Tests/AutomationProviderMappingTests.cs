using System;
using System.Linq;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.Label.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;
using Broiler.UI.TabView.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>Names, values, layout nodes, hidden tab content, text ranges, and change detection in the UIA provider.</summary>
public sealed class AutomationProviderMappingTests
{
    [Fact]
    public void UnnamedLayoutContainersAreNotControlElementsAndHaveNoClassNameAsName()
    {
        var (_, bridge, root) = Create();
        var panel = new StandardPanel();
        var button = new StandardButton { Text = "Send" };
        panel.AddChild(button);
        root.AddChild(panel);

        var panelPeer = bridge.GetOrCreatePeer(panel);
        Assert.Equal("", panelPeer.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Equal(false, panelPeer.GetPropertyValue(UiaNative.UiaIsControlElementPropertyId));
        Assert.Equal(false, panelPeer.GetPropertyValue(UiaNative.UiaIsContentElementPropertyId));
        Assert.Equal("StandardPanel", panelPeer.GetPropertyValue(UiaNative.UiaClassNamePropertyId));
        Assert.Equal(true, bridge.GetOrCreatePeer(button).GetPropertyValue(UiaNative.UiaIsControlElementPropertyId));
    }

    [Fact]
    public void LabelledEditIsNamedByItsLabelWithTheTextAsValueAndThePlaceholderAsHelp()
    {
        var (_, bridge, root) = Create();
        var edit = new StandardEdit { Text = "reader@example.test", PlaceholderText = "name@example.com" };
        var label = new StandardLabel { Text = "Email address", Target = edit };
        root.AddChild(label);
        root.AddChild(edit);

        var peer = bridge.GetOrCreatePeer(edit);
        Assert.Equal("Email address", peer.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Same(bridge.GetOrCreatePeer(label), peer.GetPropertyValue(UiaNative.UiaLabeledByPropertyId));
        Assert.Equal("name@example.com", peer.GetPropertyValue(UiaNative.UiaHelpTextPropertyId));
        Assert.Equal("reader@example.test", peer.Value);
    }

    [Fact]
    public void UnlabelledEditIsNeverNamedByItsText()
    {
        var (_, bridge, root) = Create();
        var withHint = new StandardEdit { Text = "typed text", PlaceholderText = "Search mail" };
        var bare = new StandardEdit { Text = "typed text" };
        root.AddChild(withHint);
        root.AddChild(bare);

        var hinted = bridge.GetOrCreatePeer(withHint);
        Assert.Equal("Search mail", hinted.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Equal("", hinted.GetPropertyValue(UiaNative.UiaHelpTextPropertyId));
        Assert.Equal("", bridge.GetOrCreatePeer(bare).GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Null(bridge.GetOrCreatePeer(bare).GetPropertyValue(UiaNative.UiaLabeledByPropertyId));
    }

    [Fact]
    public void PasswordFieldIsLabelledButExposesNoTextPatternOrValue()
    {
        var (_, bridge, root) = Create();
        var password = new StandardEdit { IsPassword = true, Text = "synthetic-secret" };
        root.AddChild(new StandardLabel { Text = "Password / app password", Target = password });
        root.AddChild(password);

        var peer = bridge.GetOrCreatePeer(password);
        Assert.Equal("Password / app password", peer.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Null(peer.GetPatternProvider(UiaNative.UiaTextPatternId));
        Assert.Equal("", peer.Value);
        Assert.Null(peer.TextValue);
        Assert.Equal(true, peer.GetPropertyValue(UiaNative.UiaIsPasswordPropertyId));
    }

    [Fact]
    public void UnselectedTabContentIsOffscreenAndCannotBeHit()
    {
        var (session, bridge, root) = Create();
        var tabs = new StandardTabView();
        var inbox = new StandardButton { Text = "Receive" };
        var compose = new StandardButton { Text = "Send" };
        tabs.AddTab("inbox", "Inbox", inbox);
        tabs.AddTab("compose", "Compose", compose);
        root.AddChild(tabs);
        session.RenderFrame();

        Assert.Equal(false, bridge.GetOrCreatePeer(inbox).GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
        Assert.Equal(true, bridge.GetOrCreatePeer(compose).GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
        // The last tab item is followed by the selected content only.
        var lastTab = bridge.GetOrCreateTabPeer(tabs, 1);
        Assert.Same(bridge.GetOrCreatePeer(inbox), lastTab.Navigate(NavigateDirection.NextSibling));
        Assert.Null(bridge.GetOrCreatePeer(inbox).Navigate(NavigateDirection.NextSibling));

        tabs.SelectTab("compose");
        Assert.Equal(true, bridge.GetOrCreatePeer(inbox).GetPropertyValue(UiaNative.UiaIsOffscreenPropertyId));
        Assert.Same(bridge.GetOrCreatePeer(compose), lastTab.Navigate(NavigateDirection.NextSibling));
    }

    [Fact]
    public void TextPatternReadsAndSelectsByUnitWithoutExposingTheWholeTextAsName()
    {
        var (_, bridge, root) = Create();
        var reader = new StandardRichEdit { IsReadOnly = true };
        reader.SetPlainText("Hello world\nSecond line 📬 end");
        root.AddChild(reader);
        var peer = bridge.GetOrCreatePeer(reader);

        Assert.Same(peer, peer.GetPatternProvider(UiaNative.UiaTextPatternId));
        WindowsTextRange document = peer.DocumentRange;
        Assert.Equal("Hello world\nSecond line 📬 end", document.GetText(-1));
        Assert.Equal("Hello", document.GetText(5));

        WindowsTextRange range = document.Clone();
        range.Move(TextUnit.Character, 0);
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        Assert.True(range.IsDegenerate);
        range.ExpandToEnclosingUnit(TextUnit.Word);
        Assert.Equal("Hello ", range.GetText(-1));
        Assert.Equal(1, range.Move(TextUnit.Word, 1));
        Assert.Equal("world\n", range.GetText(-1));

        range.ExpandToEnclosingUnit(TextUnit.Line);
        Assert.Equal(1, range.Move(TextUnit.Line, 1));
        Assert.Equal("Second line 📬 end", range.GetText(-1));
        // A non-degenerate range cannot move past the last unit.
        Assert.Equal(0, range.Move(TextUnit.Line, 1));

        WindowsTextRange? found = document.FindText("SECOND", backward: false, ignoreCase: true);
        Assert.NotNull(found);
        Assert.Equal("Second", found!.GetText(-1));
        Assert.Null(document.FindText("SECOND", backward: false, ignoreCase: false));

        // A surrogate pair is one character.
        WindowsTextRange emoji = document.FindText("📬", false, false)!;
        emoji.MoveEndpointByRange(TextPatternRangeEndpoint.End, emoji, TextPatternRangeEndpoint.Start);
        emoji.ExpandToEnclosingUnit(TextUnit.Character);
        Assert.Equal("📬", emoji.GetText(-1));

        found.Select();
        WindowsTextRange selection = Assert.Single(peer.GetTextSelection());
        Assert.Equal("Second", selection.GetText(-1));
        Assert.True(selection.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) > 0);
        // Ranges of different elements cannot be compared.
        Assert.Throws<ArgumentException>(() => selection.CompareEndpoints(TextPatternRangeEndpoint.Start, OtherDocument(bridge, root), TextPatternRangeEndpoint.Start));
    }

    [Fact]
    public void RangesSurviveTextThatShrinks()
    {
        var (_, bridge, root) = Create();
        var edit = new StandardEdit { Text = "A longer value" };
        root.AddChild(edit);
        var peer = bridge.GetOrCreatePeer(edit);
        WindowsTextRange range = peer.DocumentRange;
        edit.Text = "Short";
        Assert.Equal("Short", range.GetText(-1));
        Assert.Equal(5, range.End);
    }

    [Fact]
    public void ChangeDetectionReportsOnlyRealDifferences()
    {
        var (_, bridge, root) = Create();
        var edit = new StandardEdit { Text = "first" };
        root.AddChild(new StandardLabel { Text = "Subject", Target = edit });
        root.AddChild(edit);
        var peer = bridge.GetOrCreatePeer(edit);

        Assert.Empty(bridge.DetectChanges(edit, peer));

        var changes = Observe(bridge, edit, peer, () => edit.Text = "second");
        Assert.Contains(changes, change => change.IsProperty && change.Id == UiaNative.UiaValueValuePropertyId && (string?)change.NewValue == "second");
        Assert.Contains(changes, change => !change.IsProperty && change.Id == UiaNative.UiaText_TextChangedEventId);
        Assert.DoesNotContain(changes, change => change.Id == UiaNative.UiaIsEnabledPropertyId);
        Assert.DoesNotContain(changes, change => change.Id == UiaNative.UiaNamePropertyId);
        Assert.Empty(bridge.DetectChanges(edit, peer));

        Assert.Contains(Observe(bridge, edit, peer, () => edit.SetSelection(0, 3)), change => change.Id == UiaNative.UiaText_TextSelectionChangedEventId);
        Assert.Contains(Observe(bridge, edit, peer, () => edit.IsEnabled = false), change => change.Id == UiaNative.UiaIsEnabledPropertyId && Equals(change.NewValue, false));
    }

    [Fact]
    public void PasswordTypingRaisesNoValueOrTextEvents()
    {
        var (_, bridge, root) = Create();
        var password = new StandardEdit { IsPassword = true };
        root.AddChild(password);
        var peer = bridge.GetOrCreatePeer(password);
        Assert.DoesNotContain(Observe(bridge, password, peer, () => password.Text = "synthetic-secret"), change =>
            change.Id is UiaNative.UiaValueValuePropertyId or UiaNative.UiaText_TextChangedEventId or UiaNative.UiaText_TextSelectionChangedEventId);
    }

    [Fact]
    public void ListSelectionChangeSelectsTheItemPeer()
    {
        var (_, bridge, root) = Create();
        var list = new Broiler.UI.ListView.Standard.StandardListView();
        list.SetItems(Enumerable.Range(0, 3).Select(i => new Broiler.UI.ListView.UiListItem($"m{i}", $"Item {i}")));
        root.AddChild(list);
        var peer = bridge.GetOrCreatePeer(list);
        var change = Assert.Single(Observe(bridge, list, peer, () => list.SelectIndex(2)), c => c.Id == UiaNative.UiaSelectionItem_ElementSelectedEventId);
        Assert.Same(bridge.GetOrCreateItemPeer(list, 2), change.Target);
    }

    // Whether a UIA client is listening is machine-wide. When one is, the bridge raises (and consumes)
    // the changes itself; otherwise they are still pending. Collect them from both paths.
    private static System.Collections.Generic.List<WindowsAutomationBridge.AutomationChange> Observe(
        WindowsAutomationBridge bridge, UiElement element, WindowsElementAutomationPeer peer, Action change)
    {
        var observed = new System.Collections.Generic.List<WindowsAutomationBridge.AutomationChange>();
        void Collect(System.Collections.Generic.IReadOnlyList<WindowsAutomationBridge.AutomationChange> raised) => observed.AddRange(raised);
        bridge.ChangesRaised += Collect;
        try { change(); }
        finally { bridge.ChangesRaised -= Collect; }
        observed.AddRange(bridge.DetectChanges(element, peer));
        return observed;
    }

    private static WindowsTextRange OtherDocument(WindowsAutomationBridge bridge, StandardPanel root)
    {
        var other = new StandardRichEdit();
        other.SetPlainText("other");
        root.AddChild(other);
        return bridge.GetOrCreatePeer(other).DocumentRange;
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
