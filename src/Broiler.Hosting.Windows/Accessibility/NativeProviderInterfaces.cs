using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Broiler.Hosting.Windows.Accessibility;

// Native UIA interfaces independently derive from IUnknown. The managed peer hierarchy
// is a convenience only and must not add Simple's slots to Fragment or FragmentRoot.
// Methods (rather than properties) work with the .NET 10 COM source generator.
[GeneratedComInterface, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface INativeSimple
{
    ProviderOptions GetProviderOptions();
    nint GetPatternProvider(int patternId);
    AutomationVariant GetPropertyValue(int propertyId);
    INativeSimple? GetHostRawElementProvider();
}

[GeneratedComInterface, Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface INativeFragment
{
    INativeFragment? Navigate(NavigateDirection direction);
    nint GetRuntimeId(); // SAFEARRAY(VT_I4), ownership passes to UIA
    UiaRect GetBoundingRectangle();
    nint GetEmbeddedFragmentRoots(); // SAFEARRAY(VT_UNKNOWN)
    void SetFocus();
    INativeFragmentRoot? GetFragmentRoot();
}

[GeneratedComInterface, Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface INativeFragmentRoot
{
    INativeFragment? ElementProviderFromPoint(double x, double y);
    INativeFragment? GetFocus();
}

[GeneratedComInterface, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface INativeInvoke { void Invoke(); }

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("c7935180-6fb3-4201-b174-7df73adbf64a")]
internal partial interface INativeValue
{
    void SetValue(string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetValue();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetIsReadOnly();
}

[GeneratedComInterface, Guid("2acad808-b2d4-452d-a407-91ff1ad167b2")]
internal partial interface INativeSelectionItem
{
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetIsSelected();
    INativeSimple? GetSelectionContainer();
}

[GeneratedComInterface, Guid("fb8b03af-3bdf-48d4-bd36-1a65793be168")]
internal partial interface INativeSelection
{
    nint GetSelection(); // SAFEARRAY(VT_UNKNOWN)
    [return: MarshalAs(UnmanagedType.Bool)] bool GetCanSelectMultiple();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetIsSelectionRequired();
}

[GeneratedComInterface, Guid("56d00bd0-c4f4-433c-a836-1a52a57e0892")]
internal partial interface INativeToggle
{
    void Toggle();
    ToggleState GetToggleState();
}

[GeneratedComInterface, Guid("d847d3a5-cab0-4a98-8c32-ecb45c59ad24")]
internal partial interface INativeExpandCollapse
{
    void Expand();
    void Collapse();
    ExpandCollapseState GetExpandCollapseState();
}

[GeneratedComInterface, Guid("2360c714-4bf1-4b26-ba65-9b21316127eb")]
internal partial interface INativeScrollItem { void ScrollIntoView(); }
