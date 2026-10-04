using System;
using System.Linq;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.Button.Standard;
using Broiler.UI.Edit.Standard;
using Broiler.UI.Forms.Standard;
using Broiler.UI.Label.Standard;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;
using Broiler.UI.TabView;
using Broiler.UI.TabView.Standard;
using Broiler.UI.TreeView;
using Broiler.UI.TreeView.Standard;
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
    public void AScrollViewThatTakesTheKeyboardIsAFocusableControlWithOrWithoutAName()
    {
        var (session, bridge, root) = Create();
        var stop = new Broiler.UI.ScrollView.Standard.StandardScrollView { FocusWhenScrollable = true };
        var plain = new Broiler.UI.ScrollView.Standard.StandardScrollView();
        stop.AddChild(new Filler());
        plain.AddChild(new Filler());
        root.AddChild(stop);
        root.AddChild(plain);
        foreach (var scroll in new[] { stop, plain })
        {
            scroll.Measure(new BSize(300, 100));
            scroll.Arrange(new BRect(0, 0, 300, 100));
        }

        // A keyboard stop while it scrolls: CanFocus, though not Focusable (Broiler.UI ADR 0028).
        Assert.True(stop.CanFocus);
        Assert.False(stop.Focusable);
        var stopPeer = bridge.GetOrCreatePeer(stop);
        Assert.Equal(true, stopPeer.GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
        Assert.Equal(true, stopPeer.GetPropertyValue(UiaNative.UiaIsControlElementPropertyId));

        // Without the opt-in it is layout only...
        var plainPeer = bridge.GetOrCreatePeer(plain);
        Assert.Equal(false, plainPeer.GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
        Assert.Equal(false, plainPeer.GetPropertyValue(UiaNative.UiaIsControlElementPropertyId));
        // ...until an application focuses it by its own policy: what has the focus can take it.
        session.SetFocus(plain);
        Assert.Equal(true, plainPeer.GetPropertyValue(UiaNative.UiaHasKeyboardFocusPropertyId));
        Assert.Equal(true, plainPeer.GetPropertyValue(UiaNative.UiaIsKeyboardFocusablePropertyId));
        Assert.Equal(true, plainPeer.GetPropertyValue(UiaNative.UiaIsControlElementPropertyId));
    }

    [Fact]
    public void StandardControlTypesAreNamedByUiaAndOnlyCustomOnesByTheProvider()
    {
        var (_, bridge, root) = Create();
        var button = new StandardButton { Text = "Send" };
        var reader = new StandardRichEdit();
        var scroll = new Broiler.UI.ScrollView.Standard.StandardScrollView { AccessibleName = "Status and errors" };
        var code = new RoleElement(UiSemanticRole.CodeEditor);
        var dialog = new RoleElement(UiSemanticRole.Dialog);
        var list = new Broiler.UI.ListView.Standard.StandardListView();
        list.SetItems([new Broiler.UI.ListView.UiListItem("a", "Alpha")]);
        foreach (UiElement element in new UiElement[] { button, reader, scroll, code, dialog, list }) root.AddChild(element);

        // VT_EMPTY: UIA supplies "button", "edit", "pane" ... in the user's language, not "RichEdit".
        Assert.Null(bridge.GetOrCreatePeer(button).GetPropertyValue(UiaNative.UiaLocalizedControlTypePropertyId));
        Assert.Null(bridge.GetOrCreatePeer(reader).GetPropertyValue(UiaNative.UiaLocalizedControlTypePropertyId));
        Assert.Null(bridge.GetOrCreateItemPeer(list, 0).GetPropertyValue(UiaNative.UiaLocalizedControlTypePropertyId));
        Assert.Equal(UiaNative.UiaPaneControlTypeId, bridge.GetOrCreatePeer(scroll).GetPropertyValue(UiaNative.UiaControlTypePropertyId));
        Assert.Null(bridge.GetOrCreatePeer(scroll).GetPropertyValue(UiaNative.UiaLocalizedControlTypePropertyId));
        // A dialog drawn in the surface is a pane that says it is a dialog; Window needs patterns only a host window has.
        Assert.Equal(UiaNative.UiaPaneControlTypeId, bridge.GetOrCreatePeer(dialog).GetPropertyValue(UiaNative.UiaControlTypePropertyId));
        Assert.Equal(true, bridge.GetOrCreatePeer(dialog).GetPropertyValue(AutomationInterop.IsDialogPropertyId));
        Assert.Equal(false, bridge.GetOrCreatePeer(scroll).GetPropertyValue(AutomationInterop.IsDialogPropertyId));
        // A role UIA has no type for is custom, and says what it is in words.
        Assert.Equal(UiaNative.UiaCustomControlTypeId, bridge.GetOrCreatePeer(code).GetPropertyValue(UiaNative.UiaControlTypePropertyId));
        Assert.Equal("code editor", bridge.GetOrCreatePeer(code).GetPropertyValue(UiaNative.UiaLocalizedControlTypePropertyId));
    }

    [Fact]
    public void AToggleButtonIsAButtonThatTogglesAndReportsItsState()
    {
        var (_, bridge, root) = Create();
        var bold = new Broiler.UI.ToggleButton.Standard.StandardToggleButton { Text = "Bold" };
        root.AddChild(bold);
        var peer = bridge.GetOrCreatePeer(bold);

        Assert.Equal(UiaNative.UiaButtonControlTypeId, peer.GetPropertyValue(UiaNative.UiaControlTypePropertyId));
        Assert.Null(peer.GetPropertyValue(UiaNative.UiaLocalizedControlTypePropertyId));
        // Its click is its toggle, which is offered as Toggle alone.
        Assert.Null(peer.GetPatternProvider(UiaNative.UiaInvokePatternId));
        var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPatternProvider(UiaNative.UiaTogglePatternId));
        Assert.Equal(ToggleState.Off, toggle.ToggleState);

        var changes = Observe(bridge, bold, peer, toggle.Toggle);
        Assert.Equal(true, bold.IsChecked);
        Assert.Equal(ToggleState.On, toggle.ToggleState);
        Assert.Contains(changes, change => change.IsProperty && change.Id == UiaNative.UiaToggleToggleStatePropertyId
            && Equals(change.OldValue, (int)ToggleState.Off) && Equals(change.NewValue, (int)ToggleState.On));
    }

    private sealed class RoleElement(UiSemanticRole role) : UiElement
    {
        protected override UiSemanticNode GetSemanticNodeCore() =>
            new(role, role.ToString(), Bounds, UiSemanticState.Visible | UiSemanticState.Enabled, [], Id: SemanticId);
    }

    /// <summary>Content taller than a scroll viewport that takes no focus itself.</summary>
    private sealed class Filler : UiElement
    {
        protected override BSize MeasureCore(BSize availableSize) => new(100, 1000);
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
    public void ExplicitAccessibleNameWinsOverLabelAndPlaceholder()
    {
        var (_, bridge, root) = Create();
        var reader = new StandardRichEdit { PlaceholderText = "No message selected", AccessibleName = "Message body" };
        var edit = new StandardEdit { PlaceholderText = "hint", AccessibleName = "Search mail" };
        root.AddChild(new StandardLabel { Text = "Search", Target = edit });
        root.AddChild(reader);
        root.AddChild(edit);

        var readerPeer = bridge.GetOrCreatePeer(reader);
        Assert.Equal("Message body", readerPeer.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Equal("No message selected", readerPeer.GetPropertyValue(UiaNative.UiaHelpTextPropertyId));
        Assert.Equal("Search mail", bridge.GetOrCreatePeer(edit).GetPropertyValue(UiaNative.UiaNamePropertyId));
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

    [Fact]
    public void StatusElementsArePoliteLiveRegionsAndAssertiveWhenReportingAnError()
    {
        var (_, bridge, root) = Create();
        var status = new StatusElement();
        var button = new StandardButton { Text = "Send" };
        root.AddChild(status);
        root.AddChild(button);

        Assert.Equal((int)LiveSetting.Polite, bridge.GetOrCreatePeer(status).GetPropertyValue(UiaNative.UiaLiveSettingPropertyId));
        status.IsError = true;
        Assert.Equal((int)LiveSetting.Assertive, bridge.GetOrCreatePeer(status).GetPropertyValue(UiaNative.UiaLiveSettingPropertyId));
        // Other elements leave the property unsupported, which UIA reads as Off.
        Assert.Null(bridge.GetOrCreatePeer(button).GetPropertyValue(UiaNative.UiaLiveSettingPropertyId));
    }

    [Fact]
    public void AnnouncementsCarryTheirTextAndReplaceEarlierOnesFromTheSameElement()
    {
        var status = new StatusElement();
        var other = new StatusElement();

        var first = Assert.NotNull(StatusAnnouncements.Plan(status, "Receiving newest messages…"));
        var second = Assert.NotNull(StatusAnnouncements.Plan(status, "12 messages loaded."));
        Assert.Equal("12 messages loaded.", second.Text);
        Assert.Equal(NotificationProcessing.MostRecent, second.Processing);
        Assert.Equal(first.ActivityId, second.ActivityId);
        Assert.NotEqual(first.ActivityId, StatusAnnouncements.Plan(other, "Saved.")!.Value.ActivityId);

        status.IsError = true;
        Assert.Equal(NotificationProcessing.ImportantMostRecent, StatusAnnouncements.Plan(status, "Sending failed.")!.Value.Processing);
        // Nothing to read: the bridge falls back to a live-region change, read from the element's name.
        Assert.Null(StatusAnnouncements.Plan(status, ""));
        Assert.Null(StatusAnnouncements.Plan(status, null));
    }

    [Fact]
    public void DisclosureButtonExpandsItsSectionAndControlsTheContent()
    {
        var (session, bridge, root) = Create();
        var cc = new StandardEdit();
        var copies = new FormSection("Cc and Bcc", collapsible: true, expanded: false);
        copies.Content.AddChild(new FormField("Cc", cc));
        copies.Content.AddChild(new FormField("Bcc", new StandardEdit()));
        root.AddChild(copies);
        var toggle = bridge.GetOrCreatePeer(copies.Toggle!);

        var disclosure = Assert.IsAssignableFrom<IExpandCollapseProvider>(toggle.GetPatternProvider(UiaNative.UiaExpandCollapsePatternId));
        Assert.Equal(ExpandCollapseState.Collapsed, disclosure.ExpandCollapseState);
        // Hidden content is no place to send a reader.
        Assert.Null(toggle.GetPropertyValue(AutomationInterop.ControllerForPropertyId));
        // The state is on the button that has the focus, not on the group.
        Assert.Null(bridge.GetOrCreatePeer(copies).GetPatternProvider(UiaNative.UiaExpandCollapsePatternId));

        var changes = Observe(bridge, copies.Toggle!, toggle, disclosure.Expand);
        Assert.True(copies.IsExpanded);
        Assert.Equal(ExpandCollapseState.Expanded, disclosure.ExpandCollapseState);
        Assert.Contains(changes, change => change.IsProperty && change.Id == UiaNative.UiaExpandCollapseExpandCollapseStatePropertyId
            && Equals(change.OldValue, (int)ExpandCollapseState.Collapsed) && Equals(change.NewValue, (int)ExpandCollapseState.Expanded));
        var controlled = Assert.IsType<IRawElementProviderSimple[]>(toggle.GetPropertyValue(AutomationInterop.ControllerForPropertyId));
        Assert.Same(bridge.GetOrCreatePeer(copies.Content), Assert.Single(controlled));

        // Collapsing while a field inside has the focus moves it to the button.
        session.SetFocus(cc);
        disclosure.Collapse();
        Assert.False(copies.IsExpanded);
        Assert.Same(copies.Toggle, session.FocusedElement);
        Assert.Equal(ExpandCollapseState.Collapsed, disclosure.ExpandCollapseState);
    }

    [Fact]
    public void DisclosureActsThroughItsTargetAndRefusesWhenDisabled()
    {
        var (_, bridge, root) = Create();
        var details = new Details();
        var more = new StandardButton { Text = "More", Discloses = details };
        var plain = new StandardButton { Text = "Send" };
        root.AddChild(more);
        root.AddChild(plain);

        var disclosure = Assert.IsAssignableFrom<IExpandCollapseProvider>(bridge.GetOrCreatePeer(more).GetPatternProvider(UiaNative.UiaExpandCollapsePatternId));
        disclosure.Expand();
        Assert.True(details.IsExpanded);
        Assert.Equal(ExpandCollapseState.Expanded, disclosure.ExpandCollapseState);
        // A button that discloses nothing has no expand state at all.
        Assert.Null(bridge.GetOrCreatePeer(plain).GetPatternProvider(UiaNative.UiaExpandCollapsePatternId));

        more.IsEnabled = false;
        var refused = Assert.Throws<System.Runtime.InteropServices.COMException>(disclosure.Collapse);
        Assert.Equal(AutomationInterop.ElementNotEnabled, refused.HResult);
        Assert.True(details.IsExpanded);
    }

    [Fact]
    public void FieldErrorMakesTheControlInvalidAndDescribesItErrorFirst()
    {
        var (_, bridge, root) = Create();
        var email = new StandardEdit();
        var field = new FormField("Email address", email, "The address you sign in with.") { IsRequired = true };
        root.AddChild(field);
        var peer = bridge.GetOrCreatePeer(email);

        Assert.Equal(true, peer.GetPropertyValue(AutomationInterop.IsRequiredForFormPropertyId));
        Assert.Equal(true, peer.GetPropertyValue(AutomationInterop.IsDataValidForFormPropertyId));
        Assert.Equal(["The address you sign in with."], Names(peer.GetPropertyValue(AutomationInterop.DescribedByPropertyId)));
        Assert.Equal("The address you sign in with.", peer.GetPropertyValue(AutomationInterop.FullDescriptionPropertyId));

        var changes = Observe(bridge, email, peer, () => field.SetError("Enter an email address without a display name."));
        Assert.Equal(false, peer.GetPropertyValue(AutomationInterop.IsDataValidForFormPropertyId));
        Assert.Equal(["Error: Enter an email address without a display name.", "The address you sign in with."],
            Names(peer.GetPropertyValue(AutomationInterop.DescribedByPropertyId)));
        Assert.Equal("Error: Enter an email address without a display name. The address you sign in with.",
            peer.GetPropertyValue(AutomationInterop.FullDescriptionPropertyId));
        Assert.Contains(changes, change => change.IsProperty && change.Id == AutomationInterop.IsDataValidForFormPropertyId && Equals(change.NewValue, false));
        Assert.Contains(changes, change => change.IsProperty && change.Id == AutomationInterop.FullDescriptionPropertyId);

        // The relation crosses the native boundary as an array of elements.
        using (System.Runtime.InteropServices.Marshalling.ComVariant described = AutomationMarshalling.ToVariant(peer.GetPropertyValue(AutomationInterop.DescribedByPropertyId)))
        {
            Assert.Equal(System.Runtime.InteropServices.VarEnum.VT_ARRAY | System.Runtime.InteropServices.VarEnum.VT_UNKNOWN, described.VarType);
            Assert.Equal(0, SafeArrayGetUBound(described.GetRawDataRef<nint>(), 1, out int upper));
            Assert.Equal(1, upper);
        }

        Assert.Contains(Observe(bridge, email, peer, () => field.SetError(null)),
            change => change.Id == AutomationInterop.IsDataValidForFormPropertyId && Equals(change.NewValue, true));
        Assert.Equal(["The address you sign in with."], Names(peer.GetPropertyValue(AutomationInterop.DescribedByPropertyId)));
    }

    [Fact]
    public void RowsTabsTreeRowsAndTheRootAreValidAndNotRequiredUnlessTheirNodeSaysSo()
    {
        var (_, bridge, root) = Create();
        var list = new StandardListView();
        list.SetItems([new UiListItem("a", "Alpha"), new UiListItem("b", "Bravo")]);
        var tabs = new StandardTabView();
        tabs.AddTab("inbox", "Inbox");
        tabs.AddTab("drafts", "Drafts");
        var tree = new StandardTreeView { DataSource = new Folders() };
        foreach (UiElement element in new UiElement[] { list, tabs, tree }) root.AddChild(element);
        tree.Measure(new BSize(300, 200));
        tree.Arrange(new BRect(0, 0, 300, 200));
        var firstRow = Assert.IsType<WindowsElementAutomationPeer>(bridge.GetOrCreatePeer(tree).Navigate(NavigateDirection.FirstChild));

        // None is a form field. UIA reads either property left unanswered as false, which would call each one invalid.
        foreach (IRawElementProviderSimple peer in new IRawElementProviderSimple[] { bridge.GetOrCreateItemPeer(list, 1), bridge.GetOrCreateTabPeer(tabs, 1), firstRow, bridge })
        {
            Assert.Equal(true, peer.GetPropertyValue(AutomationInterop.IsDataValidForFormPropertyId));
            Assert.Equal(false, peer.GetPropertyValue(AutomationInterop.IsRequiredForFormPropertyId));
        }

        // What the container's node says of a row or tab is reported, as for an element.
        list.ItemPresenter = new FlaggingPresenter("b");
        var flaggedTabs = new FlaggingTabs("drafts");
        flaggedTabs.AddTab("inbox", "Inbox");
        flaggedTabs.AddTab("drafts", "Drafts");
        var flaggedTree = new FlaggingTree("archive") { DataSource = new Folders(), VisibleRowCapacity = 10 };
        root.AddChild(flaggedTabs);
        root.AddChild(flaggedTree);
        var inbox = Assert.IsType<WindowsElementAutomationPeer>(bridge.GetOrCreatePeer(flaggedTree).Navigate(NavigateDirection.FirstChild));
        var archive = Assert.IsType<WindowsElementAutomationPeer>(inbox.Navigate(NavigateDirection.NextSibling));
        foreach (var (plain, flagged) in new[]
        {
            (bridge.GetOrCreateItemPeer(list, 0), bridge.GetOrCreateItemPeer(list, 1)),
            (bridge.GetOrCreateTabPeer(flaggedTabs, 0), bridge.GetOrCreateTabPeer(flaggedTabs, 1)),
            (inbox, archive),
        })
        {
            Assert.Equal(true, plain.GetPropertyValue(AutomationInterop.IsDataValidForFormPropertyId));
            Assert.Equal(false, plain.GetPropertyValue(AutomationInterop.IsRequiredForFormPropertyId));
            Assert.Equal(false, flagged.GetPropertyValue(AutomationInterop.IsDataValidForFormPropertyId));
            Assert.Equal(true, flagged.GetPropertyValue(AutomationInterop.IsRequiredForFormPropertyId));
        }
    }

    private static UiSemanticNode Flagged(UiSemanticNode node) => node with { State = node.State | UiSemanticState.Invalid | UiSemanticState.Required };

    /// <summary>Describes one row as invalid and required, as a presenter may.</summary>
    private sealed class FlaggingPresenter(string flaggedId) : IUiListItemPresenter
    {
        public double GetItemHeight(UiListItem? item, UiDensity density, double availableWidth) => 28;
        public void Render(UiListItemRenderContext context) { }
        public UiSemanticNode CreateSemanticNode(UiListItemSemanticContext context)
        {
            var node = new UiSemanticNode(UiSemanticRole.ListItem, context.Item.Text, context.Bounds, UiSemanticState.Visible | UiSemanticState.Enabled, []);
            return context.Item.Id == flaggedId ? Flagged(node) : node;
        }
    }

    /// <summary>Describes one tab as invalid and required.</summary>
    private sealed class FlaggingTabs(string flaggedId) : UiTabView
    {
        protected override UiSemanticNode GetSemanticNodeCore()
        {
            UiSemanticNode node = base.GetSemanticNodeCore();
            return node with { Children = [.. node.Children.Select((tab, index) => Tabs[index].Id == flaggedId ? Flagged(tab) : tab)] };
        }
    }

    /// <summary>Describes one row in view as invalid and required.</summary>
    private sealed class FlaggingTree(string flaggedId) : UiTreeView
    {
        protected override UiSemanticNode GetSemanticNodeCore()
        {
            UiSemanticNode node = base.GetSemanticNodeCore();
            return node with { Children = [.. node.Children.Select((row, index) => Rows[FirstVisibleRow + index].Id.Value == flaggedId ? Flagged(row) : row)] };
        }
    }

    [Fact]
    public void TreeRowsAreTreeItemsThatExpandThroughTheTree()
    {
        var (_, bridge, root) = Create();
        var tree = new StandardTreeView { DataSource = new Folders() };
        root.AddChild(tree);
        tree.Measure(new BSize(300, 200));
        tree.Arrange(new BRect(0, 0, 300, 200));
        var treePeer = bridge.GetOrCreatePeer(tree);
        Assert.Equal(AutomationInterop.TreeControlTypeId, treePeer.GetPropertyValue(UiaNative.UiaControlTypePropertyId));

        var inbox = Assert.IsType<WindowsElementAutomationPeer>(treePeer.Navigate(NavigateDirection.FirstChild));
        var archive = Assert.IsType<WindowsElementAutomationPeer>(inbox.Navigate(NavigateDirection.NextSibling));
        Assert.Equal(AutomationInterop.TreeItemControlTypeId, inbox.GetPropertyValue(UiaNative.UiaControlTypePropertyId));
        Assert.StartsWith("Inbox", (string?)inbox.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Same(treePeer, inbox.Navigate(NavigateDirection.Parent));
        Assert.Null(archive.Navigate(NavigateDirection.NextSibling));
        // A row without children has nothing to expand.
        Assert.Null(archive.GetPatternProvider(UiaNative.UiaExpandCollapsePatternId));

        var disclosure = Assert.IsAssignableFrom<IExpandCollapseProvider>(inbox.GetPatternProvider(UiaNative.UiaExpandCollapsePatternId));
        Assert.Equal(ExpandCollapseState.Collapsed, disclosure.ExpandCollapseState);
        var changes = Observe(bridge, tree, treePeer, disclosure.Expand);
        Assert.True(tree.IsExpanded(new("inbox")));
        Assert.Equal(ExpandCollapseState.Expanded, disclosure.ExpandCollapseState);
        Assert.Contains(changes, change => ReferenceEquals(change.Target, inbox) && change.Id == UiaNative.UiaExpandCollapseExpandCollapseStatePropertyId
            && Equals(change.NewValue, (int)ExpandCollapseState.Expanded));

        // The children follow their row; the archive row keeps its peer below them.
        var receipts = Assert.IsType<WindowsElementAutomationPeer>(inbox.Navigate(NavigateDirection.NextSibling));
        Assert.StartsWith("Receipts", (string?)receipts.GetPropertyValue(UiaNative.UiaNamePropertyId));
        Assert.Same(archive, receipts.Navigate(NavigateDirection.NextSibling)!.Navigate(NavigateDirection.NextSibling));

        // Selecting a row selects just it, as a click does.
        Assert.IsAssignableFrom<ISelectionItemProvider>(receipts.GetPatternProvider(UiaNative.UiaSelectionItemPatternId)).Select();
        Assert.Equal([new TreeNodeId("receipts")], tree.Selection);
        var selection = Assert.IsAssignableFrom<ISelectionProvider>(treePeer.GetPatternProvider(UiaNative.UiaSelectionPatternId)).GetSelection();
        Assert.Same(receipts, Assert.Single(selection!));

        // A row is where the tree draws it, and a point on it finds it.
        BRect content = tree.ContentBounds;
        var expected = new BRect(content.Left, content.Top + tree.RowHeight, content.Width, tree.RowHeight);
        Assert.Equal((expected.X, expected.Y, expected.Width, expected.Height),
            (receipts.BoundingRectangle.Left, receipts.BoundingRectangle.Top, receipts.BoundingRectangle.Width, receipts.BoundingRectangle.Height));
        Assert.Same(receipts, bridge.ElementProviderFromPoint(expected.Left + 20, expected.Top + (expected.Height / 2)));

        // Collapsing takes the children's rows away, and their peers with them.
        disclosure.Collapse();
        Assert.False(receipts.IsAlive);
        Assert.Same(archive, inbox.Navigate(NavigateDirection.NextSibling));
    }

    private sealed class Folders : ITreeDataSource
    {
        private static readonly System.Collections.Generic.Dictionary<string, string[]> Children = new()
        {
            ["root"] = ["inbox", "archive"],
            ["inbox"] = ["receipts", "travel"],
            ["archive"] = [],
            ["receipts"] = [],
            ["travel"] = [],
        };

        public TreeNodeId Root => new("root");
        public int GetChildCount(TreeNodeId node) => Children[node.Value].Length;
        public TreeNodeId GetChild(TreeNodeId node, int index) => new(Children[node.Value][index]);
        public bool CanExpand(TreeNodeId node) => Children[node.Value].Length > 0;
        public TreeNodePresentation GetPresentation(TreeNodeId node) => new(node, char.ToUpperInvariant(node.Value[0]) + node.Value[1..]);
    }

    private static string[] Names(object? providers) =>
        Assert.IsType<IRawElementProviderSimple[]>(providers).Select(provider => provider.GetPropertyValue(UiaNative.UiaNamePropertyId) as string ?? string.Empty).ToArray();

    [System.Runtime.InteropServices.DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint array, uint dimension, out int bound);

    /// <summary>Shows and hides content of its own without being an element, as a host-side panel might.</summary>
    private sealed class Details : IUiExpandable
    {
        public bool IsExpanded { get; private set; }
        public bool Expand() => !IsExpanded && (IsExpanded = true);
        public bool Collapse() => IsExpanded && !(IsExpanded = false);
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

    private sealed class StatusElement : UiElement
    {
        public bool IsError { get; set; }
        protected override UiSemanticNode GetSemanticNodeCore() => new(UiSemanticRole.StatusAnnouncement, "Status", Bounds,
            UiSemanticState.Visible | (IsError ? UiSemanticState.Invalid : UiSemanticState.None), [], Id: SemanticId);
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
