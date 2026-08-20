using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Monitor.App.Diagnostics;
using Monitor.App.Settings;
using Monitor.App.Views;
using Monitor.Core;
using Monitor.Optional.Lhm;
using Monitor.Vendors.Nvidia;
using Monitor.Windows.Providers;

namespace Monitor.App;

/// <summary>
/// アプリのエントリポイント。単一インスタンス化、<see cref="MetricsHub"/> の起動/停止、
/// サイドバーウィンドウの生成を行う。<c>StartupUri</c> は使わず <see cref="OnStartup"/> で
/// すべて明示的に組み立てる。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\WinMonSidebar_SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private MetricsHub? _hub;
    private SidebarWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        }
        catch
        {
            // Mutex 自体が作れない異常な環境では、多重起動チェックより起動を優先する。
            createdNew = true;
        }

        _ownsSingleInstanceMutex = createdNew;

        if (!createdNew)
        {
            // AppBar が二重登録されると作業領域が二重に削られるため、既に起動中なら即終了する。
            Shutdown();
            return;
        }

        AppLog.Write("startup: begin");

        AppSettings settings = SettingsStore.Load();

        var options = new MetricsHubOptions
        {
            TopProcessCount = settings.TopProcessCount,
        };
        _hub = new MetricsHub(
            options,
            new CpuProvider(),
            new MemoryProvider(),
            new DiskProvider(),
            new NetworkProvider(),
            new GpuProvider(NvidiaVendorSensors.TryCreate()),
            new ProcessProvider(),
            new LhmThermalProvider(),
            new VolumeProvider());

        _hub.Start();

        try
        {
            _window = new SidebarWindow(_hub, settings);
            _window.Show();
            AppLog.Write("startup: window shown");
        }
        catch (Exception ex)
        {
            AppLog.Write("startup: window creation failed", ex);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;

        if (_hub is not null)
        {
            try
            {
                _hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // 終了処理中の例外でプロセス終了を妨げない。
            }
        }

        try
        {
            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }

            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // ミューテックス解放の失敗は無視する。
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Trace.TraceError("Unhandled UI exception: " + e.Exception);
            AppLog.Write("Unhandled UI exception", e.Exception);
        }
        catch
        {
            // ログ出力自体が失敗してもアプリを落とさない。
        }

        // ここでアプリを終了させると AppBar の解除処理が走らないまま落ちる恐れがあるため、
        // 例外を握りつぶしてアプリを継続させる。
        e.Handled = true;
    }
}
