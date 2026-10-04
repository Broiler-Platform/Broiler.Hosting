using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Broiler.Graphics.Geometry;
using Broiler.Native.Windows;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.ListView;
using Broiler.UI.ListView.Standard;
using Broiler.UI.TabView;
using Broiler.UI.TreeView;
using Broiler.UI.TreeView.Standard;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>
/// Bridge between <see cref="UiSession"/> and Windows UI Automation.
/// Subclasses the native window to intercept <c>WM_GETOBJECT</c>, exposes the semantic root
/// via <see cref="IRawElementProviderFragmentRoot"/>, and propagates accessibility events.
/// </summary>
/// <remarks>
/// Pass the handle of a window that already exists. A zero handle attaches nothing, silently, so
/// clients see an empty pane: with a Broiler.Graphics <c>Direct2DWindow</c>, construct the bridge in
/// <c>OnCreated</c> (run during <c>WM_CREATE</c>), not in the window's constructor.
/// </remarks>
public sealed class WindowsAutomationBridge : IRawElementProviderFragmentRoot, IDisposable
{
    private static nuint _subclassCounter;
    private readonly nuint _subclassId;
    private readonly WindowNative.SubclassProc? _subclassProc;

    private readonly nint _hwnd;
    private readonly UiSession _session;
    private readonly UiElement _root;
    private readonly Func<double> _scaleProvider;

    private readonly Dictionary<long, WindowsElementAutomationPeer> _elementPeers = new();
    // Items and tabs are keyed by their id, not their index, so a peer and its runtime ID follow the
    // item when rows are inserted above it, and never come to stand for another item.
    private readonly Dictionary<(long ListViewId, string ItemId), WindowsElementAutomationPeer> _itemPeers = new();
    private readonly Dictionary<(long TabViewId, string TabId), WindowsElementAutomationPeer> _tabPeers = new();
    private readonly Dictionary<(long TreeViewId, string NodeId), WindowsElementAutomationPeer> _treeRowPeers = new();
    private readonly Dictionary<long, AutomationSnapshot> _snapshots = new();
    // An element keeps its runtime ID for its lifetime, even when its peer is dropped and made again.
    private readonly ConditionalWeakTable<UiElement, StrongBox<int>> _elementRuntimeIds = new();
    private readonly int _rootRuntimeId;
    private int _lastRuntimeId;

    private volatile bool _isDisposed;
    private volatile bool _windowDestroyed;
    private bool _isSubclassed;
    private bool _providersReleased;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    private const int WindowPollMilliseconds = 25;

    /// <summary>
    /// True once the bridge is disposed or its window has been destroyed. Every provider call then fails
    /// with UIA_E_ELEMENTNOTAVAILABLE instead of reading state that is being torn down.
    /// </summary>
    internal bool IsTornDown => _isDisposed || _windowDestroyed;

    // A window that is gone drains nothing more, so a call posted to it would only wait for the timeout.
    private bool CanReachUiThread => !IsTornDown && (_hwnd == nint.Zero || HwndNative.IsWindow(_hwnd));

