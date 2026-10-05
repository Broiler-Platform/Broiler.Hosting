using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Broiler.Native.Windows.Accessibility;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>Generated CCW for a managed text range. Every call runs on the UI thread through the owning bridge.</summary>
[GeneratedComClass]
internal sealed partial class NativeTextRange(WindowsTextRange range) : INativeTextRange
{
    // UIA_IsReadOnlyAttributeId: the one text attribute that is known without formatting information.
    private const int IsReadOnlyAttributeId = 40015;

    internal WindowsTextRange Range { get; } = range;

    // A range outlives nothing: once its element is gone, every call fails as not available.
    private T Read<T>(Func<T> read) => Range.Owner.Bridge.OnUiThread(() =>
    {
        if (!Range.Owner.IsAlive) throw AutomationInterop.ElementNotAvailableException();
        return read();
    });
    private void Change(Action change) => Read(() => { change(); return true; });

    // Ranges passed back by UIA are this process's own CCWs, which unwrap to the managed object.
    private static WindowsTextRange Unwrap(INativeTextRange other) =>
        other is NativeTextRange native ? native.Range : throw new ArgumentException("The range was not created by this provider.");

    public INativeTextRange Clone() => Read(() => new NativeTextRange(Range.Clone()));
    public bool Compare(INativeTextRange range) => Read(() => Range.Compare(Unwrap(range)));
    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, INativeTextRange targetRange, TextPatternRangeEndpoint targetEndpoint) =>
        Read(() => Range.CompareEndpoints(endpoint, Unwrap(targetRange), targetEndpoint));
    public void ExpandToEnclosingUnit(TextUnit unit) => Change(() => Range.ExpandToEnclosingUnit(unit));
    // Plain text carries no formatting runs, so no attribute search can match.
    public INativeTextRange? FindAttribute(int attributeId, AutomationVariant value, bool backward) => null;
    public INativeTextRange? FindText(string text, bool backward, bool ignoreCase) =>
        Read(() => Range.FindText(text, backward, ignoreCase) is { } found ? new NativeTextRange(found) : null);

    public AutomationVariant GetAttributeValue(int attributeId) => Read(() =>
    {
        if (attributeId == IsReadOnlyAttributeId)
            return AutomationVariant.From(ComVariant.Create(Range.Owner.IsReadOnly));
        // Unknown attributes must return the reserved NotSupported value, not an empty VARIANT.
        Marshal.ThrowExceptionForHR(UiaNative.UiaGetReservedNotSupportedValue(out nint notSupported));
        return AutomationVariant.From(ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, notSupported));
    });

    public nint GetBoundingRectangles() => Read(() =>
    {
        // Without per-character geometry, a non-empty range is reported as the visible part of the
        // element; a range in an element scrolled out of view has no rectangle at all.
        UiaRect bounds = Range.Owner.BoundingRectangle;
        if (Range.IsDegenerate || bounds.Width <= 0 || bounds.Height <= 0) return AutomationMarshalling.Doubles([]);
        return AutomationMarshalling.Doubles([bounds.Left, bounds.Top, bounds.Width, bounds.Height]);
    });

    public INativeSimple? GetEnclosingElement() => Read(() => (INativeSimple?)NativeProviderAdapter.For(Range.Owner));
    public string GetText(int maxLength) => Read(() => Range.GetText(maxLength));
    public int Move(TextUnit unit, int count) => Read(() => Range.Move(unit, count));
    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) =>
        Read(() => Range.MoveEndpointByUnit(endpoint, unit, count));
    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, INativeTextRange targetRange, TextPatternRangeEndpoint targetEndpoint) =>
        Change(() => Range.MoveEndpointByRange(endpoint, Unwrap(targetRange), targetEndpoint));
    public void Select() => Change(Range.Select);
    // Only a single selection is supported.
    public void AddToSelection() => throw new InvalidOperationException("Only one text selection is supported.");
    public void RemoveFromSelection() => throw new InvalidOperationException("Only one text selection is supported.");
    public void ScrollIntoView(bool alignToTop) { }
    public nint GetChildren() => Read(() => AutomationMarshalling.Providers([]));
}
