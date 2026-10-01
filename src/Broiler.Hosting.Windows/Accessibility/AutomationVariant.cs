using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Broiler.Hosting.Windows.Accessibility;

// Blittable ABI storage for VARIANT: 8-byte header followed by an 8-byte value or
// two record pointers (16 bytes on 64-bit Windows). ComVariant supplies allocation
// and disposal, while this boundary type needs no runtime struct marshaller.
[StructLayout(LayoutKind.Sequential)]
internal struct AutomationVariant
{
    public ulong Header;
    public VariantData Data;

    [StructLayout(LayoutKind.Explicit)]
    internal struct VariantData
    {
        [FieldOffset(0)] public long Value;
        [FieldOffset(0)] public RecordPointers Record;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecordPointers { public nint Record; public nint RecordInfo; }

    // These copies share ownership. Exactly one copy must be disposed, by the
    // native recipient for out values or by the managed caller for borrowed inputs.
    internal static AutomationVariant From(ComVariant value) => Unsafe.BitCast<ComVariant, AutomationVariant>(value);
    internal ComVariant ToVariant() => Unsafe.BitCast<AutomationVariant, ComVariant>(this);
}
