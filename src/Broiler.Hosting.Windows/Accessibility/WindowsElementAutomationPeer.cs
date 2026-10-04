using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.Button;
using Broiler.UI.Edit;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.RichEdit;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI.TabView;
using Broiler.UI.TabView.Standard;
using Broiler.UI.TreeView;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>
/// Windows UI Automation provider peer for a <see cref="UiElement"/>, virtualized list item, tab item, or tree row.
/// Implements standard control patterns (Invoke, Value, Selection, SelectionItem, Toggle, ExpandCollapse, ScrollItem).
/// </summary>
public sealed partial class WindowsElementAutomationPeer :
    IRawElementProviderFragment,
    IInvokeProvider,
    IValueProvider,
    ISelectionItemProvider,
    ISelectionProvider,
    IToggleProvider,
    IExpandCollapseProvider,
    IScrollItemProvider,
    ITextProvider
{
    private readonly WindowsAutomationBridge _bridge;
    internal WindowsAutomationBridge Bridge => _bridge;
    private readonly WeakReference<UiElement>? _elementRef;
    private readonly WeakReference<UiListView>? _listViewRef;
    private readonly string? _itemId;
    private readonly WeakReference<UiTabView>? _tabViewRef;
    private readonly string? _tabId;

    public WindowsElementAutomationPeer(WindowsAutomationBridge bridge, UiElement element)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _elementRef = new WeakReference<UiElement>(element ?? throw new ArgumentNullException(nameof(element)));
        RuntimeIdValue = bridge.RuntimeIdFor(element);
    }

    /// <summary>A peer for the item now at <paramref name="itemIndex"/>. It follows that item, not the index, when the items change.</summary>
    public WindowsElementAutomationPeer(WindowsAutomationBridge bridge, UiListView listView, int itemIndex)
        : this(bridge, listView, ItemIdAt(listView, itemIndex))
    {
    }

    /// <summary>A peer for the tab now at <paramref name="tabIndex"/>. It follows that tab, not the index, when tabs move.</summary>
    public WindowsElementAutomationPeer(WindowsAutomationBridge bridge, UiTabView tabView, int tabIndex)
        : this(bridge, tabView, TabIdAt(tabView, tabIndex))
    {
    }

    internal WindowsElementAutomationPeer(WindowsAutomationBridge bridge, UiListView listView, string itemId)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _listViewRef = new WeakReference<UiListView>(listView ?? throw new ArgumentNullException(nameof(listView)));
        _itemId = itemId ?? throw new ArgumentNullException(nameof(itemId));
        RuntimeIdValue = bridge.AllocateRuntimeId();
    }

    internal WindowsElementAutomationPeer(WindowsAutomationBridge bridge, UiTabView tabView, string tabId)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _tabViewRef = new WeakReference<UiTabView>(tabView ?? throw new ArgumentNullException(nameof(tabView)));
        _tabId = tabId ?? throw new ArgumentNullException(nameof(tabId));
        RuntimeIdValue = bridge.AllocateRuntimeId();
    }

    public bool IsItem => _itemId is not null;

    public bool IsTab => _tabId is not null;

    public UiElement? Element => _elementRef is not null && _elementRef.TryGetTarget(out UiElement? el) ? el : null;

    public UiListView? ListView => _listViewRef is not null && _listViewRef.TryGetTarget(out UiListView? lv) ? lv : null;

    public UiTabView? TabView => _tabViewRef is not null && _tabViewRef.TryGetTarget(out UiTabView? tv) ? tv : null;

    /// <summary>The id of the list item this peer stands for, or null for other peers.</summary>
    public string? ItemId => _itemId;

    /// <summary>The id of the tab this peer stands for, or null for other peers.</summary>
    public string? TabId => _tabId;

    /// <summary>The item's current index, resolved on every call; -1 once the item is gone.</summary>
    public int ItemIndex => _itemId is not null && ListView is { } lv ? lv.IndexOf(_itemId) : -1;

    /// <summary>The tab's current index, resolved on every call; -1 once the tab is gone.</summary>
    public int TabIndex => _tabId is not null && TabView is { } tv ? IndexOfTab(tv, _tabId) : -1;

    /// <summary>
    /// The part of the runtime ID after <see cref="AutomationInterop.AppendRuntimeId"/>. The bridge allocates
    /// it, so it is unique among its peers and never reused: an element keeps its value for its lifetime,
    /// and an item or tab for as long as it exists, whatever its index.
    /// </summary>
    internal int RuntimeIdValue { get; }

    /// <summary>
    /// Whether the peer still stands for something on screen: its element is attached to the bridge's
    /// session (not disposed, not removed from the tree), its item or tab still exists, and the bridge
    /// and its window are still there. Native callers get UIA_E_ELEMENTNOTAVAILABLE otherwise.
    /// </summary>
    public bool IsAlive
    {
        get
        {
            if (_bridge.IsTornDown) return false;
            if (IsTreeItem) return TreeRowIsAlive;
            if (IsItem) return ListView is { } lv && IsAttached(lv) && ItemIndex >= 0;
            if (IsTab) return TabView is { } tv && IsAttached(tv) && TabIndex >= 0;
            return Element is { } el && IsAttached(el);
        }
    }

    // A removed element that is not disposed yet is gone as well: it has no parent to report.
    private bool IsAttached(UiElement element) => !element.IsDisposed && element.Session == _bridge.Session;

    internal static int IndexOfTab(UiTabView tabView, string tabId)
    {
        for (int index = 0; index < tabView.Tabs.Count; index++)
        {
            if (string.Equals(tabView.Tabs[index].Id, tabId, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static string ItemIdAt(UiListView listView, int index)
    {
        ArgumentNullException.ThrowIfNull(listView);
        return (uint)index < (uint)listView.Items.Count ? listView.Items[index].Id : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static string TabIdAt(UiTabView tabView, int index)
    {
        ArgumentNullException.ThrowIfNull(tabView);
        return (uint)index < (uint)tabView.Tabs.Count ? tabView.Tabs[index].Id : throw new ArgumentOutOfRangeException(nameof(index));
    }

    // --- IRawElementProviderSimple ---

    public ProviderOptions ProviderOptions =>
        ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;

    public IRawElementProviderSimple? HostRawElementProvider => null;

    public object? GetPatternProvider(int patternId)
    {
        if (!IsAlive) return null;
        if (IsTreeItem) return TreeRowPattern(patternId);

        if (IsItem)
        {
            return patternId switch
            {
                UiaNative.UiaSelectionItemPatternId => this,
                UiaNative.UiaScrollItemPatternId => this,
                _ => null,
            };
        }

        if (IsTab)
        {
            return patternId switch
            {
                UiaNative.UiaSelectionItemPatternId => this,
                UiaNative.UiaInvokePatternId => this,
                UiaNative.UiaScrollItemPatternId => this,
                _ => null,
            };
        }

        UiElement? el = Element;
        if (el is null) return null;

        UiSemanticNode node = el.GetSemanticNode();

        return patternId switch
        {
            // A toggle button's click is its Toggle, which UIA offers instead of Invoke, as for WPF and WinUI.
            UiaNative.UiaInvokePatternId when (node.Role == UiSemanticRole.Button || el is UiButton) && node.Role != UiSemanticRole.ToggleButton => this,
            UiaNative.UiaValuePatternId when node.Role is UiSemanticRole.Edit or UiSemanticRole.RichEdit || el is UiEdit or UiRichEdit => this,
            UiaNative.UiaSelectionItemPatternId when node.Role is UiSemanticRole.RadioButton => this,
            UiaNative.UiaSelectionPatternId when node.Role is UiSemanticRole.ListView or UiSemanticRole.TabView || el is UiListView or UiTabView => this,
            UiaNative.UiaTogglePatternId when IsToggle(node.Role) => this,
            // Whatever reports Expanded or Collapsed expands: a combo box, a menu, a disclosure button.
            UiaNative.UiaExpandCollapsePatternId when HasExpandState(node.State) => this,
            UiaNative.UiaScrollItemPatternId when el.Parent is not null => this,
            // Password fields never expose their text, not even through the Text pattern.
            UiaNative.UiaTextPatternId when AutomationExposure.IsTextControl(el, node) && el is not UiEdit { IsPassword: true } => this,
            _ => null,
        };
    }

    public object? GetPropertyValue(int propertyId)
    {
        if (!IsAlive) return null;
        if (IsTreeItem) return TreeRowProperty(propertyId);

        if (IsItem)
        {
            UiListView? lv = ListView;
            int index = ItemIndex;
            if (lv is null || index < 0) return null;
            UiListItem item = lv.Items[index];

            return propertyId switch
            {
                UiaNative.UiaControlTypePropertyId => UiaNative.UiaListItemControlTypeId,
                UiaNative.UiaNamePropertyId => ItemName(lv, item, index),
                // Not repeated when the presenter's name already says it.
                UiaNative.UiaHelpTextPropertyId => item.SecondaryText is { Length: > 0 } secondary
                    && !ItemName(lv, item, index).Contains(secondary, StringComparison.Ordinal) ? secondary : string.Empty,
                UiaNative.UiaAutomationIdPropertyId => $"item_{item.Id}",
                UiaNative.UiaIsEnabledPropertyId => lv.GetSemanticNode().State.HasFlag(UiSemanticState.Enabled),
                UiaNative.UiaIsKeyboardFocusablePropertyId => CanHoldFocus(lv, string.Equals(lv.SelectedItemId, item.Id, StringComparison.Ordinal)),
                UiaNative.UiaHasKeyboardFocusPropertyId => lv.SelectedIndex == index && _bridge.Session.FocusedElement == lv,
                UiaNative.UiaIsOffscreenPropertyId => VisibleBounds.IsEmpty,
                UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
                UiaNative.UiaItemStatusPropertyId => item.IsRead == false ? "Unread" : "Read",
                AutomationInterop.IsRequiredForFormPropertyId => ItemState(lv, index).HasFlag(UiSemanticState.Required),
                AutomationInterop.IsDataValidForFormPropertyId => !ItemState(lv, index).HasFlag(UiSemanticState.Invalid),
                _ => null,
            };
        }

        if (IsTab)
        {
            UiTabView? tv = TabView;
            int index = TabIndex;
            if (tv is null || index < 0) return null;
            UiTabItem tab = tv.Tabs[index];

            return propertyId switch
            {
                UiaNative.UiaControlTypePropertyId => UiaNative.UiaTabItemControlTypeId,
                UiaNative.UiaNamePropertyId => tab.Header,
                UiaNative.UiaAutomationIdPropertyId => $"tab_{tab.Id}",
                UiaNative.UiaIsEnabledPropertyId => tv.GetSemanticNode().State.HasFlag(UiSemanticState.Enabled),
                UiaNative.UiaIsKeyboardFocusablePropertyId => CanHoldFocus(tv, tv.SelectedIndex == index),
                UiaNative.UiaHasKeyboardFocusPropertyId => tv.SelectedIndex == index && _bridge.Session.FocusedElement == tv,
                UiaNative.UiaIsOffscreenPropertyId => VisibleBounds.IsEmpty,
                UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
                AutomationInterop.IsRequiredForFormPropertyId => TabState(tv, index).HasFlag(UiSemanticState.Required),
                AutomationInterop.IsDataValidForFormPropertyId => !TabState(tv, index).HasFlag(UiSemanticState.Invalid),
                _ => null,
            };
        }

        UiElement? element = Element;
        if (element is null) return null;
        UiSemanticNode semantic = element.GetSemanticNode();
        string name = AutomationExposure.Name(semantic);

        return propertyId switch
        {
            UiaNative.UiaControlTypePropertyId => element is UiTreeView ? AutomationInterop.TreeControlTypeId : MapRoleToControlType(semantic.Role),
            // Standard control types are named by UIA in the user's language; only a custom one needs a name.
            UiaNative.UiaLocalizedControlTypePropertyId => element is UiTreeView || MapRoleToControlType(semantic.Role) != UiaNative.UiaCustomControlTypeId ? null : CustomTypeName(semantic.Role),
            // No type-name fallback: an unnamed element has an empty name, not its class name.
            UiaNative.UiaNamePropertyId => name,
            UiaNative.UiaLabeledByPropertyId => element.LabeledBy is { } label && IsAttached(label) && AutomationExposure.IsExposed(label) ? _bridge.GetOrCreatePeer(label) : null,
            UiaNative.UiaIsControlElementPropertyId => !AutomationExposure.IsLayoutOnly(element, semantic, name),
            UiaNative.UiaIsContentElementPropertyId => !AutomationExposure.IsLayoutOnly(element, semantic, name),
            UiaNative.UiaAutomationIdPropertyId => element.SemanticId.ToString(),
            UiaNative.UiaClassNamePropertyId => element.GetType().Name,
            UiaNative.UiaHelpTextPropertyId => AutomationExposure.HelpText(element, name),
            UiaNative.UiaIsEnabledPropertyId => semantic.State.HasFlag(UiSemanticState.Enabled),
            UiaNative.UiaIsKeyboardFocusablePropertyId => AutomationExposure.IsKeyboardFocusable(element),
            UiaNative.UiaHasKeyboardFocusPropertyId => _bridge.Session.FocusedElement == element,
            UiaNative.UiaIsOffscreenPropertyId => semantic.State.HasFlag(UiSemanticState.Offscreen) || VisibleBounds.IsEmpty,
            UiaNative.UiaIsPasswordPropertyId => element is UiEdit { IsPassword: true },
            UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
            UiaNative.UiaLiveSettingPropertyId => StatusAnnouncements.LiveSettingFor(semantic) is var live and not LiveSetting.Off ? (int)live : null,
            // Form semantics (Broiler.UI ADR 0028). The error is reached through ErrorMessage, listed
            // before the field's own description; Description carries both for clients that never
            // follow relations.
            AutomationInterop.IsRequiredForFormPropertyId => semantic.State.HasFlag(UiSemanticState.Required),
            AutomationInterop.IsDataValidForFormPropertyId => !semantic.State.HasFlag(UiSemanticState.Invalid),
            AutomationInterop.ControllerForPropertyId => Related(element, element.Controls),
            AutomationInterop.DescribedByPropertyId => Related(element, ShownErrorMessage(element), element.DescribedBy),
            AutomationInterop.FullDescriptionPropertyId => string.IsNullOrWhiteSpace(semantic.Description) ? null : semantic.Description,
            AutomationInterop.IsDialogPropertyId => semantic.Role == UiSemanticRole.Dialog,
            _ => null,
        };
    }

    /// <summary>
    /// Whether keyboard focus can be on a row, tab or tree row. Broiler.UI lists, tab views and trees keep
    /// no focus apart from their selection (a tree, its focused node), so the focus can be on that one
    /// item only, and only while its container can take the focus. Reporting the other items focusable
    /// would invite a client that moves the focus with its cursor, such as Narrator, to call SetFocus on
    /// them and see the focus land on another item.
    /// </summary>
    private static bool CanHoldFocus(UiElement container, bool isCurrentItem) =>
        isCurrentItem && AutomationExposure.IsKeyboardFocusable(container);

    // A row, tab or tree row answers IsDataValidForForm and IsRequiredForForm from the state of the node
    // its container describes it with: valid and not required unless that node says otherwise. UIA reads
    // a property left unanswered as false, so a client would hear every row and tab called invalid.
    private static UiSemanticState ItemState(UiListView listView, int index) =>
        listView.GetItemSemanticNode(index)?.State ?? UiSemanticState.None;

    private static UiSemanticState TabState(UiTabView tabView, int index) =>
        tabView.GetSemanticNode().Children is { } tabs && index < tabs.Count ? tabs[index].State : UiSemanticState.None;

    // The peers of the related elements a client can go to, in order, or none.
    private IRawElementProviderSimple[]? Related(UiElement element, params UiElement?[] related)
    {
        List<IRawElementProviderSimple>? peers = null;
        foreach (UiElement? candidate in related)
        {
            if (!AutomationExposure.IsShownRelation(element, candidate)) continue;
            WindowsElementAutomationPeer peer = _bridge.GetOrCreatePeer(candidate!);
            if (!(peers ??= []).Contains(peer)) peers.Add(peer);
        }
        return peers?.ToArray();
    }

    // An error message counts while it says something, as it does for the element's Invalid state.
    private static UiElement? ShownErrorMessage(UiElement element) =>
        element.ErrorMessage is { } message && !string.IsNullOrWhiteSpace(message.GetSemanticNode().Name) ? message : null;

    internal static bool IsToggle(UiSemanticRole role) => role is UiSemanticRole.CheckBox or UiSemanticRole.ToggleButton;

    internal static ToggleState ToggleStateOf(UiSemanticState state) =>
        state.HasFlag(UiSemanticState.Indeterminate) ? ToggleState.Indeterminate
        : state.HasFlag(UiSemanticState.Checked) ? ToggleState.On
        : ToggleState.Off;

    internal static bool HasExpandState(UiSemanticState state) => (state & (UiSemanticState.Expanded | UiSemanticState.Collapsed)) != 0;

    internal static ExpandCollapseState ExpandStateOf(UiSemanticState state) =>
        state.HasFlag(UiSemanticState.Expanded) ? ExpandCollapseState.Expanded
        : state.HasFlag(UiSemanticState.Collapsed) ? ExpandCollapseState.Collapsed
        : ExpandCollapseState.LeafNode;

    // --- IRawElementProviderFragment ---

    public IRawElementProviderFragmentRoot? FragmentRoot => _bridge;

    /// <summary>The visible part of the element, item or tab on screen; empty while none of it can be seen.</summary>
    public UiaRect BoundingRectangle
    {
        get
        {
            if (!IsAlive) return default;
            BRect visible = VisibleBounds;
            return visible.IsEmpty ? default : _bridge.GetScreenRect(visible);
        }
    }

    /// <summary>
    /// What can be seen, in DIPs: the element's <see cref="UiElement.GetVisibleBounds"/>, an item's node
    /// from <see cref="UiListView.GetItemSemanticNode"/> (already cut to the list's content area), a
    /// tab's header or a tree row in view, each clipped to the window's surface. Empty when it is
    /// hidden or scrolled or clipped entirely out of view, which is also when it reports IsOffscreen.
    /// </summary>
    internal BRect VisibleBounds
    {
        get
        {
            if (IsTreeItem)
                return TreeView is { } tree && RowIndex is var row and >= 0 ? _bridge.ClipToSurface(RowBounds(tree, row)) : BRect.Empty;

            if (IsItem)
            {
                return ListView is { } lv && ItemIndex is var index and >= 0 && lv.GetItemSemanticNode(index) is { } node
                    && !node.State.HasFlag(UiSemanticState.Offscreen)
                    ? _bridge.ClipToSurface(node.Bounds)
                    : BRect.Empty;
            }

            if (IsTab)
            {
                return TabView is { } tv && TabIndex is var index and >= 0 && index < tv.VisibleTabCapacity
                    ? _bridge.ClipToSurface(TabBounds(tv, index))
                    : BRect.Empty;
            }

            return Element is { } element && AutomationExposure.IsExposed(element)
                ? _bridge.ClipToSurface(element.GetVisibleBounds())
                : BRect.Empty;
        }
    }

    /// <summary>
    /// A tab's header as the view draws and hit-tests it, cut to the view's visible bounds; a view that
    /// reports no header geometry places its tabs on the whole view, as their semantic nodes do.
    /// </summary>
    internal static BRect TabBounds(UiTabView tabView, int index)
    {
        BRect visible = tabView.GetVisibleBounds();
        BRect header = tabView.GetTabHeaderBounds(index);
        return header.IsEmpty ? visible : header.Intersect(visible);
    }

    // The presenter's name (for a mail row, the sender, subject and full received date) as the list
    // describes the item; the item's own text when the presenter gives none.
    private static string ItemName(UiListView listView, UiListItem item, int index) =>
        listView.GetItemSemanticNode(index)?.Name is { Length: > 0 } name ? name
        : !string.IsNullOrEmpty(item.Text) ? item.Text : item.Id;

    // UIA prefixes the appended value with the hosting window's runtime ID.
    public int[]? GetRuntimeId() => IsAlive ? [AutomationInterop.AppendRuntimeId, RuntimeIdValue] : null;

    public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;

    /// <summary>
    /// Moves keyboard focus here without changing the selection. A list row, tab or tree row has no
    /// focus of its own in Broiler.UI, so its container takes the focus; the focused item stays the
    /// selected one (a tree's focused node), which is why only that item reports IsKeyboardFocusable.
    /// Selecting is <see cref="Select"/>'s job, which UIA clients call after SetFocus: a focus call that
    /// also selected would run the application's selection handler first and then take the focus back
    /// from wherever that handler put it.
    /// </summary>
    public void SetFocus()
    {
        if (!IsAlive) return;

        UiElement? target = IsTreeItem ? TreeView : IsItem ? ListView : IsTab ? TabView : Element;
        if (target is { CanFocus: true })
            _bridge.Session.SetFocus(target);
    }

    public IRawElementProviderFragment? Navigate(NavigateDirection direction)
    {
        if (!IsAlive) return null;
        if (IsTreeItem) return TreeRowNavigate(direction);

        if (IsItem)
        {
            UiListView? lv = ListView;
            int index = ItemIndex;
            if (lv is null || index < 0) return null;

            return direction switch
            {
                NavigateDirection.Parent => _bridge.GetOrCreatePeer(lv),
                NavigateDirection.NextSibling => index + 1 < lv.Items.Count ? _bridge.GetOrCreateItemPeer(lv, index + 1) : null,
                NavigateDirection.PreviousSibling => index > 0 ? _bridge.GetOrCreateItemPeer(lv, index - 1) : null,
                _ => null,
            };
        }

        if (IsTab)
        {
            UiTabView? tv = TabView;
            int index = TabIndex;
            if (tv is null || index < 0) return null;

            return direction switch
            {
                NavigateDirection.Parent => _bridge.GetOrCreatePeer(tv),
                NavigateDirection.NextSibling => index + 1 < tv.Tabs.Count
                    ? _bridge.GetOrCreateTabPeer(tv, index + 1)
                    : (tv.SelectedTab?.Content is { } content && AutomationExposure.IsExposed(content) ? _bridge.GetOrCreatePeer(content) : null),
                NavigateDirection.PreviousSibling => index > 0 ? _bridge.GetOrCreateTabPeer(tv, index - 1) : null,
                _ => null,
            };
        }

        UiElement? element = Element;
        if (element is null) return null;

        return direction switch
        {
            NavigateDirection.Parent => (element.Parent is not null && !ReferenceEquals(element.Parent, _bridge.Root))
                ? _bridge.GetOrCreatePeer(element.Parent)
                : _bridge,
            NavigateDirection.FirstChild => GetFirstChild(element),
            NavigateDirection.LastChild => GetLastChild(element),
            NavigateDirection.NextSibling => GetSibling(element, +1),
            NavigateDirection.PreviousSibling => GetSibling(element, -1),
            _ => null,
        };
    }

    private IRawElementProviderFragment? GetFirstChild(UiElement parent)
    {
        if (parent is UiTreeView tree)
            return FirstTreeRow(_bridge, tree);

        if (parent is UiListView lv && lv.Items.Count > 0)
            return _bridge.GetOrCreateItemPeer(lv, 0);

        if (parent is UiTabView tv && tv.Tabs.Count > 0)
            return _bridge.GetOrCreateTabPeer(tv, 0);

        foreach (UiElement child in parent.Children)
        {
            if (AutomationExposure.IsExposed(child))
                return _bridge.GetOrCreatePeer(child);
        }
        return null;
    }

    private IRawElementProviderFragment? GetLastChild(UiElement parent)
    {
        if (parent is UiTreeView tree)
            return LastTreeRow(_bridge, tree);

        if (parent is UiListView lv && lv.Items.Count > 0)
            return _bridge.GetOrCreateItemPeer(lv, lv.Items.Count - 1);

        if (parent is UiTabView tv)
        {
            for (int i = tv.Children.Count - 1; i >= 0; i--)
            {
                if (AutomationExposure.IsExposed(tv.Children[i]))
                    return _bridge.GetOrCreatePeer(tv.Children[i]);
            }
            if (tv.Tabs.Count > 0)
                return _bridge.GetOrCreateTabPeer(tv, tv.Tabs.Count - 1);
        }

        for (int i = parent.Children.Count - 1; i >= 0; i--)
        {
            UiElement child = parent.Children[i];
            if (AutomationExposure.IsExposed(child))
                return _bridge.GetOrCreatePeer(child);
        }
        return null;
    }

    private IRawElementProviderFragment? GetSibling(UiElement element, int offset)
    {
        UiElement? parent = element.Parent;
        if (parent is null) return null;

        // The selected tab's content follows the last tab item.
        if (parent is UiTabView tv && offset == -1 && ReferenceEquals(tv.SelectedTab?.Content, element) && tv.Tabs.Count > 0)
        {
            return _bridge.GetOrCreateTabPeer(tv, tv.Tabs.Count - 1);
        }

        int index = -1;
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (ReferenceEquals(parent.Children[i], element))
            {
                index = i;
                break;
            }
        }
        if (index < 0) return null;

        int target = index + offset;
        while (target >= 0 && target < parent.Children.Count)
        {
            if (AutomationExposure.IsExposed(parent.Children[target]))
                return _bridge.GetOrCreatePeer(parent.Children[target]);
            target += offset;
        }

        return null;
    }

    // --- ITextProvider ---

    /// <summary>The plain text of a text control, or null for other elements and password fields.</summary>
    internal string? TextValue
    {
        get
        {
            if (Element is not { } el || el is UiEdit { IsPassword: true }) return null;
            UiSemanticNode node = el.GetSemanticNode();
            return AutomationExposure.IsTextControl(el, node) ? node.TextInfo?.Value ?? string.Empty : null;
        }
    }

    internal void SelectText(int start, int end)
    {
        if (Element is IUiTextEditor editor)
            editor.SetEditorSelection(start, end);
        else if (Element is UiEdit edit)
            edit.SetSelection(start, end - start);
    }

    public WindowsTextRange[] GetTextSelection()
    {
        if (Element?.GetSemanticNode().TextInfo is not { } info || TextValue is null) return [];
        // A collapsed selection is the caret, reported as a degenerate range.
        return info.SelectionLength > 0
            ? [new WindowsTextRange(this, info.SelectionStart, info.SelectionStart + info.SelectionLength)]
            : [new WindowsTextRange(this, info.CaretIndex, info.CaretIndex)];
    }

    // The semantic model carries no scroll geometry for text, so the whole document is reported.
    public WindowsTextRange[] GetVisibleRanges() => [DocumentRange];

    public WindowsTextRange DocumentRange => new(this, 0, (TextValue ?? string.Empty).Length);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    // Without glyph geometry the nearest position is not known; the start of the text is returned.
    public WindowsTextRange RangeFromPoint(double x, double y) => new(this, 0, 0);

    // --- IInvokeProvider ---

    public void Invoke()
    {
        if (!IsAlive) return;

        if (IsTab)
        {
            Select();
            return;
        }

        UiElement? el = Element;
        if (el is null) return;

        if (el is UiButton button)
        {
            button.Click();
            if (_bridge.ClientsListening())
                _bridge.RaiseEvent(this, UiaNative.UiaInvoke_InvokedEventId);
        }
    }

    // --- IValueProvider ---

    public string Value
    {
        get
        {
            if (!IsAlive) return string.Empty;
            UiElement? el = Element;
            if (el is null) return string.Empty;

            if (el is UiEdit edit)
            {
                // Protected passwords do not expose plain text over ValuePattern
                if (edit.IsPassword) return string.Empty;
                return edit.Text ?? string.Empty;
            }

            if (el is UiRichEdit richEdit)
                return richEdit.GetPlainText() ?? string.Empty;

            return el.GetSemanticNode().TextInfo?.Value ?? string.Empty;
        }
    }

    public bool IsReadOnly
    {
        get
        {
            if (!IsAlive) return true;
            UiElement? el = Element;
            if (el is UiEdit edit) return edit.IsReadOnly;
            if (el is UiRichEdit richEdit) return richEdit.IsReadOnly;
            return el?.GetSemanticNode().State.HasFlag(UiSemanticState.ReadOnly) ?? true;
        }
    }

    public void SetValue(string value)
    {
        if (!IsAlive) return;
        UiElement? el = Element;
        if (el is null) return;

        if (IsReadOnly)
            throw new InvalidOperationException("Control is read-only.");

        if (el is UiEdit edit)
        {
            edit.Text = value ?? string.Empty;
        }
        else if (el is UiRichEdit richEdit)
        {
            richEdit.SetPlainText(value ?? string.Empty);
        }
    }

    // --- ISelectionItemProvider ---

    public bool IsSelected
    {
        get
        {
            if (!IsAlive) return false;
            if (IsTreeItem) return TreeRowIsSelected;
            if (IsItem)
            {
                UiListView? lv = ListView;
                return lv is not null && lv.IsSelected(_itemId!);
            }
            if (IsTab)
            {
                UiTabView? tv = TabView;
                return tv is not null && tv.SelectedTab?.Id == _tabId;
            }

            UiElement? el = Element;
            return el?.GetSemanticNode().State.HasFlag(UiSemanticState.Selected) ?? false;
        }
    }

    public IRawElementProviderSimple? SelectionContainer
    {
        get
        {
            if (IsTreeItem) return TreeView is { } tree ? _bridge.GetOrCreatePeer(tree) : null;
            if (IsItem) return ListView is not null ? _bridge.GetOrCreatePeer(ListView) : null;
            if (IsTab) return TabView is not null ? _bridge.GetOrCreatePeer(TabView) : null;
            if (Element?.Parent is null) return null;
            return ReferenceEquals(Element.Parent, _bridge.Root) ? _bridge : _bridge.GetOrCreatePeer(Element.Parent);
        }
    }

    /// <summary>
    /// Selects this item as a click on it does. A list row or tab focuses its container first and then
    /// selects, so whatever the application does with the focus when the selection changes (Broiler.Mail
    /// returns to the reader when the Inbox tab is chosen) stands, as it does for the pointer. A tree
    /// row is selected without moving the focus, which is what a click on it does.
    /// </summary>
    /// <remarks>
    /// ElementSelected comes from change detection on the container, once, as for a click; a row that is
    /// already selected raises none.
    /// This departs from Win32 and WinUI, whose list and tab item providers select without moving the
    /// keyboard focus, and whose list items can hold the focus unselected. Broiler.UI has no focused but
    /// unselected row or tab, and the pointer's order (focus, then select) is the one applications
    /// already handle; selecting without the focus would leave a focused field inside tab content that
    /// the new selection hides.
    /// </remarks>
    public void Select()
    {
        if (!IsAlive) return;
        if (IsTreeItem)
        {
            TreeView!.SetSelection([_treeNode!.Value]);
            return;
        }
        if (IsItem)
        {
            UiListView? lv = ListView;
            int index = ItemIndex;
            if (lv is not null && index >= 0)
            {
                if (lv.CanFocus) _bridge.Session.SetFocus(lv);
                lv.SelectIndex(index);
                lv.ScrollIntoView(_itemId!);
            }
            return;
        }

        if (IsTab)
        {
            UiTabView? tv = TabView;
            int index = TabIndex;
            if (tv is not null && index >= 0)
            {
                if (tv.CanFocus) _bridge.Session.SetFocus(tv);
                tv.SelectIndex(index);
            }
            return;
        }

        UiElement? el = Element;
        if (el is UiButton button)
        {
            button.Click();
        }
    }

    /// <summary>
    /// Adds this item to the selection. In a list that selects several rows the row joins the others and
    /// the focus stays where it is; where only one can be selected this is <see cref="Select"/>.
    /// </summary>
    public void AddToSelection()
    {
        if (IsAlive && IsTreeItem) ChangeTreeSelection(add: true);
        else if (IsAlive && IsItem && ListView is { SelectionMode: UiListSelectionMode.Multiple } lv)
        {
            if (!lv.IsSelected(_itemId!)) lv.ToggleItem(_itemId!);
        }
        else Select();
    }

    public void RemoveFromSelection()
    {
        if (IsAlive && IsTreeItem)
        {
            ChangeTreeSelection(add: false);
            return;
        }

        if (IsItem && ListView is { SelectionMode: UiListSelectionMode.Multiple } lv && ItemIndex >= 0 && lv.IsSelected(_itemId!))
        {
            lv.ToggleItem(_itemId!);
        }
    }

    // --- ISelectionProvider ---

    public IRawElementProviderSimple[]? GetSelection()
    {
        if (!IsAlive) return null;
        UiElement? el = Element;
        if (el is UiListView lv)
        {
            // Every selected row, in item order; the list keeps no row that is gone selected.
            return lv.SelectedItemIds.Count > 0 ? [.. lv.SelectedItemIds.Select(itemId => (IRawElementProviderSimple)_bridge.ItemPeer(lv, itemId))] : null;
        }
        if (el is UiTabView tv && tv.SelectedTab is { } tab)
        {
            return [_bridge.TabPeer(tv, tab.Id)];
        }
        if (el is UiTreeView tree)
        {
            // Only the selected rows that are the tree's children; one scrolled out of view is not.
            var rows = new List<IRawElementProviderSimple>();
            foreach (TreeNodeId node in tree.Selection)
            {
                if (IsRowExposed(tree, IndexOfRow(tree, node))) rows.Add(_bridge.TreeRowPeer(tree, node));
            }
            return rows.Count > 0 ? rows.ToArray() : null;
        }

        return null;
    }

    public bool CanSelectMultiple => Element is UiListView { SelectionMode: UiListSelectionMode.Multiple } or UiTreeView { SelectionMode: TreeSelectionMode.Extended };

    public bool IsSelectionRequired => Element is UiTabView;

    // --- IToggleProvider ---

    public ToggleState ToggleState
    {
        get
        {
            if (!IsAlive) return ToggleState.Off;
            UiSemanticNode? node = Element?.GetSemanticNode();
            return node is null ? ToggleState.Off : ToggleStateOf(node.State);
        }
    }

    public void Toggle()
    {
        if (!IsAlive) return;
        UiElement? el = Element;
        if (el is UiButton button)
            button.Click();
    }

    // --- IExpandCollapseProvider ---

    // The state is the semantic flags; Broiler.UI keeps them where the focus is, on a disclosure button.
    public ExpandCollapseState ExpandCollapseState =>
        !IsAlive ? ExpandCollapseState.LeafNode
        : IsTreeItem ? TreeRowExpansion
        : Element is { } el ? ExpandStateOf(el.GetSemanticNode().State) : ExpandCollapseState.LeafNode;

    public void Expand()
    {
        if (!IsAlive) return;
        if (IsTreeItem) ExpandTreeRow(expand: true);
        else ExpandTarget().Expand();
    }

    public void Collapse()
    {
        if (!IsAlive) return;
        if (IsTreeItem) ExpandTreeRow(expand: false);
        else ExpandTarget().Collapse();
    }

    /// <summary>
    /// What acts: the element itself when it is an <see cref="IUiExpandable"/> (a combo box, a menu, a
    /// section), otherwise the target it <see cref="UiElement.Discloses"/>, as for a "Show Cc and Bcc" button.
    /// </summary>
    private IUiExpandable ExpandTarget()
    {
        UiElement element = Element!;
        UiSemanticState state = element.GetSemanticNode().State;
        if (!HasExpandState(state))
            throw new InvalidOperationException("The element does not expand or collapse.");
        if (!state.HasFlag(UiSemanticState.Enabled))
            throw AutomationInterop.ElementNotEnabledException();
        if (element is IUiExpandable expandable)
            return expandable;
        if (element.Discloses is { } target && target is not UiElement { IsDisposed: true })
            return target;
        throw new InvalidOperationException("The element reports an expand state but nothing acts on it.");
    }

    // --- IScrollItemProvider ---

    public void ScrollIntoView()
    {
        if (!IsAlive) return;
        if (IsTreeItem)
        {
            ScrollTreeRowIntoView();
        }
        else if (IsItem && ListView is { } lv)
        {
            lv.ScrollIntoView(_itemId!);
        }
        else if (Element is { } el)
        {
            el.BringIntoView();
        }
    }

    internal static double GetItemHeight(UiListView lv)
    {
        if (lv is StandardListView slv)
            return slv.EffectiveItemHeight;
        return lv.ItemPresenter?.GetItemHeight(null, lv.Density, lv.Bounds.Width) ?? 28.0;
    }

    private static int MapRoleToControlType(UiSemanticRole role) => role switch
    {
        UiSemanticRole.Button or UiSemanticRole.ToggleButton => UiaNative.UiaButtonControlTypeId,
        UiSemanticRole.CheckBox => UiaNative.UiaCheckBoxControlTypeId,
        UiSemanticRole.RadioButton => UiaNative.UiaRadioButtonControlTypeId,
        UiSemanticRole.ComboBox => UiaNative.UiaComboBoxControlTypeId,
        UiSemanticRole.Edit => UiaNative.UiaEditControlTypeId,
        UiSemanticRole.RichEdit => UiaNative.UiaEditControlTypeId,
        UiSemanticRole.Label => UiaNative.UiaTextControlTypeId,
        UiSemanticRole.ListView => UiaNative.UiaListControlTypeId,
        UiSemanticRole.ListItem => UiaNative.UiaListItemControlTypeId,
        UiSemanticRole.TabItem => UiaNative.UiaTabItemControlTypeId,
        UiSemanticRole.TabView => UiaNative.UiaTabControlTypeId,
        UiSemanticRole.MenuItem => UiaNative.UiaMenuItemControlTypeId,
        UiSemanticRole.Menu => UiaNative.UiaMenuControlTypeId,
        UiSemanticRole.ProgressBar => UiaNative.UiaProgressBarControlTypeId,
        UiSemanticRole.Slider => UiaNative.UiaSliderControlTypeId,
        UiSemanticRole.SpinBox => UiaNative.UiaSpinnerControlTypeId,
        UiSemanticRole.Splitter => UiaNative.UiaSeparatorControlTypeId,
        UiSemanticRole.Group => UiaNative.UiaGroupControlTypeId,
        UiSemanticRole.Panel => UiaNative.UiaPaneControlTypeId,
        UiSemanticRole.ScrollView => UiaNative.UiaPaneControlTypeId,
        UiSemanticRole.Toolbar => UiaNative.UiaToolBarControlTypeId,
        UiSemanticRole.Tooltip => UiaNative.UiaToolTipControlTypeId,
        // Inside the surface a window or dialog is a pane: the Window control type requires the Window and
        // Transform patterns, which only the host window offers. A dialog says so through IsDialog.
        UiSemanticRole.Window or UiSemanticRole.Dialog => UiaNative.UiaPaneControlTypeId,
        UiSemanticRole.Hyperlink => UiaNative.UiaHyperlinkControlTypeId,
        UiSemanticRole.ImageView => UiaNative.UiaImageControlTypeId,
        UiSemanticRole.StatusAnnouncement => UiaNative.UiaStatusBarControlTypeId,
        _ => UiaNative.UiaCustomControlTypeId,
    };

    // "code editor" for CodeEditor: the role's words, for the roles UIA has no control type for.
    private static string CustomTypeName(UiSemanticRole role)
    {
        string name = role.ToString();
        var words = new System.Text.StringBuilder(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index])) words.Append(' ');
            words.Append(char.ToLowerInvariant(name[index]));
        }
        return words.ToString();
    }}
