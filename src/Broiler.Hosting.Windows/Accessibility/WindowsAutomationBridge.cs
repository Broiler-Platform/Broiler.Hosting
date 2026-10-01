using System;
using System.Collections.Generic;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.ListView;
using Broiler.UI.TabView;
using Broiler.UI.TabView.Standard;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>
/// Bridge between <see cref="UiSession"/> and Windows UI Automation.
/// Subclasses the native window to intercept <c>WM_GETOBJECT</c>, exposes the semantic root
/// via <see cref="IRawElementProviderFragmentRoot"/>, and propagates accessibility events.
/// </summary>
public sealed class WindowsAutomationBridge : IRawElementProviderFragmentRoot, IDisposable
{
    private static nuint _subclassCounter;
    private readonly nuint _subclassId;
    private readonly UiaNative.SubclassProc? _subclassProc;

    private readonly nint _hwnd;
    private readonly UiSession _session;
    private readonly UiElement _root;
    private readonly Func<double> _scaleProvider;

    private readonly Dictionary<long, WindowsElementAutomationPeer> _elementPeers = new();
    private readonly Dictionary<(long ListViewId, int Index), WindowsElementAutomationPeer> _itemPeers = new();
    private readonly Dictionary<(long TabViewId, int Index), WindowsElementAutomationPeer> _tabPeers = new();

    private bool _isDisposed;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;

