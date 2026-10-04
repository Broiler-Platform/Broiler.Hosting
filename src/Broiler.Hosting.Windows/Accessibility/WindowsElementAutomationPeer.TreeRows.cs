using System;
using System.Collections.Generic;
using Broiler.Graphics.Geometry;
using Broiler.Native.Windows.Accessibility;
using Broiler.UI;
using Broiler.UI.TreeView;
using Broiler.UI.TreeView.Standard;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>
/// Tree rows as UIA tree items. Broiler.UI describes only the rows in view, as a flat list whose names
/// carry the level and position (ADR 0023); semantic child i is <c>Rows[FirstVisibleRow + i]</c>
/// (ADR 0028). A row peer is keyed by its node id, so it follows the node when rows above it expand or
/// collapse, and expands and collapses through <see cref="UiTreeView.Expand"/> and <see cref="UiTreeView.Collapse"/>.
/// </summary>
public sealed partial class WindowsElementAutomationPeer
{
    private readonly WeakReference<UiTreeView>? _treeViewRef;
    private readonly TreeNodeId? _treeNode;

    internal WindowsElementAutomationPeer(WindowsAutomationBridge bridge, UiTreeView treeView, TreeNodeId node)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _treeViewRef = new WeakReference<UiTreeView>(treeView ?? throw new ArgumentNullException(nameof(treeView)));
        _treeNode = node;
        RuntimeIdValue = bridge.AllocateRuntimeId();
        LastReportedExpansion = ExpandCollapseState;
    }

    internal bool IsTreeItem => _treeNode is not null;

    internal UiTreeView? TreeView => _treeViewRef is not null && _treeViewRef.TryGetTarget(out UiTreeView? tree) ? tree : null;

    internal TreeNodeId? TreeNode => _treeNode;

    /// <summary>The row's index in <see cref="UiTreeView.Rows"/>, resolved on every call; -1 once the node has no row.</summary>
    internal int RowIndex => _treeNode is { } node && TreeView is { } tree ? IndexOfRow(tree, node) : -1;

    /// <summary>The expand state last reported to clients, so a change raises one property-change event.</summary>
    internal ExpandCollapseState LastReportedExpansion { get; set; }

    internal static int IndexOfRow(UiTreeView tree, TreeNodeId node)
    {
        IReadOnlyList<TreeRow> rows = tree.Rows;
        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index].Id == node)
                return index;
        }
        return -1;
    }

    /// <summary>The rows Broiler.UI describes: the visible slice, as [First, End).</summary>
    internal static (int First, int End) ExposedRows(UiTreeView tree)
    {
        int count = tree.Rows.Count;
        int first = Math.Clamp(tree.FirstVisibleRow, 0, count);
        return (first, Math.Min(count, first + Math.Max(0, tree.VisibleRowCapacity)));
    }

    /// <summary>
    /// Where a row in view is drawn: a <see cref="StandardTreeView"/> stacks rows of
    /// <see cref="StandardTreeView.RowHeight"/> from the top of its content area. Other trees report no
    /// row geometry, so a row is placed on the whole tree, as its semantic node is.
    /// </summary>
    internal static BRect RowBounds(UiTreeView tree, int index)
    {
        (int first, int end) = ExposedRows(tree);
        if (index < first || index >= end) return BRect.Empty;
        BRect visible = tree.GetVisibleBounds();
        if (tree is not StandardTreeView { RowHeight: > 0 } standard) return visible;

        BRect content = standard.ContentBounds;
        var row = new BRect(content.Left, content.Top + ((index - first) * standard.RowHeight), content.Width, standard.RowHeight);
        return row.Intersect(content).Intersect(visible);
    }

    private bool TreeRowIsAlive => TreeView is { } tree && IsAttached(tree) && RowIndex >= 0;

    private TreeRow? CurrentRow => TreeView is { } tree && RowIndex is var index and >= 0 ? tree.Rows[index] : null;

    private object? TreeRowPattern(int patternId) => patternId switch
    {
        UiaNative.UiaSelectionItemPatternId => this,
        UiaNative.UiaScrollItemPatternId => this,
        UiaNative.UiaExpandCollapsePatternId when CurrentRow is { HasChildren: true } => this,
        _ => null,
    };

    private object? TreeRowProperty(int propertyId)
    {
        UiTreeView? tree = TreeView;
        int index = RowIndex;
        if (tree is null || index < 0) return null;
        TreeNodeId node = tree.Rows[index].Id;

        return propertyId switch
        {
            UiaNative.UiaControlTypePropertyId => AutomationInterop.TreeItemControlTypeId,
            UiaNative.UiaNamePropertyId => RowName(tree, index),
            UiaNative.UiaAutomationIdPropertyId => $"node_{node.Value}",
            UiaNative.UiaIsEnabledPropertyId => tree.GetSemanticNode().State.HasFlag(UiSemanticState.Enabled),
            UiaNative.UiaIsKeyboardFocusablePropertyId => CanHoldFocus(tree, tree.FocusedNode == node),
            UiaNative.UiaHasKeyboardFocusPropertyId => tree.FocusedNode == node && _bridge.Session.FocusedElement == tree,
            UiaNative.UiaIsOffscreenPropertyId => VisibleBounds.IsEmpty,
            UiaNative.UiaBoundingRectanglePropertyId => BoundingRectangle,
            _ => null,
        };
    }

    // The tree's own description of the row (label, decoration, state, level and position) while it is
    // in view; the data source's label otherwise.
    private static string RowName(UiTreeView tree, int index)
    {
        (int first, int end) = ExposedRows(tree);
        if (index >= first && index < end && tree.GetSemanticNode().Children is { } children && index - first < children.Count
            && children[index - first].Name is { Length: > 0 } name)
            return name;
        return tree.DataSource?.GetPresentation(tree.Rows[index].Id).Label ?? string.Empty;
    }

    private IRawElementProviderFragment? TreeRowNavigate(NavigateDirection direction)
    {
        UiTreeView? tree = TreeView;
        int index = RowIndex;
        if (tree is null || index < 0) return null;
        (int first, int end) = ExposedRows(tree);

        return direction switch
        {
            NavigateDirection.Parent => _bridge.GetOrCreatePeer(tree),
            NavigateDirection.NextSibling when index >= first && index + 1 < end => _bridge.TreeRowPeer(tree, tree.Rows[index + 1].Id),
            NavigateDirection.PreviousSibling when index > first && index < end => _bridge.TreeRowPeer(tree, tree.Rows[index - 1].Id),
            _ => null,
        };
    }

    internal static IRawElementProviderFragment? FirstTreeRow(WindowsAutomationBridge bridge, UiTreeView tree)
    {
        (int first, int end) = ExposedRows(tree);
        return first < end ? bridge.TreeRowPeer(tree, tree.Rows[first].Id) : null;
    }

    internal static IRawElementProviderFragment? LastTreeRow(WindowsAutomationBridge bridge, UiTreeView tree)
    {
        (int first, int end) = ExposedRows(tree);
        return first < end ? bridge.TreeRowPeer(tree, tree.Rows[end - 1].Id) : null;
    }

    private ExpandCollapseState TreeRowExpansion => CurrentRow switch
    {
        { HasChildren: true, IsExpanded: true } => ExpandCollapseState.Expanded,
        { HasChildren: true } => ExpandCollapseState.Collapsed,
        _ => ExpandCollapseState.LeafNode,
    };

    private void ExpandTreeRow(bool expand)
    {
        UiTreeView tree = TreeView!;
        if (CurrentRow is not { HasChildren: true } row)
            throw new InvalidOperationException("The row has no children to show or hide.");
        if (!tree.GetSemanticNode().State.HasFlag(UiSemanticState.Enabled))
            throw AutomationInterop.ElementNotEnabledException();
        if (expand) tree.Expand(row.Id);
        else tree.Collapse(row.Id);
    }

    private bool TreeRowIsSelected => TreeView is { } tree && _treeNode is { } node && tree.Selection.Contains(node);

    private void ChangeTreeSelection(bool add)
    {
        UiTreeView tree = TreeView!;
        TreeNodeId node = _treeNode!.Value;
        var selection = new List<TreeNodeId>(tree.Selection);
        if (add)
        {
            // A click selects just the row; adding keeps the others only where the tree allows several.
            if (tree.SelectionMode != TreeSelectionMode.Extended) selection.Clear();
            if (!selection.Contains(node)) selection.Add(node);
        }
        else
        {
            selection.Remove(node);
        }
        tree.SetSelection(selection);
    }

    private void ScrollTreeRowIntoView()
    {
        UiTreeView tree = TreeView!;
        int index = RowIndex;
        if (index < tree.FirstVisibleRow)
            tree.FirstVisibleRow = index;
        else if (index >= tree.FirstVisibleRow + tree.VisibleRowCapacity)
            tree.FirstVisibleRow = index - tree.VisibleRowCapacity + 1;
    }
}
