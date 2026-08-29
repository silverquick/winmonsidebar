using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Monitor.App.Diagnostics;
using Monitor.App.Settings;
using Monitor.App.Views;
using Monitor.Core;
using Monitor.Core.Models;
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

        // AppBar が二重登録されると作業領域が二重に削られるため、多重起動は許さない。
        //
        // ただし単純に「取れなければ即終了」にすると「管理者で再起動」が競合で失敗する。
        // 旧インスタンスは Process.Start(runas) の直後に Shutdown() するが、昇格した新プロセスの
        // .NET/WPF 起動には 1 秒近くかかる一方、Process.Start はプロセス生成時点で戻るため、
        // 新旧どちらが先にミューテックスへ到達するかはタイミング次第になる。新プロセスが先に
        // 見に行くと「既に起動中」と判断して即終了し、その後で旧プロセスも終了するので、
        // 「UAC を承認したのにアプリが消える（または昇格しないまま残る）」という結果になる。
        // そこで少しの間だけ待ってから諦める。真の多重起動ではこの待ち時間のあと終了する。
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
            try
            {
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                // 前のインスタンスが解放せずに落ちた場合。所有権はこちらに移っている。
                _ownsSingleInstanceMutex = true;
            }
        }
        catch
        {
            // ミューテックス自体が作れない異常な環境では、多重起動チェックより起動を優先する。
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

        if (_singleInstanceMutex is not null && !_ownsSingleInstanceMutex)
        {
            Shutdown();
            return;
        }

        AppLog.Write("startup: begin");
        AppLog.Write($"startup: elevated={IsElevated()}");

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
            new ProcessProvider(options.TopProcessCount),
            new LhmThermalProvider(settings.SensorAliases),
            new VolumeProvider());

        // 温度・ファンは権限とハードウェアに強く依存し、取れないときの原因が画面からは分からない。
        // 「管理者で起動したのに温度が出ない」ときに、権限・プロバイダ・センサーのどこで
        // 止まっているのかをログだけで切り分けられるよう、最初の取得結果を 1 度だけ記録する。
        var thermalLogged = false;
        var snapshotsSeen = 0;
        _hub.SnapshotAvailable += snapshot =>
        {
            if (thermalLogged)
            {
                return;
            }

            snapshotsSeen++;
            ThermalSnapshot t = snapshot.Thermal;
            if (!t.IsAvailable && snapshotsSeen < 5)
            {
                // 最初の数回はまだ Thermal のサンプリング周期に入っていない可能性がある。
                return;
            }

            thermalLogged = true;
            AppLog.Write(
                $"thermal: available={t.IsAvailable} elevated={t.IsElevated} source={t.Source} " +
                $"cpuPackage={Fmt(t.CpuPackageTemperatureC)} cpuCores={t.CpuCoreTemperatures.Count} " +
                $"cpuPower={Fmt(t.CpuPackagePowerWatts)} motherboard={Fmt(t.MotherboardTemperatureC)} " +
                $"vrm={Fmt(t.VrmTemperatureC)} fans={t.Fans.Count} " +
                $"otherTemps={t.OtherTemperatures.Count} storageTemps={t.StorageTemperatures.Count}");

            // センサー名はマザーボードごとに違い、VRM がどの名前で出てくるかは実機を見ないと分からない。
            // 振り分け（VrmTemperatureC か OtherTemperatures か）を調整できるよう名前も残す。
            if (t.IsAvailable)
            {
                AppLog.Write("thermal: cores    = " + Names(t.CpuCoreTemperatures));
                AppLog.Write("thermal: others   = " + Names(t.OtherTemperatures));
                AppLog.Write("thermal: fans     = " + Names(t.Fans));
                AppLog.Write("thermal: storage  = " + Names(t.StorageTemperatures));
            }

            static string Fmt(double? v) =>
                v?.ToString("F1", CultureInfo.InvariantCulture) ?? "null";

            static string Names(IReadOnlyList<SensorReading> readings) =>
                readings.Count == 0
                    ? "(なし)"
                    : string.Join(", ", readings.Select(r =>
                        string.Create(CultureInfo.InvariantCulture, $"{r.Name}={r.Value:F1}")));
        };

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

    /// <summary>現在のプロセスが管理者権限で動いているか。</summary>
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
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
