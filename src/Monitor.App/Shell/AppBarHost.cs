using System.Windows;
using System.Windows.Interop;
using Monitor.Windows.Native;

namespace Monitor.App.Shell;

/// <summary>
/// Registers a WPF <see cref="Window"/> as a Win32 AppBar (<see cref="Shell32.SHAppBarMessage"/>) so it
/// reserves a strip of the desktop work area that maximized windows avoid, and keeps it positioned as
/// monitors, DPI, and other AppBars (e.g. the taskbar) change.
///
/// Registration lifecycle is strict: <see cref="Register"/> must run after the window's HWND exists
/// (i.e. at or after SourceInitialized), and <see cref="Unregister"/> MUST be called before the process
/// exits or the reserved desktop work area is left permanently shrunk. Both methods are idempotent.
/// </summary>
public sealed class AppBarHost : IDisposable
{
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_DESTROY = 0x0002;

    private readonly Window _window;

    private IntPtr _hwnd;
    private uint _callbackMessage;
    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private bool _disposed;

    public AppBarHost(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public AppBarEdge Edge { get; set; } = AppBarEdge.Right;

    /// <summary>Thickness of the reserved strip in device-independent pixels. Converted to physical
    /// pixels at the target monitor's DPI whenever the position is (re)computed.</summary>
    public int ThicknessDip { get; set; } = 340;

    public bool IsRegistered { get; private set; }

    /// <summary>
    /// Registers the AppBar with the shell. No-op if already registered. No-op (returns silently) if the
    /// window does not yet have an HWND — call this at or after SourceInitialized.
    /// </summary>
    public void Register()
    {
        if (IsRegistered || _disposed)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _hwnd = hwnd;
        _callbackMessage = User32.RegisterWindowMessageW("WinMonSidebar_AppBarMessage");

        var abd = APPBARDATA.Create(_hwnd);
        abd.uCallbackMessage = _callbackMessage;

        var result = Shell32.SHAppBarMessage(Shell32.ABM_NEW, ref abd);
        if (result == UIntPtr.Zero)
        {
            // ABM_NEW failed (e.g. shell not ready). Nothing was registered, so there is nothing to
            // undo; leave IsRegistered false so callers/Dispose don't attempt ABM_REMOVE.
            return;
        }

        IsRegistered = true;

        // Hide from Alt+Tab and the taskbar.
        var exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        var newExStyle = (IntPtr)((long)exStyle | User32.WS_EX_TOOLWINDOW);
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, newExStyle);

        _source = HwndSource.FromHwnd(_hwnd);
        _hook = WndProc;
        _source?.AddHook(_hook);

        UpdatePosition();
    }

    /// <summary>
    /// Recomputes and applies the AppBar's screen rectangle for its current <see cref="Edge"/> and
    /// <see cref="ThicknessDip"/>. All geometry is computed and applied in physical pixels — WPF's
    /// DIP-based Window.Left/Top/Width/Height are never used.
    /// </summary>
    public void UpdatePosition()
    {
        if (!IsRegistered || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var dpi = User32.GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var thicknessPx = (int)Math.Round(ThicknessDip * dpi / 96.0);

        var monitor = User32.MonitorFromWindow(_hwnd, User32.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = MONITORINFO.Create();
        if (!User32.GetMonitorInfoW(monitor, ref monitorInfo))
        {
            return;
        }

        var mon = monitorInfo.rcMonitor;

        var rc = Edge switch
        {
            AppBarEdge.Left => new RECT { Left = mon.Left, Top = mon.Top, Right = mon.Left + thicknessPx, Bottom = mon.Bottom },
            AppBarEdge.Top => new RECT { Left = mon.Left, Top = mon.Top, Right = mon.Right, Bottom = mon.Top + thicknessPx },
            AppBarEdge.Right => new RECT { Left = mon.Right - thicknessPx, Top = mon.Top, Right = mon.Right, Bottom = mon.Bottom },
            AppBarEdge.Bottom => new RECT { Left = mon.Left, Top = mon.Bottom - thicknessPx, Right = mon.Right, Bottom = mon.Bottom },
            _ => new RECT { Left = mon.Right - thicknessPx, Top = mon.Top, Right = mon.Right, Bottom = mon.Bottom },
        };

        var abd = APPBARDATA.Create(_hwnd);
        abd.uCallbackMessage = _callbackMessage;
        abd.uEdge = (uint)Edge;
        abd.rc = rc;

        // ABM_QUERYPOS lets the shell push our rectangle inward to avoid overlapping other AppBars
        // (e.g. the taskbar). It only adjusts to avoid collisions; it does not resize our thickness,
        // so we must re-apply our own thickness against the (possibly moved) edge it gave back.
        Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref abd);

        switch (Edge)
        {
            case AppBarEdge.Left:
                abd.rc.Right = abd.rc.Left + thicknessPx;
                break;
            case AppBarEdge.Top:
                abd.rc.Bottom = abd.rc.Top + thicknessPx;
                break;
            case AppBarEdge.Right:
                abd.rc.Left = abd.rc.Right - thicknessPx;
                break;
            case AppBarEdge.Bottom:
                abd.rc.Top = abd.rc.Bottom - thicknessPx;
                break;
        }

        Shell32.SHAppBarMessage(Shell32.ABM_SETPOS, ref abd);

        // The shell may have adjusted the rectangle further; abd.rc as returned by ABM_SETPOS is final.
        var final = abd.rc;
        User32.SetWindowPos(_hwnd, IntPtr.Zero, final.Left, final.Top, final.Width, final.Height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

        Shell32.SHAppBarMessage(Shell32.ABM_WINDOWPOSCHANGED, ref abd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_callbackMessage != 0 && (uint)msg == _callbackMessage)
        {
            var notification = (uint)wParam.ToInt64();
            switch (notification)
            {
                case Shell32.ABN_POSCHANGED:
                case Shell32.ABN_STATECHANGE:
                case Shell32.ABN_WINDOWARRANGE:
                    UpdatePosition();
                    break;
                case Shell32.ABN_FULLSCREENAPP:
                    if (lParam != IntPtr.Zero)
                    {
                        // A fullscreen app appeared: drop behind everything so it isn't obscured by us.
                        User32.SetWindowPos(_hwnd, User32.HWND_BOTTOM, 0, 0, 0, 0,
                            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                    }
                    else
                    {
                        // The fullscreen app is gone: reclaim our topmost position.
                        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                    }
                    break;
            }

            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_WINDOWPOSCHANGED:
                if (IsRegistered)
                {
                    var abd = APPBARDATA.Create(_hwnd);
                    Shell32.SHAppBarMessage(Shell32.ABM_WINDOWPOSCHANGED, ref abd);
                }
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                UpdatePosition();
                break;
            case WM_DESTROY:
                Unregister();
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Unregisters the AppBar with the shell, releasing the reserved desktop work area, and removes the
    /// message hook. Idempotent — safe to call multiple times (including after Dispose) and safe to call
    /// even if Register never succeeded.
    /// </summary>
    public void Unregister()
    {
        if (_hook is not null)
        {
            _source?.RemoveHook(_hook);
            _hook = null;
        }

        _source = null;

        if (!IsRegistered)
        {
            return;
        }

        var abd = APPBARDATA.Create(_hwnd);
        Shell32.SHAppBarMessage(Shell32.ABM_REMOVE, ref abd);

        IsRegistered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }
}
