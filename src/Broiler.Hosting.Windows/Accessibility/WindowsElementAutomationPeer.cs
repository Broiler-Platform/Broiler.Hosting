using System;
using System.Collections.Generic;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.Button;
using Broiler.UI.ComboBox;
using Broiler.UI.Edit;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.RichEdit;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI.TabView;
using Broiler.UI.TabView.Standard;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>
/// Windows UI Automation provider peer for a <see cref="UiElement"/>, virtualized list item, or tab item.
/// Implements standard control patterns (Invoke, Value, Selection, SelectionItem, Toggle, ExpandCollapse, ScrollItem).
/// </summary>
public sealed class WindowsElementAutomationPeer :
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
            UiaNative.UiaInvokePatternId when node.Role == UiSemanticRole.Button || el is UiButton => this,
            UiaNative.UiaValuePatternId when node.Role is UiSemanticRole.Edit or UiSemanticRole.RichEdit || el is UiEdit or UiRichEdit => this,
            UiaNative.UiaSelectionItemPatternId when node.Role is UiSemanticRole.RadioButton => this,
            UiaNative.UiaSelectionPatternId when node.Role is UiSemanticRole.ListView or UiSemanticRole.TabView || el is UiListView or UiTabView => this,
            UiaNative.UiaTogglePatternId when node.Role is UiSemanticRole.CheckBox => this,
            UiaNative.UiaExpandCollapsePatternId when el is UiComboBox || node.State.HasFlag(UiSemanticState.Expanded) => this,
            UiaNative.UiaScrollItemPatternId when el.Parent is not null => this,
            // Password fields never expose their text, not even through the Text pattern.
            UiaNative.UiaTextPatternId when AutomationExposure.IsTextControl(el, node) && el is not UiEdit { IsPassword: true } => this,
            _ => null,
        };
    }

    public object? GetPropertyValue(int propertyId)
    {
        if (!IsAlive) return null;

        if (IsItem)
        {
            UiListView? lv = ListView;
            int index = ItemIndex;
            if (lv is null || index < 0) return null;
            UiListItem item = lv.Items[index];

            return propertyId switch
            {
                UiaNative.UiaControlTypePropertyId => UiaNative.UiaListItemControlTypeId,
                UiaNative.UiaLocalizedControlTypePropertyId => "list item",
                UiaNative.UiaNamePropertyId => !string.IsNullOrEmpty(item.Text) ? item.Text : item.Id,
                UiaNative.UiaHelpTextPropertyId => item.SecondaryText ?? string.Empty,
                UiaNative.UiaAutomationIdPropertyId => $"item_{item.Id}",
                UiaNative.UiaIsEnabledPropertyId => lv.GetSemanticNode().State.HasFlag(UiSemanticState.Enabled),
                UiaNative.UiaIsKeyboardFocusablePropertyId => true,
                UiaNative.UiaHasKeyboardFocusPropertyId => lv.SelectedIndex == index && _bridge.Session.FocusedElement == lv,
                UiaNative.UiaIsOffscreenPropertyId => IsItemOffscreen(lv, index),
                UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
                UiaNative.UiaItemStatusPropertyId => item.IsRead == false ? "Unread" : "Read",
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
                UiaNative.UiaLocalizedControlTypePropertyId => "tab item",
                UiaNative.UiaNamePropertyId => tab.Header,
                UiaNative.UiaAutomationIdPropertyId => $"tab_{tab.Id}",
                UiaNative.UiaIsEnabledPropertyId => tv.GetSemanticNode().State.HasFlag(UiSemanticState.Enabled),
                UiaNative.UiaIsKeyboardFocusablePropertyId => true,
                UiaNative.UiaHasKeyboardFocusPropertyId => tv.SelectedIndex == index && _bridge.Session.FocusedElement == tv,
                UiaNative.UiaIsOffscreenPropertyId => index >= tv.VisibleTabCapacity,
                UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
                _ => null,
            };
        }

        UiElement? element = Element;
        if (element is null) return null;
        UiSemanticNode semantic = element.GetSemanticNode();
        string name = AutomationExposure.Name(semantic);

        return propertyId switch
        {
            UiaNative.UiaControlTypePropertyId => MapRoleToControlType(semantic.Role),
            UiaNative.UiaLocalizedControlTypePropertyId => semantic.Role.ToString(),
            // No type-name fallback: an unnamed element has an empty name, not its class name.
            UiaNative.UiaNamePropertyId => name,
            UiaNative.UiaLabeledByPropertyId => element.LabeledBy is { } label && IsAttached(label) && AutomationExposure.IsExposed(label) ? _bridge.GetOrCreatePeer(label) : null,
            UiaNative.UiaIsControlElementPropertyId => !AutomationExposure.IsLayoutOnly(element, semantic, name),
            UiaNative.UiaIsContentElementPropertyId => !AutomationExposure.IsLayoutOnly(element, semantic, name),
            UiaNative.UiaAutomationIdPropertyId => element.SemanticId.ToString(),
            UiaNative.UiaClassNamePropertyId => element.GetType().Name,
            UiaNative.UiaHelpTextPropertyId => AutomationExposure.HelpText(element, name),
            UiaNative.UiaIsEnabledPropertyId => semantic.State.HasFlag(UiSemanticState.Enabled),
            UiaNative.UiaIsKeyboardFocusablePropertyId => element.CanFocus,
            UiaNative.UiaHasKeyboardFocusPropertyId => _bridge.Session.FocusedElement == element,
            UiaNative.UiaIsOffscreenPropertyId => semantic.State.HasFlag(UiSemanticState.Offscreen) || !AutomationExposure.IsExposed(element),
            UiaNative.UiaIsPasswordPropertyId => element is UiEdit { IsPassword: true },
            UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
            UiaNative.UiaLiveSettingPropertyId => StatusAnnouncements.LiveSettingFor(semantic) is var live and not LiveSetting.Off ? (int)live : null,
            _ => null,
        };
    }

    // --- IRawElementProviderFragment ---

    public IRawElementProviderFragmentRoot? FragmentRoot => _bridge;

    public UiaRect BoundingRectangle
    {
        get
        {
            if (!IsAlive) return default;

            if (IsItem)
            {
                UiListView? lv = ListView;
                int index = ItemIndex;
                if (lv is null || index < 0) return default;
                double height = GetItemHeight(lv);
                double top = lv.Bounds.Top - lv.VerticalOffset + (index * height);
                var dipRect = new BRect(lv.Bounds.Left, top, lv.Bounds.Width, height);
                return _bridge.GetScreenRect(dipRect);
            }

            if (IsTab)
            {
                UiTabView? tv = TabView;
                int index = TabIndex;
                if (tv is null || index < 0) return default;
                double headerHeight = (tv as StandardTabView)?.HeaderHeight ?? 32.0;
                double tabWidth = tv.Bounds.Width / Math.Max(1, tv.Tabs.Count);
                var dipRect = new BRect(tv.Bounds.Left + (index * tabWidth), tv.Bounds.Top, tabWidth, headerHeight);
                return _bridge.GetScreenRect(dipRect);
            }

            UiElement? el = Element;
            if (el is null) return default;
            return _bridge.GetScreenRect(el.Bounds);
        }
    }

    // UIA prefixes the appended value with the hosting window's runtime ID.
    public int[]? GetRuntimeId() => IsAlive ? [AutomationInterop.AppendRuntimeId, RuntimeIdValue] : null;

    public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;

    public void SetFocus()
    {
        if (!IsAlive) return;

        if (IsItem)
        {
            UiListView? lv = ListView;
            if (lv is not null)
            {
                lv.SelectIndex(ItemIndex);
                _bridge.Session.SetFocus(lv);
            }
            return;
        }

        if (IsTab)
        {
            UiTabView? tv = TabView;
            if (tv is not null)
            {
                tv.SelectIndex(TabIndex);
                _bridge.Session.SetFocus(tv);
            }
            return;
        }

        UiElement? el = Element;
        if (el is not null && el.CanFocus)
        {
            _bridge.Session.SetFocus(el);
        }
    }

    public IRawElementProviderFragment? Navigate(NavigateDirection direction)
    {
        if (!IsAlive) return null;

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
            if (UiaNative.UiaClientsAreListening())
                UiaNative.UiaRaiseAutomationEvent(NativeProviderAdapter.For(this)!, UiaNative.UiaInvoke_InvokedEventId);
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
            if (IsItem)
            {
                UiListView? lv = ListView;
                return lv is not null && lv.SelectedItemId == _itemId;
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
            if (IsItem) return ListView is not null ? _bridge.GetOrCreatePeer(ListView) : null;
            if (IsTab) return TabView is not null ? _bridge.GetOrCreatePeer(TabView) : null;
            if (Element?.Parent is null) return null;
            return ReferenceEquals(Element.Parent, _bridge.Root) ? _bridge : _bridge.GetOrCreatePeer(Element.Parent);
        }
    }

    public void Select()
    {
        if (!IsAlive) return;
        if (IsItem)
        {
            UiListView? lv = ListView;
            int index = ItemIndex;
            if (lv is not null && index >= 0)
            {
                lv.SelectIndex(index);
                if (UiaNative.UiaClientsAreListening())
                    UiaNative.UiaRaiseAutomationEvent(NativeProviderAdapter.For(this)!, UiaNative.UiaSelectionItem_ElementSelectedEventId);
            }
            return;
        }

        if (IsTab)
        {
            UiTabView? tv = TabView;
            int index = TabIndex;
            if (tv is not null && index >= 0)
            {
                tv.SelectIndex(index);
                if (UiaNative.UiaClientsAreListening())
                    UiaNative.UiaRaiseAutomationEvent(NativeProviderAdapter.For(this)!, UiaNative.UiaSelectionItem_ElementSelectedEventId);
            }
            return;
        }

        UiElement? el = Element;
        if (el is UiButton button)
        {
            button.Click();
        }
    }

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
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
        if (el is UiListView lv && lv.SelectedItemId is { } itemId && lv.IndexOf(itemId) >= 0)
        {
            return [_bridge.ItemPeer(lv, itemId)];
        }
        if (el is UiTabView tv && tv.SelectedTab is { } tab)
        {
            return [_bridge.TabPeer(tv, tab.Id)];
        }

        return null;
    }

    public bool CanSelectMultiple => Element is UiListView { SelectionMode: UiListSelectionMode.Multiple };

    public bool IsSelectionRequired => Element is UiTabView;

    // --- IToggleProvider ---

    public ToggleState ToggleState
    {
        get
        {
            if (!IsAlive) return ToggleState.Off;
            UiSemanticNode? node = Element?.GetSemanticNode();
            if (node is null) return ToggleState.Off;
            if (node.State.HasFlag(UiSemanticState.Indeterminate)) return ToggleState.Indeterminate;
            if (node.State.HasFlag(UiSemanticState.Checked)) return ToggleState.On;
            return ToggleState.Off;
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

    public ExpandCollapseState ExpandCollapseState
    {
        get
        {
            if (!IsAlive) return ExpandCollapseState.LeafNode;
            UiElement? el = Element;
            if (el is UiComboBox cb)
                return cb.IsDropDownOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
            if (el?.GetSemanticNode().State.HasFlag(UiSemanticState.Expanded) == true)
                return ExpandCollapseState.Expanded;
            return ExpandCollapseState.Collapsed;
        }
    }

    public void Expand()
    {
        if (!IsAlive) return;
        if (Element is UiComboBox cb)
            cb.OpenDropDown();
    }

    public void Collapse()
    {
        if (!IsAlive) return;
        if (Element is UiComboBox cb)
            cb.CloseDropDown();
    }

    // --- IScrollItemProvider ---

    public void ScrollIntoView()
    {
        if (!IsAlive) return;
        if (IsItem && ListView is { } lv)
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

    private static bool IsItemOffscreen(UiListView lv, int index)
    {
        double height = GetItemHeight(lv);
        double top = (index * height) - lv.VerticalOffset;
        return top + height <= 0 || top >= lv.Bounds.Height;
    }

    private static int MapRoleToControlType(UiSemanticRole role) => role switch
    {
        UiSemanticRole.Button => UiaNative.UiaButtonControlTypeId,
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
        UiSemanticRole.Hyperlink => UiaNative.UiaHyperlinkControlTypeId,
        UiSemanticRole.ImageView => UiaNative.UiaImageControlTypeId,
        UiSemanticRole.StatusAnnouncement => UiaNative.UiaStatusBarControlTypeId,
        _ => UiaNative.UiaCustomControlTypeId,
    };
}
