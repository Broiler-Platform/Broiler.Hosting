using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>Explicit OLE ownership at the UIA boundary; no runtime COM/VARIANT marshaller.</summary>
internal static unsafe partial class AutomationMarshalling
{
    internal static ComVariant ToVariant(object? value) => value switch
    {
        null => default, // VT_EMPTY means an unsupported UIA property, not VT_NULL.
        string text => ComVariant.Create(text),
        bool flag => ComVariant.Create(flag),
        int number => ComVariant.Create(number),
        nint handle => ComVariant.Create(unchecked((int)handle)), // UIA native-window-handle property is VT_I4.
        UiaRect rect => ComVariant.CreateRaw(VarEnum.VT_ARRAY | VarEnum.VT_R8,
            Doubles([rect.Left, rect.Top, rect.Width, rect.Height])),
        // Element-valued properties such as LabeledBy: the VARIANT owns one provider reference.
        IRawElementProviderSimple provider => ComVariant.CreateRaw(VarEnum.VT_UNKNOWN,
            (nint)ComInterfaceMarshaller<INativeSimple>.ConvertToUnmanaged(NativeProviderAdapter.For(provider))),
        _ => throw new NotSupportedException($"Unsupported UIA property type: {value.GetType().Name}"),
    };

    internal static nint Integers(int[]? values) => values is null ? 0 : Array(values, VarEnum.VT_I4);
    internal static nint Doubles(double[] values) => Array(values, VarEnum.VT_R8);

    private static nint Array<T>(T[] values, VarEnum type) where T : unmanaged
    {
        nint array = SafeArrayCreateVector((ushort)type, 0, (uint)values.Length);
        if (array == 0) throw new OutOfMemoryException();
        try
        {
            Marshal.ThrowExceptionForHR(SafeArrayAccessData(array, out nint data));
            try { values.AsSpan().CopyTo(new Span<T>((void*)data, values.Length)); }
            finally { Marshal.ThrowExceptionForHR(SafeArrayUnaccessData(array)); }
            return array;
        }
        catch { SafeArrayDestroy(array); throw; }
    }

    internal static nint Providers(IRawElementProviderSimple[]? values)
    {
        if (values is null) return 0;
        nint array = SafeArrayCreateVector((ushort)VarEnum.VT_UNKNOWN, 0, (uint)values.Length);
        if (array == 0) throw new OutOfMemoryException();
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                void* pointer = ComInterfaceMarshaller<INativeSimple>.ConvertToUnmanaged(NativeProviderAdapter.For(values[i]));
                // PutElement takes an IUnknown* directly and adds its own reference.
                try { Marshal.ThrowExceptionForHR(SafeArrayPutElement(array, in i, (nint)pointer)); }
                finally { ComInterfaceMarshaller<INativeSimple>.Free(pointer); }
            }
            return array;
        }
        catch { SafeArrayDestroy(array); throw; }
    }

    internal static nint TextRanges(WindowsTextRange[] values)
    {
        nint array = SafeArrayCreateVector((ushort)VarEnum.VT_UNKNOWN, 0, (uint)values.Length);
        if (array == 0) throw new OutOfMemoryException();
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                void* pointer = ComInterfaceMarshaller<INativeTextRange>.ConvertToUnmanaged(new NativeTextRange(values[i]));
                try { Marshal.ThrowExceptionForHR(SafeArrayPutElement(array, in i, (nint)pointer)); }
                finally { ComInterfaceMarshaller<INativeTextRange>.Free(pointer); }
            }
            return array;
        }
        catch { SafeArrayDestroy(array); throw; }
    }

    [LibraryImport("oleaut32.dll")]
    private static partial nint SafeArrayCreateVector(ushort type, int lowerBound, uint count);
    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayAccessData(nint array, out nint data);
    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayUnaccessData(nint array);
    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayDestroy(nint array);
    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayPutElement(nint array, in int index, nint value);
}
