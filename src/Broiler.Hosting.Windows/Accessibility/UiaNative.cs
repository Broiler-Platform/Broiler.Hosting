using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Broiler.Hosting.Windows.Accessibility;

[StructLayout(LayoutKind.Sequential)]
public struct UiaRect
{
    public double Left;
    public double Top;
    public double Width;
    public double Height;

    public UiaRect(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }
}

[Flags]
public enum ProviderOptions
{
    ClientSideProvider = 0x0001,
    ServerSideProvider = 0x0002,
    NonClientAreaProvider = 0x0004,
    OverrideProvider = 0x0008,
    ProviderOwnsSetFocus = 0x0010,
    UseComThreading = 0x0020,
}

public enum NavigateDirection
{
    Parent = 0,
    NextSibling = 1,
    PreviousSibling = 2,
    FirstChild = 3,
    LastChild = 4,
}

public enum ToggleState
{
    Off = 0,
    On = 1,
    Indeterminate = 2,
}

public enum ExpandCollapseState
{
    Collapsed = 0,
    Expanded = 1,
    PartiallyExpanded = 2,
    LeafNode = 3,
}

public enum StructureChangeType
{
    ChildAdded = 0,
    ChildRemoved = 1,
    ChildrenInvalidated = 2,
    ChildrenBulkAdded = 3,
    ChildrenBulkRemoved = 4,
    ChildrenReordered = 5,
}

public interface IRawElementProviderSimple
{
    ProviderOptions ProviderOptions { get; }

    object? GetPatternProvider(int patternId);

    object? GetPropertyValue(int propertyId);

    IRawElementProviderSimple? HostRawElementProvider { get; }
}

public interface IRawElementProviderFragment : IRawElementProviderSimple
{
    new ProviderOptions ProviderOptions { get; }

    new object? GetPatternProvider(int patternId);

    new object? GetPropertyValue(int propertyId);

    new IRawElementProviderSimple? HostRawElementProvider { get; }

    IRawElementProviderFragment? Navigate(NavigateDirection direction);

    int[]? GetRuntimeId();

    UiaRect BoundingRectangle { get; }

    IRawElementProviderSimple[]? GetEmbeddedFragmentRoots();

    void SetFocus();

    IRawElementProviderFragmentRoot? FragmentRoot { get; }
}

public interface IRawElementProviderFragmentRoot : IRawElementProviderFragment
{
    new ProviderOptions ProviderOptions { get; }

    new object? GetPatternProvider(int patternId);

    new object? GetPropertyValue(int propertyId);

    new IRawElementProviderSimple? HostRawElementProvider { get; }

    new IRawElementProviderFragment? Navigate(NavigateDirection direction);

    new int[]? GetRuntimeId();

    new UiaRect BoundingRectangle { get; }

    new IRawElementProviderSimple[]? GetEmbeddedFragmentRoots();

    new void SetFocus();

    new IRawElementProviderFragmentRoot? FragmentRoot { get; }

    IRawElementProviderFragment? ElementProviderFromPoint(double x, double y);

    IRawElementProviderFragment? GetFocus();
}

public interface IInvokeProvider
{
    void Invoke();
}

public interface IValueProvider
{
    void SetValue(string value);

    string Value { get; }

    bool IsReadOnly { get; }
}

public interface ISelectionItemProvider
{
    void Select();

    void AddToSelection();

    void RemoveFromSelection();

    bool IsSelected { get; }

    IRawElementProviderSimple? SelectionContainer { get; }
}

public interface ISelectionProvider
{
    IRawElementProviderSimple[]? GetSelection();

    bool CanSelectMultiple { get; }

    bool IsSelectionRequired { get; }
}

public interface IToggleProvider
{
    void Toggle();

    ToggleState ToggleState { get; }
}

public interface IExpandCollapseProvider
{
    void Expand();

    void Collapse();

    ExpandCollapseState ExpandCollapseState { get; }
}

public interface IScrollItemProvider
{
    void ScrollIntoView();
}

internal static partial class UiaNative
{
    public const int UiaRootObjectId = -25;
    public const uint WmGetObject = 0x003D;

    // Pattern IDs
    public const int UiaInvokePatternId = 10000;
    public const int UiaSelectionPatternId = 10001;
    public const int UiaValuePatternId = 10002;
    public const int UiaRangeValuePatternId = 10003;
    public const int UiaScrollPatternId = 10004;
    public const int UiaExpandCollapsePatternId = 10005;
    public const int UiaSelectionItemPatternId = 10010;
    public const int UiaTogglePatternId = 10015;
    public const int UiaScrollItemPatternId = 10017;

    // Control Type IDs
    public const int UiaButtonControlTypeId = 50000;
    public const int UiaCheckBoxControlTypeId = 50002;
    public const int UiaComboBoxControlTypeId = 50003;
    public const int UiaEditControlTypeId = 50004;
    public const int UiaHyperlinkControlTypeId = 50005;
    public const int UiaImageControlTypeId = 50006;
    public const int UiaListItemControlTypeId = 50007;
    public const int UiaListControlTypeId = 50008;
    public const int UiaMenuControlTypeId = 50009;
    public const int UiaMenuItemControlTypeId = 50011;
    public const int UiaProgressBarControlTypeId = 50012;
    public const int UiaRadioButtonControlTypeId = 50013;
    public const int UiaScrollBarControlTypeId = 50014;
    public const int UiaSliderControlTypeId = 50015;
    public const int UiaSpinnerControlTypeId = 50016;
    public const int UiaStatusBarControlTypeId = 50017;
    public const int UiaTabControlTypeId = 50018;
    public const int UiaTabItemControlTypeId = 50019;
    public const int UiaTextControlTypeId = 50020;
    public const int UiaToolBarControlTypeId = 50021;
    public const int UiaToolTipControlTypeId = 50022;
    public const int UiaCustomControlTypeId = 50025;
    public const int UiaGroupControlTypeId = 50026;
    public const int UiaPaneControlTypeId = 50033;
    public const int UiaSeparatorControlTypeId = 50038;

