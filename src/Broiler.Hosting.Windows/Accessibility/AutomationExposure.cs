using System;
using Broiler.UI;
using Broiler.UI.Edit;
using Broiler.UI.RichEdit;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>
/// What the provider exposes and how elements are named. Kept in one place so the bridge and every
/// peer navigate, hit-test, and name elements the same way.
/// </summary>
internal static class AutomationExposure
{
    /// <summary>
    /// An element is exposed while it is visible and no container has hidden it, or an ancestor, from
    /// assistive technology. Tab views keep inactive content attached (and visible to layout), so
    /// visibility alone cannot tell.
    /// </summary>
    public static bool IsExposed(UiElement element) =>
        element.Visibility == UiVisibility.Visible && !element.IsHiddenFromAccessibility;

    /// <summary>
    /// Unnamed layout containers are not controls. UIA's Control view then skips them and shows their
    /// children directly, instead of reading "pane" for every panel.
    /// </summary>
    public static bool IsLayoutOnly(UiElement element, UiSemanticNode node, string name) =>
        node.Role is UiSemanticRole.Generic or UiSemanticRole.Panel or UiSemanticRole.ScrollView
        && !element.Focusable
        && name.Length == 0;

    /// <summary>
    /// The UIA name. Broiler.UI already resolves it: an explicit <see cref="UiElement.AccessibleName"/>,
    /// then the <see cref="UiElement.LabeledBy"/> label, then the name the control derives itself (a
    /// text control's placeholder, never its value). Layout elements have an empty name.
    /// </summary>
    public static string Name(UiSemanticNode node) => node.Name ?? string.Empty;

    /// <summary>Help text is the placeholder hint, never the control's value; it is omitted when it is already the name.</summary>
    public static string HelpText(UiElement element, string name) =>
        Placeholder(element) is { Length: > 0 } placeholder && placeholder != name ? placeholder : string.Empty;

    /// <summary>
    /// Whether a related element (what this one controls, its error message or description) can be
    /// offered to a client: in the same session and not disposed, it and every ancestor visible, not
    /// hidden from assistive technology, and not an ancestor of the element, which already contains it
    /// and is no place to send a reader (Broiler.UI ADR 0028).
    /// </summary>
    public static bool IsShownRelation(UiElement element, UiElement? related)
    {
        if (related is null || related.IsDisposed || related.Session is null || related.Session != element.Session
            || ReferenceEquals(related, element) || element.IsDescendantOf(related) || related.IsHiddenFromAccessibility)
            return false;

        for (UiElement? current = related; current is not null; current = current.Parent)
        {
            if (current.Visibility != UiVisibility.Visible)
                return false;
        }
        return true;
    }

    public static bool IsTextControl(UiElement element, UiSemanticNode node) =>
        node.Role is UiSemanticRole.Edit or UiSemanticRole.RichEdit || element is UiEdit or UiRichEdit;

    private static string? Placeholder(UiElement element) => element switch
    {
        UiEdit edit => edit.PlaceholderText,
        UiRichEdit richEdit => richEdit.PlaceholderText,
        _ => null,
    };
}