    internal T OnUiThread<T>(Func<T> operation)
    {
        T Run()
        {
            if (_isDisposed) throw new System.Runtime.InteropServices.COMException("Element is no longer available.", unchecked((int)0x80040201));
            return operation();
        }
        if (Environment.CurrentManagedThreadId == _uiThread) return Run();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        _session.Dispatcher.Post(() =>
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
            try { completion.TrySetResult(Run()); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        try { return completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        finally { Interlocked.CompareExchange(ref started, 2, 0); }
    }

    public WindowsAutomationBridge(nint hwnd, UiSession session, UiElement root, Func<double>? scaleProvider = null)
    {
        _hwnd = hwnd;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _scaleProvider = scaleProvider ?? (() => 1.0);

        _session.SemanticChanged += OnSemanticChanged;

        if (_hwnd != nint.Zero)
        {
            _subclassId = ++_subclassCounter;
            _subclassProc = SubclassWindowProc;
            UiaNative.SetWindowSubclass(_hwnd, _subclassProc, _subclassId, 0);
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

    private nint SubclassWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == UiaNative.WmGetObject && ((int)lParam == UiaNative.UiaRootObjectId || unchecked((uint)(long)lParam) == 0xFFFFFFE7))
        {
            return UiaNative.UiaReturnRawElementProvider(hWnd, wParam, lParam, this);
        }

        return UiaNative.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public WindowsElementAutomationPeer GetOrCreatePeer(UiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (_elementPeers.TryGetValue(element.SemanticId, out WindowsElementAutomationPeer? existing) && existing.IsAlive)
            return existing;

        var peer = new WindowsElementAutomationPeer(this, element);
        _elementPeers[element.SemanticId] = peer;
        return peer;
    }

    public WindowsElementAutomationPeer GetOrCreateItemPeer(UiListView listView, int index)
    {
        ArgumentNullException.ThrowIfNull(listView);

        var key = (listView.SemanticId, index);
        if (_itemPeers.TryGetValue(key, out WindowsElementAutomationPeer? existing) && existing.IsAlive)
            return existing;

        var peer = new WindowsElementAutomationPeer(this, listView, index);
        _itemPeers[key] = peer;
        return peer;
    }

    public WindowsElementAutomationPeer GetOrCreateTabPeer(UiTabView tabView, int index)
    {
        ArgumentNullException.ThrowIfNull(tabView);

        var key = (tabView.SemanticId, index);
        if (_tabPeers.TryGetValue(key, out WindowsElementAutomationPeer? existing) && existing.IsAlive)
            return existing;

        var peer = new WindowsElementAutomationPeer(this, tabView, index);
        _tabPeers[key] = peer;
        return peer;
    }

    public UiaRect GetScreenRect(BRect dipRect)
    {
        if (_hwnd == nint.Zero)
            return new UiaRect(dipRect.X, dipRect.Y, dipRect.Width, dipRect.Height);

        var pt = new UiaNative.POINT { X = 0, Y = 0 };
        UiaNative.ClientToScreen(_hwnd, ref pt);
        double scale = _scaleProvider();

        return new UiaRect(
            pt.X + (dipRect.X * scale),
            pt.Y + (dipRect.Y * scale),
            Math.Max(0, dipRect.Width * scale),
            Math.Max(0, dipRect.Height * scale));
    }

    private void OnSemanticChanged(object? sender, UiSemanticChangedEventArgs e)
    {
        if (!UiaNative.UiaClientsAreListening())
            return;

        switch (e.Change)
        {
            case UiSemanticChangeKind.FocusChanged when e.Element is not null:
                WindowsElementAutomationPeer focusedPeer = GetOrCreatePeer(e.Element);
                UiaNative.UiaRaiseAutomationEvent(focusedPeer, UiaNative.UiaAutomationFocusChangedEventId);
                UiaNative.UiaRaiseAutomationPropertyChangedEvent(focusedPeer, UiaNative.UiaHasKeyboardFocusPropertyId, false, true);
                break;

            case UiSemanticChangeKind.StatusAnnounced:
                IRawElementProviderSimple target = e.Element is not null ? GetOrCreatePeer(e.Element) : this;
                UiaNative.UiaRaiseAutomationEvent(target, UiaNative.UiaLiveRegionChangedEventId);
                break;

            case UiSemanticChangeKind.StateChanged when e.Element is not null:
                WindowsElementAutomationPeer statePeer = GetOrCreatePeer(e.Element);
                bool isEnabled = e.Element.GetSemanticNode().State.HasFlag(UiSemanticState.Enabled);
                UiaNative.UiaRaiseAutomationPropertyChangedEvent(statePeer, UiaNative.UiaIsEnabledPropertyId, !isEnabled, isEnabled);
                break;

            case UiSemanticChangeKind.StructureChanged or UiSemanticChangeKind.SubtreeChanged:
                CleanDeadPeers();
                UiaNative.UiaRaiseStructureChangedEvent(this, StructureChangeType.ChildrenInvalidated, null, 0);
                break;
        }
    }

    private void CleanDeadPeers()
    {
        var deadElementKeys = new List<long>();
        foreach ((long key, WindowsElementAutomationPeer peer) in _elementPeers)
        {
            if (!peer.IsAlive) deadElementKeys.Add(key);
        }
        foreach (long key in deadElementKeys) _elementPeers.Remove(key);

        var deadItemKeys = new List<(long, int)>();
        foreach (((long, int) key, WindowsElementAutomationPeer peer) in _itemPeers)
        {
            if (!peer.IsAlive) deadItemKeys.Add(key);
        }
        foreach (var key in deadItemKeys) _itemPeers.Remove(key);

        var deadTabKeys = new List<(long, int)>();
        foreach (((long, int) key, WindowsElementAutomationPeer peer) in _tabPeers)
        {
            if (!peer.IsAlive) deadTabKeys.Add(key);
        }
        foreach (var key in deadTabKeys) _tabPeers.Remove(key);
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
        UiaNative.UiaNamePropertyId => "Broiler.Mail Window",
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

    public int[]? GetRuntimeId() => [1, unchecked((int)_hwnd), (int)_root.SemanticId];

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
            if (child.Visibility == UiVisibility.Visible)
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
                if (tv.Children[i].Visibility == UiVisibility.Visible)
                    return GetOrCreatePeer(tv.Children[i]);
            }
            if (tv.Tabs.Count > 0)
                return GetOrCreateTabPeer(tv, tv.Tabs.Count - 1);
        }

        for (int i = parent.Children.Count - 1; i >= 0; i--)
        {
            UiElement child = parent.Children[i];
            if (child.Visibility == UiVisibility.Visible)
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
            var pt = new UiaNative.POINT { X = (int)x, Y = (int)y };
            UiaNative.ScreenToClient(_hwnd, ref pt);
            dipX = pt.X / scale;
            dipY = pt.Y / scale;
        }

        UiElement? hit = HitTestElement(_root, new BPoint(dipX, dipY));
        if (hit is null) return this;

        if (hit is UiListView lv && lv.Items.Count > 0)
        {
            double relativeY = dipY - lv.Bounds.Top + lv.VerticalOffset;
            double itemHeight = WindowsElementAutomationPeer.GetItemHeight(lv);
            if (itemHeight > 0)
            {
                int itemIndex = (int)(relativeY / itemHeight);
                if (itemIndex >= 0 && itemIndex < lv.Items.Count)
                    return GetOrCreateItemPeer(lv, itemIndex);
            }
        }
        else if (hit is UiTabView tv && tv.Tabs.Count > 0)
        {
            double headerHeight = (tv as StandardTabView)?.HeaderHeight ?? 32.0;
            if (dipY >= tv.Bounds.Top && dipY <= tv.Bounds.Top + headerHeight)
            {
                double tabWidth = tv.Bounds.Width / Math.Max(1, tv.Tabs.Count);
                if (tabWidth > 0)
                {
                    int tabIndex = (int)((dipX - tv.Bounds.Left) / tabWidth);
                    if (tabIndex >= 0 && tabIndex < tv.Tabs.Count)
                        return GetOrCreateTabPeer(tv, tabIndex);
                }
            }
        }

        return GetOrCreatePeer(hit);
    }

    public IRawElementProviderFragment? GetFocus()
    {
        UiElement? focused = _session.FocusedElement;
        if (focused is null) return this;

        if (focused is UiListView lv && lv.SelectedIndex >= 0 && lv.SelectedIndex < lv.Items.Count)
        {
            return GetOrCreateItemPeer(lv, lv.SelectedIndex);
        }

        if (focused is UiTabView tv && tv.SelectedIndex >= 0 && tv.SelectedIndex < tv.Tabs.Count)
        {
            return GetOrCreateTabPeer(tv, tv.SelectedIndex);
        }

        return GetOrCreatePeer(focused);
    }

    private static UiElement? HitTestElement(UiElement root, BPoint point)
    {
        if (root.Visibility != UiVisibility.Visible || !root.Bounds.Contains(point))
            return null;

        for (int i = root.Children.Count - 1; i >= 0; i--)
        {
            UiElement? childHit = HitTestElement(root.Children[i], point);
            if (childHit is not null)
                return childHit;
        }

        return root;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _session.SemanticChanged -= OnSemanticChanged;

        if (_hwnd != nint.Zero && _subclassProc is not null)
        {
            UiaNative.RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
        }

        _elementPeers.Clear();
        _itemPeers.Clear();
        _tabPeers.Clear();
    }
}
