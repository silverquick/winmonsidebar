using System.Windows;
using System.Windows.Interop;
using Monitor.Windows.Native;

namespace Monitor.App.Shell;

/// <summary>
/// Base class for a WPF window that behaves as a Win32 AppBar: chromeless, non-resizable, hidden from
/// the taskbar/Alt+Tab, always on top, docked to a screen edge, and reserving its own strip of the
/// desktop work area via <see cref="AppBarHost"/>.
/// </summary>
public class AppBarWindow : Window
{
    private readonly AppBarHost _appBarHost;
    private bool _safetyNetsHooked;

    public AppBarWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // AllowsTransparency = true would disable the DWM backdrop applied in OnSourceInitialized; the
        // two are mutually exclusive, so this must stay false.
        AllowsTransparency = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _appBarHost = new AppBarHost(this);
    }

    /// <summary>Screen edge to dock to. Forwarded to the underlying <see cref="AppBarHost"/>.</summary>
    public AppBarEdge Edge
    {
        get => _appBarHost.Edge;
        set => _appBarHost.Edge = value;
    }

    /// <summary>Reserved strip thickness in device-independent pixels. Forwarded to the underlying
    /// <see cref="AppBarHost"/>.</summary>
    public int ThicknessDip
    {
        get => _appBarHost.ThicknessDip;
        set => _appBarHost.ThicknessDip = value;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        DwmApi.ApplyModernFrame(
            hwnd,
            DwmSystemBackdropType.TransientWindow,
            darkMode: true,
            DwmWindowCornerPreference.DoNotRound);

        // Multiple safety nets around AppBar unregistration: failing to call SHAppBarMessage(ABM_REMOVE)
        // leaves the desktop work area permanently shrunk for the user, so every plausible teardown path
        // (normal close, logoff/shutdown, process exit, an unhandled exception) must attempt it.
        HookSafetyNets();

        _appBarHost.Register();
    }

    // Deliberately no OnClosing override: a close can be cancelled (e.Cancel = true), and unregistering
    // there would leave a live window that no longer reserves its strip. Teardown happens on WM_DESTROY
    // (inside AppBarHost) and OnClosed, both of which only run once the close actually goes through.

    protected override void OnClosed(EventArgs e)
    {
        _appBarHost.Unregister();
        UnhookSafetyNets();
        _appBarHost.Dispose();
        base.OnClosed(e);
    }

    private void HookSafetyNets()
    {
        if (_safetyNetsHooked)
        {
            return;
        }

        _safetyNetsHooked = true;

        if (Application.Current is not null)
        {
            Application.Current.SessionEnding += OnSessionEnding;
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void UnhookSafetyNets()
    {
        if (!_safetyNetsHooked)
        {
            return;
        }

        _safetyNetsHooked = false;

        if (Application.Current is not null)
        {
            Application.Current.SessionEnding -= OnSessionEnding;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e) => _appBarHost.Unregister();

    private void OnProcessExit(object? sender, EventArgs e) => _appBarHost.Unregister();

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => _appBarHost.Unregister();
}
