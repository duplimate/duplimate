using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Duplimate.Services.Platform.Windows;

/// <summary>
/// Flashes the taskbar entry for a window via Win32 <c>FlashWindowEx</c>.
/// Used by <see cref="NotificationService"/> to draw the user's attention
/// to a sticky failure alert that's not on their active monitor.
///
/// <para>
/// <c>FLASHW_TIMERNOFG</c> keeps flashing until the window comes to the
/// foreground (rather than a fixed count of flashes) — for failure
/// notifications that's exactly what we want.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTaskbarFlash
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    public static void Flash(IntPtr hwnd)
    {
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref fi);
    }
}
