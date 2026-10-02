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

[StructLayout(LayoutKind.Sequential)]
internal struct UiaPoint { public double X; public double Y; }

// Slot order follows ITextProvider in UIAutomationCore.idl.
[GeneratedComInterface, Guid("3589c92c-63f3-4367-99bb-ada653b77cf2")]
internal partial interface INativeText
{
    nint GetSelection(); // SAFEARRAY(VT_UNKNOWN) of ITextRangeProvider
    nint GetVisibleRanges(); // SAFEARRAY(VT_UNKNOWN) of ITextRangeProvider
    INativeTextRange? RangeFromChild(INativeSimple? childElement);
    INativeTextRange? RangeFromPoint(UiaPoint point);
    INativeTextRange? GetDocumentRange();
    SupportedTextSelection GetSupportedTextSelection();
}

// Slot order follows ITextRangeProvider in UIAutomationCore.idl.
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("5347ad7b-c355-46f8-aff5-909033582f63")]
internal partial interface INativeTextRange
{
    INativeTextRange Clone();
    [return: MarshalAs(UnmanagedType.Bool)] bool Compare(INativeTextRange range);
    int CompareEndpoints(TextPatternRangeEndpoint endpoint, INativeTextRange targetRange, TextPatternRangeEndpoint targetEndpoint);
    void ExpandToEnclosingUnit(TextUnit unit);
    INativeTextRange? FindAttribute(int attributeId, AutomationVariant value, [MarshalAs(UnmanagedType.Bool)] bool backward);
    INativeTextRange? FindText([MarshalAs(UnmanagedType.BStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool backward, [MarshalAs(UnmanagedType.Bool)] bool ignoreCase);
    AutomationVariant GetAttributeValue(int attributeId);
    nint GetBoundingRectangles(); // SAFEARRAY(VT_R8)
    INativeSimple? GetEnclosingElement();
    [return: MarshalAs(UnmanagedType.BStr)] string GetText(int maxLength);
    int Move(TextUnit unit, int count);
    int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count);
    void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, INativeTextRange targetRange, TextPatternRangeEndpoint targetEndpoint);
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    void ScrollIntoView([MarshalAs(UnmanagedType.Bool)] bool alignToTop);
    nint GetChildren(); // SAFEARRAY(VT_UNKNOWN)
}
