using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Broiler.Native.Windows;
using Broiler.UI;

namespace Broiler.Hosting.Windows;

/// <summary>
/// The Win32 clipboard, shared across Broiler platform applications (Mail, Code, Writer, Browser).
/// Enforces 1 MB boundary limits, GlobalSize buffer validation, lazy or direct owner resolution,
/// and safe OS memory handoff.
/// </summary>
[SupportedOSPlatform("windows5.0")]
public sealed class WindowsClipboard : IUiClipboardHost
{
    private const int MaximumBytes = 1024 * 1024; // 1 MB boundary protection

    private readonly Func<IntPtr> _owner;

    public WindowsClipboard(IntPtr ownerWindow) : this(() => ownerWindow) { }

    public WindowsClipboard(Func<IntPtr> owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public bool TryGetText(out string text)
    {
        text = string.Empty;
        IntPtr ownerHandle = _owner();
        if (!ClipboardNative.OpenClipboard(ownerHandle))
            return false;

        try
        {
            if (!ClipboardNative.IsClipboardFormatAvailable(ClipboardNative.CF_UNICODETEXT))
                return false;

            IntPtr handle = ClipboardNative.GetClipboardData(ClipboardNative.CF_UNICODETEXT);
            nuint size = handle == IntPtr.Zero ? 0 : ClipboardNative.GlobalSize(handle);
            if (size < 2 || size > MaximumBytes)
                return false;

            IntPtr pointer = ClipboardNative.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return false;

            try
            {
                string value = Marshal.PtrToStringUni(pointer, (int)size / 2) ?? string.Empty;
                int end = value.IndexOf('\0');
                if (end < 0)
                    return false;

                text = value[..end];
                return text.Length > 0;
            }
            finally
            {
                ClipboardNative.GlobalUnlock(handle);
            }
        }
        finally
        {
            ClipboardNative.CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length >= MaximumBytes / 2)
            return;

        IntPtr ownerHandle = _owner();
        if (!ClipboardNative.OpenClipboard(ownerHandle))
            return;

        try
        {
            ClipboardNative.EmptyClipboard();

            int bytes = (text.Length + 1) * sizeof(char);
            IntPtr block = ClipboardNative.GlobalAlloc(ClipboardNative.GMEM_MOVEABLE, (nuint)bytes);
            if (block == IntPtr.Zero)
                return;

            IntPtr pointer = ClipboardNative.GlobalLock(block);
            if (pointer == IntPtr.Zero)
            {
                ClipboardNative.GlobalFree(block);
                return;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                ClipboardNative.GlobalUnlock(block);
            }

            if (ClipboardNative.SetClipboardData(ClipboardNative.CF_UNICODETEXT, block) == IntPtr.Zero)
                ClipboardNative.GlobalFree(block);
        }
        finally
        {
            ClipboardNative.CloseClipboard();
        }
    }
}
