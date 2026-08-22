using System.Runtime.InteropServices;

namespace Monitor.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    /// <summary>Creates an instance with cbSize already populated, as required by GetMonitorInfoW.</summary>
    public static MONITORINFO Create()
    {
        return new MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
        };
    }
}

public static partial class User32
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_TOP = new(0);
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string lpString);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    // ---- フルスクリーンアプリの検出用 ----
    // ABN_FULLSCREENAPP の通知は Windows 10/11 では取りこぼしがある（ブラウザのフルスクリーンで
    // 特に顕著）ため、前面ウィンドウがモニタ全体を覆っているかを自分で調べられるようにする。

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    /// <summary>デスクトップ（Progman）のウィンドウ。フルスクリーン判定から除外するために使う。</summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    /// <summary>ウィンドウのクラス名を取得する。失敗したら空文字。</summary>
    public static string GetWindowClassName(IntPtr hWnd)
    {
        char[] buffer = new char[256];
        int length = GetClassNameW(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
