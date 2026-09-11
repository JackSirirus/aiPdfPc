// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIPDF.Windows.Services;

/// <summary>
/// 已有实例的窗口唤起（FR-32）。
/// <para>
/// <c>SetForegroundWindow</c> 在 Windows 前台锁（用户正在操作别的窗口、或本进程未持有前台权限）
/// 下会失败。失败时回退：还原最小化 + 闪烁任务栏图标提示「已有一个实例」。
/// 无论唤起成功与否都直接返回，调用方据此保证「绝不因唤起失败而另开实例」。
/// </para>
/// </summary>
public static class WindowActivation
{
    private const int SW_RESTORE = 9;
    private const uint FLASHW_ALL = 0x00000003;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    /// <summary>
    /// 尝试把窗口带到前台。返回 true 表示成功拿到前台；false 表示已走回退（仍返回，不抛出）。
    /// 句柄尚未创建（窗口未显示）时返回 false，不做任何动作。
    /// </summary>
    public static bool TryActivate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (SetForegroundWindow(handle))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // 落入回退
        }

        try
        {
            ShowWindowAsync(handle, SW_RESTORE);
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = handle,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception)
        {
            // 回退也失败：不再尝试；调用方保证不新开实例
        }

        return false;
    }
}
