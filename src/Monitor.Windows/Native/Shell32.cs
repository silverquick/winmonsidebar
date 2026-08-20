using System.Runtime.InteropServices;

namespace Monitor.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
public struct APPBARDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public int lParam;

    /// <summary>Creates an instance with cbSize and hWnd already populated, as required by SHAppBarMessage.</summary>
    public static APPBARDATA Create(IntPtr hWnd)
    {
        return new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = hWnd,
        };
    }
}

public static partial class Shell32
{
    public const uint ABM_NEW = 0x00;
    public const uint ABM_REMOVE = 0x01;
    public const uint ABM_QUERYPOS = 0x02;
    public const uint ABM_SETPOS = 0x03;
    public const uint ABM_GETSTATE = 0x04;
    public const uint ABM_GETTASKBARPOS = 0x05;
    public const uint ABM_ACTIVATE = 0x06;
    public const uint ABM_GETAUTOHIDEBAR = 0x07;
    public const uint ABM_SETAUTOHIDEBAR = 0x08;
    public const uint ABM_WINDOWPOSCHANGED = 0x09;
    public const uint ABM_SETSTATE = 0x0A;

    public const uint ABN_STATECHANGE = 0x00;
    public const uint ABN_FULLSCREENAPP = 0x01;
    public const uint ABN_POSCHANGED = 0x02;
    public const uint ABN_WINDOWARRANGE = 0x03;

    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    [LibraryImport("shell32.dll")]
    public static partial UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
