using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
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
        if (!IsClipboardFormatAvailable(CfUnicodeText))
            return false;

        IntPtr ownerHandle = _owner();
        if (!OpenClipboard(ownerHandle))
            return false;

        try
        {
            IntPtr handle = GetClipboardData(CfUnicodeText);
            nuint size = handle == IntPtr.Zero ? 0 : GlobalSize(handle);
            if (size < 2 || size > MaximumBytes)
                return false;

            IntPtr pointer = GlobalLock(handle);
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
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length >= MaximumBytes / 2)
            return;

        IntPtr ownerHandle = _owner();
        if (!OpenClipboard(ownerHandle))
            return;

        try
        {
            EmptyClipboard();

            int bytes = (text.Length + 1) * sizeof(char);
            IntPtr block = GlobalAlloc(GmemMoveable, (nuint)bytes);
            if (block == IntPtr.Zero)
                return;

            IntPtr pointer = GlobalLock(block);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(block);
                return;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(block);
            }

            if (SetClipboardData(CfUnicodeText, block) == IntPtr.Zero)
                GlobalFree(block);
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr handle);
}
