namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>UI Automation values this provider needs that Broiler.Native does not declare (UIAutomationCoreApi.h).</summary>
internal static class AutomationInterop
{
    /// <summary>
    /// First element of a fragment's runtime ID: UIA prefixes the rest with the runtime ID of the window
    /// that hosts the fragment root, so the value after it only has to be unique within that root.
    /// </summary>
    public const int AppendRuntimeId = 3;
}
