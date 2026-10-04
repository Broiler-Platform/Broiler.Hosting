using Broiler.Native.Windows.Accessibility;
using Broiler.UI;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>A status announcement as a UIA notification: the text to read and how it queues.</summary>
internal readonly record struct StatusNotification(NotificationProcessing Processing, string Text, string ActivityId);

/// <summary>
/// How <see cref="UiSession.AnnounceStatus"/> reaches UIA clients. The announced text travels in a
/// notification event, so a client reads the status itself rather than the name of the element that
/// raised it. Each source element has its own activity, so a newer status from the same element
/// replaces one still queued instead of being read after it; an error is read before other speech.
/// </summary>
internal static class StatusAnnouncements
{
    public static StatusNotification? Plan(UiElement source, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        bool error = source.GetSemanticNode().State.HasFlag(UiSemanticState.Invalid);
        return new(error ? NotificationProcessing.ImportantMostRecent : NotificationProcessing.MostRecent,
            message, $"Broiler.Status.{source.SemanticId}");
    }

    /// <summary>Status elements are polite live regions; one reporting an error is assertive.</summary>
    public static LiveSetting LiveSettingFor(UiSemanticNode node) =>
        node.Role != UiSemanticRole.StatusAnnouncement ? LiveSetting.Off
        : node.State.HasFlag(UiSemanticState.Invalid) ? LiveSetting.Assertive : LiveSetting.Polite;
}