    // Property IDs
    public const int UiaRuntimeIdPropertyId = 30000;
    public const int UiaBoundingRectanglePropertyId = 30001;
    public const int UiaProcessIdPropertyId = 30002;
    public const int UiaControlTypePropertyId = 30003;
    public const int UiaLocalizedControlTypePropertyId = 30004;
    public const int UiaNamePropertyId = 30005;
    public const int UiaAcceleratorKeyPropertyId = 30006;
    public const int UiaAccessKeyPropertyId = 30007;
    public const int UiaHasKeyboardFocusPropertyId = 30008;
    public const int UiaIsKeyboardFocusablePropertyId = 30009;
    public const int UiaIsEnabledPropertyId = 30010;
    public const int UiaAutomationIdPropertyId = 30011;
    public const int UiaClassNamePropertyId = 30012;
    public const int UiaHelpTextPropertyId = 30013;
    public const int UiaIsControlElementPropertyId = 30016;
    public const int UiaIsContentElementPropertyId = 30017;
    public const int UiaIsPasswordPropertyId = 30019;
    public const int UiaNativeWindowHandlePropertyId = 30020;
    public const int UiaIsOffscreenPropertyId = 30022;
    public const int UiaOrientationPropertyId = 30023;
    public const int UiaItemStatusPropertyId = 30028;

    // Pattern Property IDs
    public const int UiaValueValuePropertyId = 30045;
    public const int UiaValueIsReadOnlyPropertyId = 30046;
    public const int UiaSelectionSelectionPropertyId = 30059;
    public const int UiaSelectionCanSelectMultiplePropertyId = 30060;
    public const int UiaSelectionIsSelectionRequiredPropertyId = 30061;
    public const int UiaSelectionItemIsSelectedPropertyId = 30079;
    public const int UiaSelectionItemSelectionContainerPropertyId = 30080;
    public const int UiaToggleToggleStatePropertyId = 30086;
    public const int UiaExpandCollapseExpandCollapseStatePropertyId = 30070;

    // Event IDs
    public const int UiaStructureChangedEventId = 20002;
    public const int UiaAutomationPropertyChangedEventId = 20004;
    public const int UiaAutomationFocusChangedEventId = 20005;
    public const int UiaInvoke_InvokedEventId = 20009;
    public const int UiaSelectionItem_ElementSelectedEventId = 20012;
    public const int UiaLiveRegionChangedEventId = 20024;

    [LibraryImport("UIAutomationCore.dll", EntryPoint = "UiaReturnRawElementProvider")]
    private static partial nint ReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, INativeSimple provider);

    public static nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, IRawElementProviderSimple provider) =>
        ReturnRawElementProvider(hwnd, wParam, lParam, NativeProviderAdapter.For(provider)!);

    [LibraryImport("UIAutomationCore.dll")]
    internal static partial int UiaHostProviderFromHwnd(nint hwnd, out INativeSimple? provider);

    [LibraryImport("UIAutomationCore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UiaClientsAreListening();

    [LibraryImport("UIAutomationCore.dll", EntryPoint = "UiaRaiseAutomationEvent")]
    private static partial int RaiseAutomationEvent(INativeSimple provider, int eventId);

    public static int UiaRaiseAutomationEvent(IRawElementProviderSimple provider, int eventId) =>
        RaiseAutomationEvent(NativeProviderAdapter.For(provider)!, eventId);

    [LibraryImport("UIAutomationCore.dll", EntryPoint = "UiaRaiseAutomationPropertyChangedEvent")]
    private static partial int RaisePropertyChanged(INativeSimple provider, int propertyId, AutomationVariant oldValue, AutomationVariant newValue);

    public static int UiaRaiseAutomationPropertyChangedEvent(IRawElementProviderSimple provider, int propertyId, object oldValue, object newValue)
    {
        using var oldVariant = AutomationMarshalling.ToVariant(oldValue);
        using var newVariant = AutomationMarshalling.ToVariant(newValue);
        return RaisePropertyChanged(NativeProviderAdapter.For(provider)!, propertyId, AutomationVariant.From(oldVariant), AutomationVariant.From(newVariant));
    }

    [LibraryImport("UIAutomationCore.dll", EntryPoint = "UiaRaiseStructureChangedEvent")]
    private static partial int RaiseStructureChanged(INativeSimple provider, StructureChangeType change,
        [MarshalUsing(CountElementName = nameof(length))] int[]? runtimeId, int length);

    public static int UiaRaiseStructureChangedEvent(IRawElementProviderSimple provider, StructureChangeType change, int[]? runtimeId, int length) =>
        RaiseStructureChanged(NativeProviderAdapter.For(provider)!, change, runtimeId, length);

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass, nuint dwRefData);
    public static bool SetWindowSubclass(nint hWnd, SubclassProc callback, nuint id, nuint data) =>
        SetSubclass(hWnd, Marshal.GetFunctionPointerForDelegate(callback), id, data);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass);
    public static bool RemoveWindowSubclass(nint hWnd, SubclassProc callback, nuint id) =>
        RemoveSubclass(hWnd, Marshal.GetFunctionPointerForDelegate(callback), id);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    public static partial nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }
}
