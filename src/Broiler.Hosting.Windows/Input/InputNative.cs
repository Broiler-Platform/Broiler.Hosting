using System.Runtime.InteropServices;

namespace Broiler.Hosting.Windows.Input;

/// <summary>
/// Win32 and IMM32 P/Invoke interop declarations for text input, IME composition,
/// subclassing, and precision wheel routing.
/// </summary>
public static partial class InputNative
{
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_DEADCHAR = 0x0103;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_SYSDEADCHAR = 0x0107;
    public const uint WM_UNICHAR = 0x0109;
    public const uint WM_IME_STARTCOMPOSITION = 0x010D;
    public const uint WM_IME_ENDCOMPOSITION = 0x010E;
    public const uint WM_IME_COMPOSITION = 0x010F;
    public const uint WM_IME_SETCONTEXT = 0x0281;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_MOUSEHWHEEL = 0x020E;

    public const uint GCS_COMPSTR = 0x0008;
    public const uint GCS_RESULTSTR = 0x0800;
    public const uint GCS_CURSORPOS = 0x0080;

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_MBUTTON = 0x04;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;

    public const int WA_INACTIVE = 0;
    public const int WA_ACTIVE = 1;
    public const int WA_CLICKACTIVE = 2;

    public const double WheelDelta = 120.0;

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
    public static partial nint SetFocus(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [LibraryImport("imm32.dll")]
    public static partial nint ImmGetContext(nint hWnd);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmReleaseContext(nint hWnd, nint hImc);

    [LibraryImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW")]
    public static partial int ImmGetCompositionString(nint hImc, uint dwIndex, nint lpBuf, uint dwBufLen);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }
}
