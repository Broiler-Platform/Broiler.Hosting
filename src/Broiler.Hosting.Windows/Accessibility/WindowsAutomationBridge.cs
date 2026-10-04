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
        if (_hwnd != nint.Zero)
            AutomationInterop.UiaReleaseWindowProviders(_hwnd);

        _elementPeers.Clear();
        _itemPeers.Clear();
        _tabPeers.Clear();
        _snapshots.Clear();
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

    internal WindowsElementAutomationPeer ItemPeer(UiListView listView, string itemId)
    {
        var key = (listView.SemanticId, itemId);
        if (_itemPeers.TryGetValue(key, out WindowsElementAutomationPeer? existing))
            return existing;

        var peer = new WindowsElementAutomationPeer(this, listView, itemId);
        _itemPeers[key] = peer;
        return peer;
    }

    internal WindowsElementAutomationPeer TabPeer(UiTabView tabView, string tabId)
    {
        var key = (tabView.SemanticId, tabId);
        if (_tabPeers.TryGetValue(key, out WindowsElementAutomationPeer? existing))
            return existing;

        var peer = new WindowsElementAutomationPeer(this, tabView, tabId);
        _tabPeers[key] = peer;
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
        if (IsTornDown || !UiaNative.UiaClientsAreListening())
            return;

        switch (e.Change)
        {
            case UiSemanticChangeKind.FocusChanged when e.Element is not null:
                WindowsElementAutomationPeer focusedPeer = GetOrCreatePeer(e.Element);
                UiaNative.UiaRaiseAutomationEvent(NativeProviderAdapter.For(focusedPeer)!, UiaNative.UiaAutomationFocusChangedEventId);
                RaisePropertyChanged(focusedPeer, UiaNative.UiaHasKeyboardFocusPropertyId, false, true);
                break;

            case UiSemanticChangeKind.StatusAnnounced:
                IRawElementProviderSimple target = e.Element is not null ? GetOrCreatePeer(e.Element) : this;
                if (StatusAnnouncements.Plan(e.Element!, e.Message) is { } notification && RaiseNotification(target, notification))
                    break;
                UiaNative.UiaRaiseAutomationEvent(NativeProviderAdapter.For(target)!, UiaNative.UiaLiveRegionChangedEventId);
                break;

            // Every semantic invalidation arrives as StateChanged; only real differences become UIA events,
            // and only for elements a client has already reached (and therefore has a peer).
            case UiSemanticChangeKind.StateChanged when e.Element is not null
                && _elementPeers.TryGetValue(e.Element.SemanticId, out WindowsElementAutomationPeer? statePeer) && statePeer.IsAlive:
                RaiseChanges(e.Element, statePeer);
                break;

            case UiSemanticChangeKind.StructureChanged or UiSemanticChangeKind.SubtreeChanged:
                CleanDeadPeers();
                UiaNative.UiaRaiseStructureChangedEvent(NativeProviderAdapter.For(this)!, StructureChangeType.ChildrenInvalidated, null, 0);
                break;
        }
    }

    // The selection is the selected item or tab id: rows inserted above it change its index, not the selection.
    private readonly record struct AutomationSnapshot(string Name, bool IsEnabled, string? Text, int SelectionStart, int SelectionLength, string? SelectedId);

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
            element switch { UiListView list => list.SelectedItemId, UiTabView tabs => tabs.SelectedTab?.Id, _ => null });
    }

    /// <summary>A UIA event or property change detected for an element, raised only while clients listen.</summary>
    internal readonly record struct AutomationChange(IRawElementProviderSimple Target, int Id, bool IsProperty, object? OldValue = null, object? NewValue = null);

    /// <summary>Changes raised to UIA clients, for diagnostics and tests.</summary>
    internal event Action<IReadOnlyList<AutomationChange>>? ChangesRaised;

    private void RaiseChanges(UiElement element, WindowsElementAutomationPeer peer)
    {
        List<AutomationChange> changes = DetectChanges(element, peer);
        if (changes.Count > 0) ChangesRaised?.Invoke(changes);
        foreach (AutomationChange change in changes)
        {
            if (change.IsProperty)
                RaisePropertyChanged(change.Target, change.Id, change.OldValue, change.NewValue);
            else
                UiaNative.UiaRaiseAutomationEvent(NativeProviderAdapter.For(change.Target)!, change.Id);
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
        if (before.SelectedId != now.SelectedId && now.SelectedId is { } selectedId)
        {
            IRawElementProviderSimple? selected = element switch
            {
                UiListView list when list.IndexOf(selectedId) >= 0 => ItemPeer(list, selectedId),
                UiTabView tabs when WindowsElementAutomationPeer.IndexOfTab(tabs, selectedId) >= 0 => TabPeer(tabs, selectedId),
                _ => null,
            };
            if (selected is not null)
                changes.Add(new(selected, UiaNative.UiaSelectionItem_ElementSelectedEventId, false));
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
        RemoveDeadPeers(_elementPeers, key => _snapshots.Remove(key));
        RemoveDeadPeers(_itemPeers);
        RemoveDeadPeers(_tabPeers);
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
        while (hit is not null && !ReferenceEquals(hit, _root) && !AutomationExposure.IsExposed(hit))
            hit = hit.Parent;
        if (hit is null || ReferenceEquals(hit, _root)) return this;

        if (hit is UiListView lv && ItemAt(lv, point) is { } item) return item;
        if (hit is UiTabView tv && TabAt(tv, point) is { } tab) return tab;
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

    public IRawElementProviderFragment? GetFocus()
    {
        UiElement? focused = _session.FocusedElement;
        if (focused is null) return this;

        if (focused is UiListView lv && lv.SelectedItemId is { } itemId && lv.IndexOf(itemId) >= 0)
        {
            return ItemPeer(lv, itemId);
        }

        if (focused is UiTabView tv && tv.SelectedTab is { } tab)
        {
            return TabPeer(tv, tab.Id);
        }

        return GetOrCreatePeer(focused);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _session.SemanticChanged -= OnSemanticChanged;
        RemoveSubclass();
        ReleaseProviders();
    }
}
