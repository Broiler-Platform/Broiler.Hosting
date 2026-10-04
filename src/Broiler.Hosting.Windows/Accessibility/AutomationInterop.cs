using System.Runtime.InteropServices;
using Broiler.Native.Windows.Accessibility;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>UI Automation values and functions this provider needs that Broiler.Native does not declare (UIAutomationCoreApi.h).</summary>
internal static partial class AutomationInterop
{
    /// <summary>
    /// First element of a fragment's runtime ID: UIA prefixes the rest with the runtime ID of the window
    /// that hosts the fragment root, so the value after it only has to be unique within that root.
    /// </summary>
    public const int AppendRuntimeId = 3;

    /// <summary>UIA_E_ELEMENTNOTAVAILABLE: the element is no longer part of the UI, or its window is gone.</summary>
    public const int ElementNotAvailable = unchecked((int)0x80040201);

    public static COMException ElementNotAvailableException() => new("The element is no longer available.", ElementNotAvailable);

    /// <summary>Releases every reference UIA holds to a provider, so a client's next call on it fails as not available.</summary>
    [LibraryImport("UIAutomationCore.dll")]
    public static partial int UiaDisconnectProvider(INativeSimple provider);

    /// <summary>
    /// UiaReturnRawElementProvider with a null provider, called when the window is destroyed: UIA then
    /// releases the providers it obtained for that window.
    /// </summary>
    [LibraryImport("UIAutomationCore.dll", EntryPoint = "UiaReturnRawElementProvider")]
    private static partial nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, nint provider);

    public static void UiaReleaseWindowProviders(nint hwnd) => UiaReturnRawElementProvider(hwnd, 0, 0, 0);
}
