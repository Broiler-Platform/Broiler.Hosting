using System;
using System.Globalization;
using System.Linq;
using Broiler.UI;
using Broiler.UI.Edit;
using Broiler.UI.Label;
using Broiler.UI.RichEdit;
using Broiler.UI.TabView;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>
/// What the provider exposes and how elements are named. Kept in one place so the bridge and every
/// peer navigate, hit-test, and name elements the same way.
/// </summary>
internal static class AutomationExposure
{
    /// <summary>An element is exposed while it is visible and not inactive content kept alive by a container.</summary>
    public static bool IsExposed(UiElement element) =>
        element.Visibility == UiVisibility.Visible && !IsHiddenByContainer(element);

    /// <summary>
    /// True for the content of an unselected tab, or anything inside it. Tab views keep that content
    /// attached (and, by default, visible to layout), so visibility alone cannot tell.
    /// </summary>
    /// <remarks>Broiler.UI with ADR 0027 publishes <c>UiElement.IsHiddenFromAccessibility</c> for any container; use it once consumed.</remarks>
    public static bool IsHiddenByContainer(UiElement element)
    {
        for (UiElement current = element; current.Parent is { } parent; current = parent)
        {
            if (parent is UiTabView tabs
                && tabs.Tabs.Any(tab => ReferenceEquals(tab.Content, current))
                && !ReferenceEquals(tabs.SelectedTab?.Content, current))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Unnamed layout containers are not controls. UIA's Control view then skips them and shows their
    /// children directly, instead of reading "pane" for every panel.
    /// </summary>
    public static bool IsLayoutOnly(UiElement element, UiSemanticNode node, string name) =>
        node.Role is UiSemanticRole.Generic or UiSemanticRole.Panel or UiSemanticRole.ScrollView
        && !element.Focusable
        && name.Length == 0;

    /// <summary>
    /// The UIA name. Takes the semantic name, but corrects two habits of earlier Broiler.UI versions:
    /// layout elements named by their type name (or a scroll view by its offset), and text controls
    /// named by their own text. A text control is then named by the label that targets it, or by its
    /// placeholder. With Broiler.UI that implements ADR 0027 the semantic name is already correct and
    /// passes through unchanged.
    /// </summary>
    public static string Name(UiElement element, UiSemanticNode node, UiLabel? label)
    {
        string name = node.Name ?? string.Empty;
        if (name == element.GetType().Name || (node.Role == UiSemanticRole.ScrollView && IsOffsetName(name)))
            name = string.Empty;

        if (IsTextControl(element, node))
        {
            string? value = node.TextInfo?.Value;
            string? placeholder = Placeholder(element);
            bool derived = name.Length == 0
                || (value is { Length: > 0 } && name == value)
                || name == placeholder
                || (element is UiEdit { IsPassword: true } && name == "Password field");
            if (derived && label is not null && label.DisplayText.Trim().Length > 0)
                return label.DisplayText.Trim();
            if (value is { Length: > 0 } && name == value)
                return placeholder ?? string.Empty;
        }

        return name;
    }

    /// <summary>Help text is the placeholder hint, never the control's value; it is omitted when it is already the name.</summary>
    public static string HelpText(UiElement element, string name) =>
        Placeholder(element) is { Length: > 0 } placeholder && placeholder != name ? placeholder : string.Empty;

    public static bool IsTextControl(UiElement element, UiSemanticNode node) =>
        node.Role is UiSemanticRole.Edit or UiSemanticRole.RichEdit || element is UiEdit or UiRichEdit;

    /// <summary>The label whose target is <paramref name="element"/>, searched from <paramref name="root"/>.</summary>
    public static UiLabel? FindLabel(UiElement root, UiElement element)
    {
        if (root is UiLabel label && ReferenceEquals(label.Target, element) && !label.IsDisposed)
            return label;
        foreach (UiElement child in root.Children)
        {
            if (FindLabel(child, element) is { } found)
                return found;
        }

        return null;
    }

    private static string? Placeholder(UiElement element) => element switch
    {
        UiEdit edit => edit.PlaceholderText,
        UiRichEdit richEdit => richEdit.PlaceholderText,
        _ => null,
    };

    private static bool IsOffsetName(string name)
    {
        string[] parts = name.Split(',');
        return parts.Length == 2 && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || double.TryParse(part, NumberStyles.Float, CultureInfo.CurrentCulture, out _));
    }
}
