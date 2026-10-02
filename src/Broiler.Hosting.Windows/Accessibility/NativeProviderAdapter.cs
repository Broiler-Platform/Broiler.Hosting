using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>Generated CCW around the testable managed peer model. Returned COM/SAFEARRAY/VARIANT values transfer ownership.</summary>
[GeneratedComClass]
internal sealed partial class NativeProviderAdapter(IRawElementProviderSimple target) :
    INativeSimple, INativeFragment, INativeFragmentRoot, INativeInvoke, INativeValue,
    INativeSelectionItem, INativeSelection, INativeToggle, INativeExpandCollapse, INativeScrollItem, INativeText
{
    private static readonly ConditionalWeakTable<IRawElementProviderSimple, NativeProviderAdapter> Adapters = new();
    internal static NativeProviderAdapter? For(IRawElementProviderSimple? target) =>
        target is null ? null : Adapters.GetValue(target, static provider => new(provider));

    private IRawElementProviderFragment Fragment => (IRawElementProviderFragment)target;
    private WindowsAutomationBridge Bridge => target is WindowsAutomationBridge bridge ? bridge
        : ((WindowsElementAutomationPeer)target).Bridge;
    private T Read<T>(Func<T> read) => Bridge.OnUiThread(read);
    private void Change(Action change) => Bridge.OnUiThread(() => { change(); return true; });

    ProviderOptions INativeSimple.GetProviderOptions() => ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;
    unsafe nint INativeSimple.GetPatternProvider(int id) => Read(() =>
        (nint)ComInterfaceMarshaller<INativeSimple>.ConvertToUnmanaged(For(target.GetPatternProvider(id) as IRawElementProviderSimple)));
    AutomationVariant INativeSimple.GetPropertyValue(int id) => Read(() => AutomationVariant.From(AutomationMarshalling.ToVariant(target.GetPropertyValue(id))));
    INativeSimple? INativeSimple.GetHostRawElementProvider()
    {
        if (target is not WindowsAutomationBridge { Hwnd: not 0 } bridge) return null;
        return UiaNative.UiaHostProviderFromHwnd(bridge.Hwnd, out var host) >= 0 ? host : null;
    }

    INativeFragment? INativeFragment.Navigate(NavigateDirection direction) => Read(() => For(Fragment.Navigate(direction)));
    nint INativeFragment.GetRuntimeId() => Read(() => AutomationMarshalling.Integers(Fragment.GetRuntimeId()));
    UiaRect INativeFragment.GetBoundingRectangle() => Read(() => Fragment.BoundingRectangle);
    nint INativeFragment.GetEmbeddedFragmentRoots() => Read(() => AutomationMarshalling.Providers(Fragment.GetEmbeddedFragmentRoots()));
    void INativeFragment.SetFocus() => Change(Fragment.SetFocus);
    INativeFragmentRoot? INativeFragment.GetFragmentRoot() => Read(() => For(Fragment.FragmentRoot));
    INativeFragment? INativeFragmentRoot.ElementProviderFromPoint(double x, double y) =>
        Read(() => For(((IRawElementProviderFragmentRoot)target).ElementProviderFromPoint(x, y)));
    INativeFragment? INativeFragmentRoot.GetFocus() => Read(() => For(((IRawElementProviderFragmentRoot)target).GetFocus()));

    void INativeInvoke.Invoke() => Change(((IInvokeProvider)target).Invoke);
    void INativeValue.SetValue(string value) => Change(() => ((IValueProvider)target).SetValue(value));
    string INativeValue.GetValue() => Read(() => ((IValueProvider)target).Value);
    bool INativeValue.GetIsReadOnly() => Read(() => ((IValueProvider)target).IsReadOnly);
    void INativeSelectionItem.Select() => Change(((ISelectionItemProvider)target).Select);
    void INativeSelectionItem.AddToSelection() => Change(((ISelectionItemProvider)target).AddToSelection);
    void INativeSelectionItem.RemoveFromSelection() => Change(((ISelectionItemProvider)target).RemoveFromSelection);
    bool INativeSelectionItem.GetIsSelected() => Read(() => ((ISelectionItemProvider)target).IsSelected);
    INativeSimple? INativeSelectionItem.GetSelectionContainer() => Read(() => For(((ISelectionItemProvider)target).SelectionContainer));
    nint INativeSelection.GetSelection() => Read(() => AutomationMarshalling.Providers(((ISelectionProvider)target).GetSelection()));
    bool INativeSelection.GetCanSelectMultiple() => Read(() => ((ISelectionProvider)target).CanSelectMultiple);
    bool INativeSelection.GetIsSelectionRequired() => Read(() => ((ISelectionProvider)target).IsSelectionRequired);
    void INativeToggle.Toggle() => Change(((IToggleProvider)target).Toggle);
    ToggleState INativeToggle.GetToggleState() => Read(() => ((IToggleProvider)target).ToggleState);
    void INativeExpandCollapse.Expand() => Change(((IExpandCollapseProvider)target).Expand);
    void INativeExpandCollapse.Collapse() => Change(((IExpandCollapseProvider)target).Collapse);
    ExpandCollapseState INativeExpandCollapse.GetExpandCollapseState() => Read(() => ((IExpandCollapseProvider)target).ExpandCollapseState);
    void INativeScrollItem.ScrollIntoView() => Change(((IScrollItemProvider)target).ScrollIntoView);

    private ITextProvider Text => (ITextProvider)target;
    nint INativeText.GetSelection() => Read(() => AutomationMarshalling.TextRanges(Text.GetTextSelection()));
    nint INativeText.GetVisibleRanges() => Read(() => AutomationMarshalling.TextRanges(Text.GetVisibleRanges()));
    // The text has no embedded child elements.
    INativeTextRange? INativeText.RangeFromChild(INativeSimple? childElement) => null;
    INativeTextRange? INativeText.RangeFromPoint(UiaPoint point) => Read(() => new NativeTextRange(Text.RangeFromPoint(point.X, point.Y)));
    INativeTextRange? INativeText.GetDocumentRange() => Read(() => new NativeTextRange(Text.DocumentRange));
    SupportedTextSelection INativeText.GetSupportedTextSelection() => Read(() => Text.SupportedTextSelection);
}