    internal T OnUiThread<T>(Func<T> operation)
    {
        T Run()
        {
            if (IsTornDown) throw AutomationInterop.ElementNotAvailableException();
            return operation();
        }
        if (Environment.CurrentManagedThreadId == _uiThread) return Run();
        if (!CanReachUiThread) throw AutomationInterop.ElementNotAvailableException();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        _session.Dispatcher.Post(() =>
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
            try { completion.TrySetResult(Run()); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        try
        {
            // The wait is sliced so that a window destroyed meanwhile ends it at once.
            long deadline = Environment.TickCount64 + (long)CallTimeout.TotalMilliseconds;
            WaitHandle pending = ((IAsyncResult)completion.Task).AsyncWaitHandle;
            while (!pending.WaitOne(WindowPollMilliseconds))
            {
                if (!CanReachUiThread) throw AutomationInterop.ElementNotAvailableException();
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("The UI thread did not answer the automation call in time.");
            }
            return completion.Task.GetAwaiter().GetResult();
        }
        finally { Interlocked.CompareExchange(ref started, 2, 0); }
    }

    public WindowsAutomationBridge(nint hwnd, UiSession session, UiElement root, Func<double>? scaleProvider = null)
    {
        _hwnd = hwnd;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _scaleProvider = scaleProvider ?? (() => 1.0);
        _rootRuntimeId = AllocateRuntimeId();

        _session.SemanticChanged += OnSemanticChanged;

        if (_hwnd != nint.Zero)
        {
            _subclassId = ++_subclassCounter;
            _subclassProc = SubclassWindowProc;
            _isSubclassed = WindowNative.SetWindowSubclass(_hwnd, _subclassProc, _subclassId, 0);
        }
        else
        {
            _subclassProc = null;
        }
    }

    public nint Hwnd => _hwnd;

    public UiSession Session => _session;

    public UiElement Root => _root;

    public Func<double> ScaleProvider => _scaleProvider;

    /// <summary>Raised when a provider's native wrapper is disconnected from UIA, for diagnostics and tests.</summary>
    internal event Action<IRawElementProviderSimple>? ProviderDisconnected;

    /// <summary>
    /// Whether a UIA client listens for events. UIA answers for the whole machine, so tests pin it to
    /// see what a client would be sent.
    /// </summary>
    internal Func<bool> ClientsListening { get; set; } = UiaNative.UiaClientsAreListening;

    /// <summary>Automation events raised to UIA (not property or structure changes), for diagnostics and tests.</summary>
    internal event Action<IRawElementProviderSimple, int>? EventRaised;

    internal void RaiseEvent(IRawElementProviderSimple target, int eventId)
    {
        EventRaised?.Invoke(target, eventId);
        UiaNative.UiaRaiseAutomationEvent(NativeProviderAdapter.For(target)!, eventId);
    }

    private nint SubclassWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == UiaNative.WmGetObject && !IsTornDown && ((int)lParam == UiaNative.UiaRootObjectId || unchecked((uint)(long)lParam) == 0xFFFFFFE7))
        {
            return UiaNative.UiaReturnRawElementProvider(hWnd, wParam, lParam, NativeProviderAdapter.For(this)!);
        }

        if (uMsg == WindowNative.WmDestroy)
        {
            // Calls still arriving fail as not available, and UIA lets go of what it holds for the window.
            _windowDestroyed = true;
            ReleaseProviders();
        }
        else if (uMsg == WindowNative.WmNcdestroy)
        {
            // A subclass must be gone before the window is; DefSubclassProc still completes this call.
            RemoveSubclass();
        }

        return WindowNative.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void RemoveSubclass()
    {
        if (!_isSubclassed || _subclassProc is null) return;
        _isSubclassed = false;
        WindowNative.RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
    }

    /// <summary>
    /// Disconnects every provider wrapper this bridge handed to UIA and, for a window, tells UIA to
    /// release the providers it obtained for it (UiaReturnRawElementProvider with no provider).
    /// </summary>
    private void ReleaseProviders()
    {
        if (_providersReleased) return;
        _providersReleased = true;

        Disconnect(this);
        foreach (WindowsElementAutomationPeer peer in _elementPeers.Values) Disconnect(peer);
        foreach (WindowsElementAutomationPeer peer in _itemPeers.Values) Disconnect(peer);
        foreach (WindowsElementAutomationPeer peer in _tabPeers.Values) Disconnect(peer);
        foreach (WindowsElementAutomationPeer peer in _treeRowPeers.Values) Disconnect(peer);
        if (_hwnd != nint.Zero)
            AutomationInterop.UiaReleaseWindowProviders(_hwnd);

        _elementPeers.Clear();
        _itemPeers.Clear();
        _tabPeers.Clear();
        _treeRowPeers.Clear();
        _snapshots.Clear();
        _treeRows.Clear();
        _structureChanges.Clear();
        _treesToCheck.Clear();
    }

    // Only wrappers that exist were ever handed out; a peer that never reached UIA has nothing to release.
    private void Disconnect(IRawElementProviderSimple provider)
    {
        if (NativeProviderAdapter.Existing(provider) is not { } adapter) return;
        AutomationInterop.UiaDisconnectProvider(adapter);
        ProviderDisconnected?.Invoke(provider);
    }

    public WindowsElementAutomationPeer GetOrCreatePeer(UiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        // A semantic ID names one element for good, so an existing peer is that element's, alive or not.
        if (_elementPeers.TryGetValue(element.SemanticId, out WindowsElementAutomationPeer? existing))
            return existing;

        var peer = new WindowsElementAutomationPeer(this, element);
        _elementPeers[element.SemanticId] = peer;
        // The first snapshot is the baseline that later changes are compared with.
        _snapshots[element.SemanticId] = Capture(element, peer);
        if (element is UiTreeView tree)
            _treeRows[tree.SemanticId] = RowsInView(tree);
        return peer;
    }

    /// <summary>The peer of the item now at <paramref name="index"/>; it follows that item, not the index.</summary>
    public WindowsElementAutomationPeer GetOrCreateItemPeer(UiListView listView, int index)
    {
        ArgumentNullException.ThrowIfNull(listView);
        if ((uint)index >= (uint)listView.Items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ItemPeer(listView, listView.Items[index].Id);
    }

    /// <summary>The peer of the tab now at <paramref name="index"/>; it follows that tab, not the index.</summary>
    public WindowsElementAutomationPeer GetOrCreateTabPeer(UiTabView tabView, int index)
    {
        ArgumentNullException.ThrowIfNull(tabView);
        if ((uint)index >= (uint)tabView.Tabs.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return TabPeer(tabView, tabView.Tabs[index].Id);
    }

    // A row, tab or tree row peer also makes its container's peer: the container's snapshot is what its
    // selection changes are compared with, so a client that reached a row only through a focus event
    // still hears when the selection, and with it the focus, moves on.
    internal WindowsElementAutomationPeer ItemPeer(UiListView listView, string itemId)
    {
        var key = (listView.SemanticId, itemId);
        if (_itemPeers.TryGetValue(key, out WindowsElementAutomationPeer? existing))
            return existing;

        GetOrCreatePeer(listView);
        var peer = new WindowsElementAutomationPeer(this, listView, itemId);
        _itemPeers[key] = peer;
        return peer;
    }

    internal WindowsElementAutomationPeer TabPeer(UiTabView tabView, string tabId)
    {
        var key = (tabView.SemanticId, tabId);
        if (_tabPeers.TryGetValue(key, out WindowsElementAutomationPeer? existing))
            return existing;

        GetOrCreatePeer(tabView);
        var peer = new WindowsElementAutomationPeer(this, tabView, tabId);
        _tabPeers[key] = peer;
        return peer;
    }

    internal WindowsElementAutomationPeer TreeRowPeer(UiTreeView treeView, TreeNodeId node)
    {
        var key = (treeView.SemanticId, node.Value);
        if (_treeRowPeers.TryGetValue(key, out WindowsElementAutomationPeer? existing))
            return existing;

        GetOrCreatePeer(treeView);
        var peer = new WindowsElementAutomationPeer(this, treeView, node);
        _treeRowPeers[key] = peer;
        return peer;
    }

    /// <summary>A runtime ID value unique among this bridge's providers; values are never reused.</summary>
    internal int AllocateRuntimeId() => checked(++_lastRuntimeId);

    internal int RuntimeIdFor(UiElement element)
    {
        if (!_elementRuntimeIds.TryGetValue(element, out StrongBox<int>? id))
        {
            id = new StrongBox<int>(AllocateRuntimeId());
            _elementRuntimeIds.Add(element, id);
        }
        return id.Value;
    }

    public UiaRect GetScreenRect(BRect dipRect)
    {
        if (_hwnd == nint.Zero)
            return new UiaRect(dipRect.X, dipRect.Y, dipRect.Width, dipRect.Height);

        var pt = new WindowNative.POINT { X = 0, Y = 0 };
        WindowNative.ClientToScreen(_hwnd, ref pt);
        double scale = _scaleProvider();

        return new UiaRect(
            pt.X + (dipRect.X * scale),
            pt.Y + (dipRect.Y * scale),
            Math.Max(0, dipRect.Width * scale),
            Math.Max(0, dipRect.Height * scale));
    }

    /// <summary>
    /// <paramref name="dipRect"/> cut to the window's surface, the root's bounds; empty when it lies
    /// outside. Broiler.UI clips to scroll viewports and lists, but not to the host window.
    /// </summary>
    internal BRect ClipToSurface(BRect dipRect) => dipRect.IsEmpty ? BRect.Empty : dipRect.Intersect(_root.Bounds);

    private void OnSemanticChanged(object? sender, UiSemanticChangedEventArgs e)
    {
        if (IsTornDown)
            return;

        // Structure changes also drop the peers of removed elements, so they are collected whether or
        // not a client listens.
        if (e.Change is UiSemanticChangeKind.StructureChanged or UiSemanticChangeKind.SubtreeChanged)
        {
            QueueStructureChange(e.Element ?? _root);
            return;
        }

        // A tree's rows in view, which are its children, change without a structure change.
        if (e.Change is UiSemanticChangeKind.StateChanged && e.Element is UiTreeView changedTree && _treeRows.ContainsKey(changedTree.SemanticId))
            QueueTreeCheck(changedTree);

        if (!ClientsListening())
            return;

        switch (e.Change)
        {
            case UiSemanticChangeKind.FocusChanged:
                if (_focusHoldDepth > 0) _focusMovedWhileHeld = true;
                else RaiseFocusChanged();
                break;

            case UiSemanticChangeKind.StatusAnnounced:
                IRawElementProviderSimple target = e.Element is not null ? GetOrCreatePeer(e.Element) : this;
                if (StatusAnnouncements.Plan(e.Element!, e.Message) is { } notification && RaiseNotification(target, notification))
                    break;
                RaiseEvent(target, UiaNative.UiaLiveRegionChangedEventId);
                break;

            // Every semantic invalidation arrives as StateChanged; only real differences become UIA events,
            // and only for elements a client has already reached (and therefore has a peer).
            case UiSemanticChangeKind.StateChanged when e.Element is not null
                && _elementPeers.TryGetValue(e.Element.SemanticId, out WindowsElementAutomationPeer? statePeer) && statePeer.IsAlive:
                RaiseChanges(e.Element, statePeer);
                break;
        }
    }

    // The event names what now has the focus, as GetFocus does: the selected row of a focused list
    // rather than the list, and the root when focus went nowhere, never the element that lost it.
    private void RaiseFocusChanged()
    {
        IRawElementProviderSimple focused = FocusTarget();
        RaiseEvent(focused, UiaNative.UiaAutomationFocusChangedEventId);
        RaisePropertyChanged(focused, UiaNative.UiaHasKeyboardFocusPropertyId, false, true);
    }

    private int _focusHoldDepth;
    private bool _focusMovedWhileHeld;
    private (UiElement? Element, string? Item) _focusBeforeHold;

    /// <summary>
    /// Runs <paramref name="selection"/>, a selection a UIA client asked for, with focus events held, and
    /// then raises one focus event for wherever the focus is. Selecting focuses the list or tab view
    /// first, as a click does, and the focus event for that would name the row or tab selected before:
    /// a screen reader would read the old message ahead of the new one. Clients hear ElementSelected on
    /// the new row, and then the focus on it, or on whatever the application's selection handler focused;
    /// nothing when the focus ends where it started, since the moves between were never announced.
    /// </summary>
    /// <remarks>
    /// Only Select itself is covered. A client that calls SetFocus on the row before Select, as the
    /// managed System.Windows.Automation client does, has been told of the selected row by then, because
    /// SetFocus does not select; the COM client's Select calls Select alone.
    /// </remarks>
    internal void HoldFocusEvents(Action selection)
    {
        if (_focusHoldDepth++ == 0) _focusBeforeHold = FocusedItem();
        try { selection(); }
        finally
        {
            if (--_focusHoldDepth == 0 && _focusMovedWhileHeld)
            {
                _focusMovedWhileHeld = false;
                if (!IsTornDown && ClientsListening() && FocusedItem() != _focusBeforeHold)
                    RaiseFocusChanged();
            }
        }
    }

    private readonly List<UiElement> _structureChanges = [];
    private bool _structureFlushQueued;

    // The rows exposed by each tree a client has reached, and the trees whose rows may have changed:
    // expanding, collapsing and moving the focus change them, and so does scrolling, which Broiler.UI
    // reports only as a render change. Moving the focus scrolls after the selection's state change, so
    // they are compared when the dispatcher next runs, after the change is complete.
    private readonly Dictionary<long, string> _treeRows = new();
    private readonly List<UiTreeView> _treesToCheck = [];

    /// <summary>Raised with the providers whose children a flush invalidated, for diagnostics and tests.</summary>
    internal event Action<IReadOnlyList<IRawElementProviderSimple>>? StructureInvalidated;

    // Broiler.UI raises StructureChanged once per changed parent after an input dispatch or a frame, but
    // one per change for changes an application makes between them. The bridge collects them until the
    // dispatcher next runs, which a host does before each frame, so a refresh that adds a hundred rows
    // or fields is one event per container. An ImmediateUiDispatcher runs the flush inside Post, so
    // there each change is flushed on its own.
    private void QueueStructureChange(UiElement element)
    {
        if (!_structureChanges.Contains(element))
            _structureChanges.Add(element);
        QueueFlush();
    }

    /// <summary>Compares the tree's rows in view with what clients were last told when the dispatcher next runs.</summary>
    internal void QueueTreeCheck(UiTreeView tree)
    {
        if (IsTornDown)
            return;
        if (!_treesToCheck.Contains(tree))
            _treesToCheck.Add(tree);
        QueueFlush();
    }

    private void QueueFlush()
    {
        if (_structureFlushQueued)
            return;

        _structureFlushQueued = true;
        _session.Dispatcher.Post(FlushStructureChanges);
    }

    /// <summary>
    /// Drops the peers of what was removed, disconnecting them from UIA, and raises one
    /// children-invalidated event on the peer of each changed parent, a tree whose exposed rows differ
    /// included. A parent no client has reached has no peer and needs none, and one inside another
    /// changed parent is covered by it.
    /// </summary>
    private void FlushStructureChanges()
    {
        _structureFlushQueued = false;
        var changed = new List<UiElement>(_structureChanges);
        UiTreeView[] trees = [.. _treesToCheck];
        _structureChanges.Clear();
        _treesToCheck.Clear();
        if (IsTornDown || _session.IsDisposed || (changed.Count == 0 && trees.Length == 0))
            return;

        CleanDeadPeers();

        foreach (UiTreeView tree in trees)
        {
            if (tree.IsDisposed || tree.Session != _session || !_treeRows.TryGetValue(tree.SemanticId, out string? before))
                continue;
            string now = RowsInView(tree);
            if (now == before)
                continue;
            _treeRows[tree.SemanticId] = now;
            if (!changed.Contains(tree))
                changed.Add(tree);
        }

        var targets = new List<UiElement>();
        foreach (UiElement element in changed)
        {
            bool reached = ReferenceEquals(element, _root)
                || (_elementPeers.TryGetValue(element.SemanticId, out WindowsElementAutomationPeer? peer) && peer.IsAlive);
            if (reached && !targets.Contains(element))
                targets.Add(element);
        }
        targets.RemoveAll(element => targets.Exists(other => !ReferenceEquals(other, element) && element.IsDescendantOf(other)));
        if (targets.Count == 0)
            return;

        var providers = targets.ConvertAll<IRawElementProviderSimple>(element => ReferenceEquals(element, _root) ? this : _elementPeers[element.SemanticId]);
        StructureInvalidated?.Invoke(providers);
        if (!ClientsListening())
            return;

        foreach (IRawElementProviderSimple provider in providers)
            UiaNative.UiaRaiseStructureChangedEvent(NativeProviderAdapter.For(provider)!, StructureChangeType.ChildrenInvalidated, null, 0);
    }

    // The selection is the selected item or tab id: rows inserted above it change its index, not the selection.
    // Expansion is null for an element that does not expand, Toggle for one that does not toggle.
    private readonly record struct AutomationSnapshot(string Name, bool IsEnabled, string? Text, int SelectionStart, int SelectionLength, string? SelectedId,
        ExpandCollapseState? Expansion, bool IsDataValid, string? Description, ToggleState? Toggle);

    private static AutomationSnapshot Capture(UiElement element, WindowsElementAutomationPeer peer)
    {
        UiSemanticNode node = element.GetSemanticNode();
        UiSemanticTextInfo? text = node.TextInfo;
        string? value = peer.TextValue;
        return new(
            peer.GetPropertyValue(UiaNative.UiaNamePropertyId) as string ?? string.Empty,
            node.State.HasFlag(UiSemanticState.Enabled),
            value,
            value is null ? 0 : text?.SelectionLength > 0 ? text.SelectionStart : text?.CaretIndex ?? 0,
            value is null ? 0 : text?.SelectionLength ?? 0,
            element switch
            {
                UiListView list => list.SelectedItemId,
                UiTabView tabs => tabs.SelectedTab?.Id,
                UiTreeView { Selection.Count: > 0 } tree => tree.Selection[^1].Value,
                _ => null,
            },
            WindowsElementAutomationPeer.HasExpandState(node.State) ? WindowsElementAutomationPeer.ExpandStateOf(node.State) : null,
            !node.State.HasFlag(UiSemanticState.Invalid),
            string.IsNullOrWhiteSpace(node.Description) ? null : node.Description,
            WindowsElementAutomationPeer.IsToggle(node.Role) ? WindowsElementAutomationPeer.ToggleStateOf(node.State) : null);
    }

    // The ids of the rows exposed as the tree's children, in order.
    private static string RowsInView(UiTreeView tree)
    {
        List<int> rows = WindowsElementAutomationPeer.ExposedRows(tree);
        return string.Join('\n', rows.ConvertAll(index => tree.Rows[index].Id.Value));
    }

    /// <summary>A UIA event or property change detected for an element, raised only while clients listen.</summary>
    internal readonly record struct AutomationChange(IRawElementProviderSimple Target, int Id, bool IsProperty, object? OldValue = null, object? NewValue = null);

    /// <summary>Changes raised to UIA clients, for diagnostics and tests.</summary>
    internal event Action<IReadOnlyList<AutomationChange>>? ChangesRaised;

    private void RaiseChanges(UiElement element, WindowsElementAutomationPeer peer)
    {
        List<AutomationChange> changes = DetectChanges(element, peer);
        // The focus moving with the selection is raised once the hold ends, after ElementSelected.
        if (_focusHoldDepth > 0 && changes.RemoveAll(change => !change.IsProperty && change.Id == UiaNative.UiaAutomationFocusChangedEventId) > 0)
            _focusMovedWhileHeld = true;
        if (changes.Count > 0) ChangesRaised?.Invoke(changes);
        foreach (AutomationChange change in changes)
        {
            if (change.IsProperty)
                RaisePropertyChanged(change.Target, change.Id, change.OldValue, change.NewValue);
            else
                RaiseEvent(change.Target, change.Id);
        }
    }

    /// <summary>Compares the element with its last snapshot and returns only what actually changed.</summary>
    internal List<AutomationChange> DetectChanges(UiElement element, WindowsElementAutomationPeer peer)
    {
        var changes = new List<AutomationChange>();
        AutomationSnapshot now = Capture(element, peer);
        bool known = _snapshots.TryGetValue(element.SemanticId, out AutomationSnapshot before);
        _snapshots[element.SemanticId] = now;
        if (!known) return changes;

        if (before.Name != now.Name)
            changes.Add(new(peer, UiaNative.UiaNamePropertyId, true, before.Name, now.Name));
        if (before.IsEnabled != now.IsEnabled)
            changes.Add(new(peer, UiaNative.UiaIsEnabledPropertyId, true, before.IsEnabled, now.IsEnabled));
        // Password fields have no text here, so typing in them raises no value or text events.
        if (now.Text is not null && before.Text != now.Text)
        {
            changes.Add(new(peer, UiaNative.UiaValueValuePropertyId, true, before.Text ?? string.Empty, now.Text));
            changes.Add(new(peer, UiaNative.UiaText_TextChangedEventId, false));
        }
        if (now.Text is not null && (before.SelectionStart, before.SelectionLength) != (now.SelectionStart, now.SelectionLength))
            changes.Add(new(peer, UiaNative.UiaText_TextSelectionChangedEventId, false));
        if (before.Expansion != now.Expansion && now.Expansion is { } expansion)
            changes.Add(new(peer, UiaNative.UiaExpandCollapseExpandCollapseStatePropertyId, true, (int)(before.Expansion ?? ExpandCollapseState.LeafNode), (int)expansion));
        if (before.Toggle != now.Toggle && now.Toggle is { } toggle)
            changes.Add(new(peer, UiaNative.UiaToggleToggleStatePropertyId, true, (int)(before.Toggle ?? ToggleState.Off), (int)toggle));
        if (before.IsDataValid != now.IsDataValid)
            changes.Add(new(peer, AutomationInterop.IsDataValidForFormPropertyId, true, before.IsDataValid, now.IsDataValid));
        if (before.Description != now.Description)
            changes.Add(new(peer, AutomationInterop.FullDescriptionPropertyId, true, before.Description ?? string.Empty, now.Description ?? string.Empty));
        if (before.SelectedId != now.SelectedId && now.SelectedId is { } selectedId)
        {
            IRawElementProviderSimple? selected = element switch
            {
                UiListView list when list.IndexOf(selectedId) >= 0 => ItemPeer(list, selectedId),
                UiTabView tabs when WindowsElementAutomationPeer.IndexOfTab(tabs, selectedId) >= 0 => TabPeer(tabs, selectedId),
                UiTreeView tree when WindowsElementAutomationPeer.IsRowExposed(tree, WindowsElementAutomationPeer.IndexOfRow(tree, new TreeNodeId(selectedId))) => TreeRowPeer(tree, new TreeNodeId(selectedId)),
                _ => null,
            };
            if (selected is not null)
            {
                changes.Add(new(selected, UiaNative.UiaSelectionItem_ElementSelectedEventId, false));
                // In a focused container the focus moves with the selection, as GetFocus reports it.
                if (ReferenceEquals(_session.FocusedElement, element) && ReferenceEquals(FocusTarget(), selected))
                    changes.Add(new(selected, UiaNative.UiaAutomationFocusChangedEventId, false));
            }
        }

        if (element is UiTreeView changedTree)
        {
            // Rows a client holds report their own expand state; rows shown or hidden are a structure change.
            foreach (((long treeId, string _), WindowsElementAutomationPeer row) in _treeRowPeers)
            {
                if (treeId != changedTree.SemanticId || !row.IsAlive || row.ExpandCollapseState == row.LastReportedExpansion)
                    continue;
                changes.Add(new(row, UiaNative.UiaExpandCollapseExpandCollapseStatePropertyId, true, (int)row.LastReportedExpansion, (int)row.ExpandCollapseState));
                row.LastReportedExpansion = row.ExpandCollapseState;
            }
        }

        return changes;
    }

    private static bool _notificationsUnavailable;

    /// <summary>Raises a notification event; false where UIA predates them (before Windows 10 1709).</summary>
    private static bool RaiseNotification(IRawElementProviderSimple target, StatusNotification notification)
    {
        if (_notificationsUnavailable) return false;
        try
        {
            UiaNative.UiaRaiseNotificationEvent(NativeProviderAdapter.For(target)!, NotificationKind.Other, notification.Processing, notification.Text, notification.ActivityId);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            _notificationsUnavailable = true;
            return false;
        }
    }

    private static void RaisePropertyChanged(IRawElementProviderSimple provider, int propertyId, object? oldValue, object? newValue)
    {
        using var oldVar = AutomationMarshalling.ToVariant(oldValue);
        using var newVar = AutomationMarshalling.ToVariant(newValue);
        UiaNative.UiaRaiseAutomationPropertyChangedEvent(NativeProviderAdapter.For(provider)!, propertyId, AutomationVariant.From(oldVar), AutomationVariant.From(newVar));
    }

    /// <summary>Drops the peers of elements, items and tabs that are gone, and disconnects their wrappers from UIA.</summary>
    internal void CleanDeadPeers()
    {
        if (IsTornDown) return;
        RemoveDeadPeers(_elementPeers, key =>
        {
            _snapshots.Remove(key);
            _treeRows.Remove(key);
        });
        RemoveDeadPeers(_itemPeers);
        RemoveDeadPeers(_tabPeers);
        RemoveDeadPeers(_treeRowPeers);
    }

    private void RemoveDeadPeers<TKey>(Dictionary<TKey, WindowsElementAutomationPeer> peers, Action<TKey>? removed = null) where TKey : notnull
    {
        List<TKey>? dead = null;
        foreach ((TKey key, WindowsElementAutomationPeer peer) in peers)
        {
            if (!peer.IsAlive) (dead ??= []).Add(key);
        }
        if (dead is null) return;

        foreach (TKey key in dead)
        {
            Disconnect(peers[key]);
            peers.Remove(key);
            removed?.Invoke(key);
        }
    }

    // --- IRawElementProviderSimple ---

    // ProviderOwnsSetFocus is not declared, here or on the peers: every element lives in this window,
    // so UIA giving the window the Win32 focus before it calls SetFocus is what lets the session's
    // focused element receive the keys, as Win32 and WinUI controls hosted in one window rely on.
    public ProviderOptions ProviderOptions =>
        ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;

    // The native adapter obtains the HWND host provider without a runtime COM wrapper.
    public IRawElementProviderSimple? HostRawElementProvider => null;

    public object? GetPatternProvider(int patternId) => null;

    public object? GetPropertyValue(int propertyId) => propertyId switch
    {
        UiaNative.UiaControlTypePropertyId => UiaNative.UiaPaneControlTypeId,
        // The host application's root element names itself (for example by its window title).
        UiaNative.UiaNamePropertyId => _root.GetSemanticNode().Name ?? string.Empty,
        UiaNative.UiaAutomationIdPropertyId => _root.SemanticId.ToString(),
        UiaNative.UiaNativeWindowHandlePropertyId => _hwnd,
        UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
        UiaNative.UiaIsEnabledPropertyId => true,
        UiaNative.UiaIsKeyboardFocusablePropertyId => true,
        // UIA reads either property left unanswered as false, which would call the window's pane invalid.
        AutomationInterop.IsRequiredForFormPropertyId => _root.GetSemanticNode().State.HasFlag(UiSemanticState.Required),
        AutomationInterop.IsDataValidForFormPropertyId => !_root.GetSemanticNode().State.HasFlag(UiSemanticState.Invalid),
        _ => null,
    };

    // --- IRawElementProviderFragment ---

    public IRawElementProviderFragmentRoot FragmentRoot => this;

    public UiaRect BoundingRectangle => GetScreenRect(_root.Bounds);

    // A fragment root hosted in a window returns none: UIA uses the window's own runtime ID, and prefixes
    // every peer's appended ID with it. Without a window there is no host, so the root appends its own.
    public int[]? GetRuntimeId() => _hwnd != nint.Zero ? null : [AutomationInterop.AppendRuntimeId, _rootRuntimeId];

    public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;

    public void SetFocus() => _session.SetFocus(_root);

    public IRawElementProviderFragment? Navigate(NavigateDirection direction) => direction switch
    {
        NavigateDirection.FirstChild => GetFirstChild(_root),
        NavigateDirection.LastChild => GetLastChild(_root),
        _ => null,
    };

    private IRawElementProviderFragment? GetFirstChild(UiElement parent)
    {
        if (parent is UiTreeView tree)
            return WindowsElementAutomationPeer.FirstTreeRow(this, tree);

        if (parent is UiListView lv && lv.Items.Count > 0)
            return GetOrCreateItemPeer(lv, 0);

        if (parent is UiTabView tv && tv.Tabs.Count > 0)
            return GetOrCreateTabPeer(tv, 0);

        foreach (UiElement child in parent.Children)
        {
            if (AutomationExposure.IsExposed(child))
                return GetOrCreatePeer(child);
        }
        return null;
    }

    private IRawElementProviderFragment? GetLastChild(UiElement parent)
    {
        if (parent is UiTreeView tree)
            return WindowsElementAutomationPeer.LastTreeRow(this, tree);

        if (parent is UiListView lv && lv.Items.Count > 0)
            return GetOrCreateItemPeer(lv, lv.Items.Count - 1);

        if (parent is UiTabView tv)
        {
            for (int i = tv.Children.Count - 1; i >= 0; i--)
            {
                if (AutomationExposure.IsExposed(tv.Children[i]))
                    return GetOrCreatePeer(tv.Children[i]);
            }
            if (tv.Tabs.Count > 0)
                return GetOrCreateTabPeer(tv, tv.Tabs.Count - 1);
        }

        for (int i = parent.Children.Count - 1; i >= 0; i--)
        {
            UiElement child = parent.Children[i];
            if (AutomationExposure.IsExposed(child))
                return GetOrCreatePeer(child);
        }
        return null;
    }

    // --- IRawElementProviderFragmentRoot ---

    public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y)
    {
        double scale = _scaleProvider();
        double dipX = x;
        double dipY = y;

        if (_hwnd != nint.Zero)
        {
            var pt = new WindowNative.POINT { X = (int)x, Y = (int)y };
            WindowNative.ScreenToClient(_hwnd, ref pt);
            dipX = pt.X / scale;
            dipY = pt.Y / scale;
        }

        var point = new BPoint(dipX, dipY);
        // The session's own hit test: it respects overlays, such as an open drop-down drawn over the
        // fields below it, and what containers clip, such as a field scrolled out under an action bar.
        UiElement? hit = _session.HitTest(point);
        // Another root of the session is not this bridge's to describe, and what it draws over this one
        // hides what lies below; the window answers for that point.
        if (hit is not null && !ReferenceEquals(hit, _root) && !hit.IsDescendantOf(_root))
            return this;
        while (hit is not null && !ReferenceEquals(hit, _root) && !AutomationExposure.IsExposed(hit))
            hit = hit.Parent;
        if (hit is null || ReferenceEquals(hit, _root)) return this;

        if (hit is UiListView lv && ItemAt(lv, point) is { } item) return item;
        if (hit is UiTabView tv && TabAt(tv, point) is { } tab) return tab;
        if (hit is UiTreeView tree && RowAt(tree, point) is { } row) return row;
        return GetOrCreatePeer(hit);
    }

    // The row under the point, by the same geometry the item peers report.
    private WindowsElementAutomationPeer? ItemAt(UiListView list, BPoint point)
    {
        double rowHeight = WindowsElementAutomationPeer.GetItemHeight(list);
        if (rowHeight <= 0 || list.Items.Count == 0) return null;
        double top = list is StandardListView standard ? standard.ContentBounds.Top : list.Bounds.Top;
        int guess = (int)Math.Floor((point.Y - top + list.VerticalOffset) / rowHeight);
        for (int index = Math.Max(0, guess - 1); index <= Math.Min(list.Items.Count - 1, guess + 1); index++)
        {
            WindowsElementAutomationPeer peer = ItemPeer(list, list.Items[index].Id);
            if (peer.VisibleBounds.Contains(point)) return peer;
        }
        return null;
    }

    // Only rows in view can be hit, by the geometry their peers report.
    private WindowsElementAutomationPeer? RowAt(UiTreeView tree, BPoint point)
    {
        // Other trees report no row geometry, and answer for themselves.
        if (tree is not StandardTreeView) return null;
        (int first, int end) = WindowsElementAutomationPeer.VisibleRows(tree);
        for (int index = first; index < end; index++)
        {
            if (WindowsElementAutomationPeer.RowBounds(tree, index).Contains(point))
                return TreeRowPeer(tree, tree.Rows[index].Id);
        }
        return null;
    }

    // Only a header the view really draws can be hit; a view without header geometry answers for itself.
    private WindowsElementAutomationPeer? TabAt(UiTabView tabs, BPoint point)
    {
        for (int index = 0; index < tabs.Tabs.Count && index < tabs.VisibleTabCapacity; index++)
        {
            if (!tabs.GetTabHeaderBounds(index).IsEmpty && WindowsElementAutomationPeer.TabBounds(tabs, index).Contains(point))
                return TabPeer(tabs, tabs.Tabs[index].Id);
        }
        return null;
    }

    public IRawElementProviderFragment? GetFocus() => FocusTarget();

    /// <summary>
    /// The provider that has the keyboard focus: the selected row or tab of a focused list, tab view
    /// or tree, which have no focus apart from their selection, or the focused element; the root when
    /// nothing has the focus. Focus events go to the same provider.
    /// </summary>
    internal IRawElementProviderFragment FocusTarget() => FocusedItem() switch
    {
        (null, _) => this,
        (UiListView lv, { } itemId) => ItemPeer(lv, itemId),
        (UiTabView tv, { } tabId) => TabPeer(tv, tabId),
        (UiTreeView tree, { } node) => TreeRowPeer(tree, new TreeNodeId(node)),
        (UiElement focused, _) => GetOrCreatePeer(focused),
    };

    // What FocusTarget names, without making a peer for it: the focused element, and the id of the row,
    // tab or tree row the focus is on inside it.
    private (UiElement? Element, string? Item) FocusedItem() => _session.FocusedElement switch
    {
        null => (null, null),
        UiListView lv when lv.SelectedItemId is { } itemId && lv.IndexOf(itemId) >= 0 => (lv, itemId),
        UiTabView tv when tv.SelectedTab is { } tab => (tv, tab.Id),
        UiTreeView tree when !tree.FocusedNode.IsNone && WindowsElementAutomationPeer.IndexOfRow(tree, tree.FocusedNode) >= 0 => (tree, tree.FocusedNode.Value),
        UiElement focused => (focused, null),
    };

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _session.SemanticChanged -= OnSemanticChanged;
        RemoveSubclass();
        ReleaseProviders();
    }
}
