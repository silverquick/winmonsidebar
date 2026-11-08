using System.Diagnostics;
using Monitor.Core.Abstractions;
using Monitor.Core.Alerts;
using Monitor.Core.Models;

namespace Monitor.Core;

public sealed class MetricsHubOptions
{
    /// <summary>CPU/Mem/Disk/Net/GPU をサンプリングする間隔。</summary>
    public TimeSpan FastInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>プロセス一覧をサンプリングする間隔。</summary>
    public TimeSpan ProcessInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>温度・ファンをサンプリングする間隔。ハードウェア走査の負荷を抑える。</summary>
    public TimeSpan ThermalInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>ボリュームのキャッシュを読み取る間隔。</summary>
    public TimeSpan VolumeInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int TopProcessCount { get; init; } = 8;

    /// <summary>
    /// 診断ログの出力先（メッセージ, 例外）。バックグラウンドスレッドから呼ばれるため、実装はスレッドセーフであること。
    /// 同じ失敗が続く間は連打せず、失敗開始時と回復時にだけ呼ばれる。sink 自体の例外は hub 内で握りつぶす。
    /// </summary>
    public Action<string, Exception?>? Log { get; init; }
}

/// <summary>
/// サンプリングの司令塔。バックグラウンドスレッドで1本のループを回し、各 <see cref="IMetricProvider{T}"/> を
/// 定期的に呼び出して <see cref="MetricsSnapshot"/> を組み立てる。
/// </summary>
public sealed class MetricsHub : IAsyncDisposable
{
    private readonly MetricsHubOptions _options;
    private readonly IMetricProvider<CpuSnapshot> _cpu;
    private readonly IMetricProvider<MemorySnapshot> _memory;
    private readonly IMetricProvider<DiskSnapshot> _disk;
    private readonly IMetricProvider<NetworkSnapshot> _network;
    private readonly IMetricProvider<GpuSnapshot> _gpu;
    private readonly IMetricProvider<ProcessSnapshot> _processes;
    private readonly IThermalProvider _thermal;
    private readonly IMetricProvider<IReadOnlyList<VolumeSnapshot>> _volumes;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile MetricsSnapshot? _latest;
    private int _processSamplingEnabled = 1;
    private readonly IDetailSamplingProvider? _cpuDetails;
    private readonly object _snapshotLock = new();
    private readonly DiskBusyAlertTracker _diskAlertTracker = new();
    private CpuSnapshot _lastCpu = CpuSnapshot.Empty;
    private MemorySnapshot _lastMemory = MemorySnapshot.Empty;
    private DiskSnapshot _lastDisk = DiskSnapshot.Empty;
    private NetworkSnapshot _lastNetwork = NetworkSnapshot.Empty;
    private GpuSnapshot _lastGpu = GpuSnapshot.Empty;
    private ProcessSnapshot _lastProcesses = ProcessSnapshot.Empty;
    private ThermalSnapshot _lastThermal = ThermalSnapshot.Empty;
    private IReadOnlyList<VolumeSnapshot> _lastVolumes = Array.Empty<VolumeSnapshot>();

    // 失敗の連続状態。ログを「失敗開始時 1 回・回復時 1 回」に絞るために使う。
    // プロバイダ単位のものは担当レーンからしか触らないが、Publish の購読者用は全レーンから触るため、
    // FailureTracker 自体を lock で保護して一律にスレッドセーフにしている。
    private readonly FailureTracker _cpuFailures;
    private readonly FailureTracker _memoryFailures;
    private readonly FailureTracker _diskFailures;
    private readonly FailureTracker _networkFailures;
    private readonly FailureTracker _gpuFailures;
    private readonly FailureTracker _processFailures;
    private readonly FailureTracker _thermalFailures;
    private readonly FailureTracker _volumeFailures;
    private readonly FailureTracker _diskAlertFailures;
    private readonly FailureTracker _subscriberFailures;
    private readonly FailureTracker _fastLaneFailures;
    private readonly FailureTracker _processLaneFailures;
    private readonly FailureTracker _thermalLaneFailures;
    private readonly FailureTracker _volumeLaneFailures;

    /// <summary>テスト用の seam。各レーンの 1 周の先頭（try の内側）でレーン名を渡して呼ばれる。</summary>
    internal Action<string>? LaneIterationHookForTest { get; set; }

    public MetricsHub(
        MetricsHubOptions options,
        IMetricProvider<CpuSnapshot> cpu,
        IMetricProvider<MemorySnapshot> memory,
        IMetricProvider<DiskSnapshot> disk,
        IMetricProvider<NetworkSnapshot> network,
        IMetricProvider<GpuSnapshot> gpu,
        IMetricProvider<ProcessSnapshot> processes,
        IThermalProvider thermal,
        IMetricProvider<IReadOnlyList<VolumeSnapshot>> volumes)
    {
        _options = options;
        _cpu = cpu;
        _memory = memory;
        _disk = disk;
        _network = network;
        _gpu = gpu;
        _processes = processes;
        _thermal = thermal;
        _volumes = volumes;
        _cpuDetails = cpu as IDetailSamplingProvider;

        // ログ用デリゲートは 1 度だけ生成し、毎秒のサンプリングで割り当てを発生させない。
        Action<string, Exception?> log = LogSafe;
        _cpuFailures = new FailureTracker(ProviderLabel(cpu), log);
        _memoryFailures = new FailureTracker(ProviderLabel(memory), log);
        _diskFailures = new FailureTracker(ProviderLabel(disk), log);
        _networkFailures = new FailureTracker(ProviderLabel(network), log);
        _gpuFailures = new FailureTracker(ProviderLabel(gpu), log);
        _processFailures = new FailureTracker(ProviderLabel(processes), log);
        _thermalFailures = new FailureTracker(ProviderLabel(thermal), log);
        _volumeFailures = new FailureTracker(ProviderLabel(volumes), log);
        _diskAlertFailures = new FailureTracker("DiskBusyAlertTracker の更新", log);
        _subscriberFailures = new FailureTracker("SnapshotAvailable 購読者", log);
        _fastLaneFailures = new FailureTracker("fast レーン", log);
        _processLaneFailures = new FailureTracker("process レーン", log);
        _thermalLaneFailures = new FailureTracker("thermal レーン", log);
        _volumeLaneFailures = new FailureTracker("volume レーン", log);

        History = new MetricsHistory();
    }

    /// <summary>
    /// バックグラウンドスレッド上で発火する。購読側が UI へのマーシャリング責任を持つ。
    /// </summary>
    public event Action<MetricsSnapshot>? SnapshotAvailable;

    public MetricsSnapshot? Latest => _latest;

    public MetricsHistory History { get; }

    /// <summary>プロセス一覧の需要を通知する。非表示時は全プロセス走査を止める。</summary>
    public void SetProcessSamplingEnabled(bool enabled) =>
        Volatile.Write(ref _processSamplingEnabled, enabled ? 1 : 0);

    /// <summary>CPU コア別値の需要を通知する。概要値の取得は継続する。</summary>
    public void SetCpuDetailSamplingEnabled(bool enabled) =>
        _cpuDetails?.SetDetailSamplingEnabled(enabled);

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        // PDH の初期化などは遅い可能性があるため、UI スレッドをブロックしないようここで行う。
        InitializeAll();

        try
        {
            await Task.WhenAll(
                RunFastLaneAsync(token),
                RunProcessLaneAsync(token),
                RunThermalLaneAsync(token),
                RunVolumeLaneAsync(token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispose による正常終了。
        }
    }

    private async Task RunFastLaneAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(_options.FastInterval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            // ループ本体の例外でレーンが静かに止まらないよう、待機以外はすべて try の内側で行う。
            try
            {
                LaneIterationHookForTest?.Invoke("fast");

                TimeSpan elapsed = stopwatch.Elapsed;
                stopwatch.Restart();

                CpuSnapshot cpu = SampleSafe(_cpu, _cpuFailures, elapsed, CpuSnapshot.Empty);
                MemorySnapshot memory = SampleSafe(_memory, _memoryFailures, elapsed, MemorySnapshot.Empty);
                DiskSnapshot rawDisk = SampleSafe(_disk, _diskFailures, elapsed, DiskSnapshot.Empty);
                DiskSnapshot disk = UpdateDiskAlertsSafe(rawDisk, elapsed);
                NetworkSnapshot network = SampleSafe(_network, _networkFailures, elapsed, NetworkSnapshot.Empty);
                GpuSnapshot gpu = SampleSafe(_gpu, _gpuFailures, elapsed, GpuSnapshot.Empty);

                Publish(cpu: cpu, memory: memory, disk: disk, network: network, gpu: gpu, appendHistory: true);
                _fastLaneFailures.RecordSuccess();
            }
            catch (Exception ex) when (!IsCancellation(ex, token))
            {
                _fastLaneFailures.RecordFailure(ex);
            }
        }
    }

    /// <summary>
    /// 高 Busy 継続警告を付与する。失敗しても fast レーンの公開を止めないよう、警告なしの生値で代用する。
    /// </summary>
    private DiskSnapshot UpdateDiskAlertsSafe(DiskSnapshot rawDisk, TimeSpan elapsed)
    {
        try
        {
            DiskSnapshot disk = _diskAlertTracker.Update(rawDisk, elapsed);
            _diskAlertFailures.RecordSuccess();
            return disk;
        }
        catch (Exception ex)
        {
            _diskAlertFailures.RecordFailure(ex);
            return rawDisk;
        }
    }

    private async Task RunProcessLaneAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        ProcessSnapshot last = ProcessSnapshot.Empty;
        using var timer = new PeriodicTimer(_options.ProcessInterval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                LaneIterationHookForTest?.Invoke("process");

                TimeSpan elapsed = stopwatch.Elapsed;
                stopwatch.Restart();
                last = Volatile.Read(ref _processSamplingEnabled) != 0
                    ? SampleSafe(_processes, _processFailures, elapsed, last)
                    : ProcessSnapshot.Empty;
                Publish(processes: last);
                _processLaneFailures.RecordSuccess();
            }
            catch (Exception ex) when (!IsCancellation(ex, token))
            {
                _processLaneFailures.RecordFailure(ex);
            }
        }
    }

    private async Task RunThermalLaneAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        ThermalSnapshot last = ThermalSnapshot.Empty;
        using var timer = new PeriodicTimer(_options.ThermalInterval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                LaneIterationHookForTest?.Invoke("thermal");

                TimeSpan elapsed = stopwatch.Elapsed;
                stopwatch.Restart();
                last = SampleSafe(_thermal, _thermalFailures, elapsed, last);
                Publish(thermal: last);
                _thermalLaneFailures.RecordSuccess();
            }
            catch (Exception ex) when (!IsCancellation(ex, token))
            {
                _thermalLaneFailures.RecordFailure(ex);
            }
        }
    }

    private async Task RunVolumeLaneAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<VolumeSnapshot> last = Array.Empty<VolumeSnapshot>();
        using var timer = new PeriodicTimer(_options.VolumeInterval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                LaneIterationHookForTest?.Invoke("volume");

                TimeSpan elapsed = stopwatch.Elapsed;
                stopwatch.Restart();
                last = SampleSafe(_volumes, _volumeFailures, elapsed, last);
                Publish(volumes: last);
                _volumeLaneFailures.RecordSuccess();
            }
            catch (Exception ex) when (!IsCancellation(ex, token))
            {
                _volumeLaneFailures.RecordFailure(ex);
            }
        }
    }

    /// <summary>Dispose による正常なキャンセルかどうか。それ以外の OperationCanceledException は失敗として扱う。</summary>
    private static bool IsCancellation(Exception ex, CancellationToken token) =>
        ex is OperationCanceledException && token.IsCancellationRequested;

    /// <summary>1つのレーンで更新された値を反映し、他レーンの直近値と合成して公開する。</summary>
    private void Publish(
        CpuSnapshot? cpu = null,
        MemorySnapshot? memory = null,
        DiskSnapshot? disk = null,
        NetworkSnapshot? network = null,
        GpuSnapshot? gpu = null,
        ProcessSnapshot? processes = null,
        ThermalSnapshot? thermal = null,
        IReadOnlyList<VolumeSnapshot>? volumes = null,
        bool appendHistory = false)
    {
        MetricsSnapshot snapshot;
        lock (_snapshotLock)
        {
            _lastCpu = cpu ?? _lastCpu;
            _lastMemory = memory ?? _lastMemory;
            _lastDisk = disk ?? _lastDisk;
            _lastNetwork = network ?? _lastNetwork;
            _lastGpu = gpu ?? _lastGpu;
            _lastProcesses = processes ?? _lastProcesses;
            _lastThermal = thermal ?? _lastThermal;
            _lastVolumes = volumes ?? _lastVolumes;

            snapshot = new MetricsSnapshot(
                Timestamp: DateTimeOffset.UtcNow,
                Cpu: _lastCpu,
                Memory: _lastMemory,
                Disk: _lastDisk,
                Network: _lastNetwork,
                Gpu: _lastGpu,
                Processes: _lastProcesses,
                Thermal: _lastThermal,
                Volumes: _lastVolumes);

            _latest = snapshot;
            if (appendHistory)
            {
                History.Append(snapshot);
            }
        }

        try
        {
            SnapshotAvailable?.Invoke(snapshot);
            _subscriberFailures.RecordSuccess();
        }
        catch (Exception ex)
        {
            // 購読側の例外でサンプリングループを止めない。記録は失敗開始時と回復時だけ。
            _subscriberFailures.RecordFailure(ex);
        }
    }

    private T SampleSafe<T>(IMetricProvider<T> provider, FailureTracker failures, TimeSpan elapsed, T fallback)
    {
        try
        {
            if (!provider.IsAvailable)
            {
                return fallback;
            }

            T value = provider.Sample(elapsed);
            failures.RecordSuccess();
            return value;
        }
        catch (Exception ex)
        {
            failures.RecordFailure(ex);
            return fallback;
        }
    }

    private void InitializeAll()
    {
        TryInitialize(_cpu);
        TryInitialize(_memory);
        TryInitialize(_disk);
        TryInitialize(_network);
        TryInitialize(_gpu);
        TryInitialize(_processes);
        TryInitialize(_thermal);
        TryInitialize(_volumes);
    }

    private void TryInitialize<T>(IMetricProvider<T> provider)
    {
        try
        {
            provider.Initialize();
        }
        catch (Exception ex)
        {
            // Provider の契約上ここには到達しないはずだが、防御的に握りつぶし、原因だけは記録する。
            LogSafe($"MetricsHub: {ProviderLabel(provider)} の初期化に失敗しました", ex);
        }
    }

    /// <summary>ログ sink を呼ぶ。sink 自体の例外はサンプリングに影響させない。</summary>
    private void LogSafe(string message, Exception? exception)
    {
        try
        {
            _options.Log?.Invoke(message, exception);
        }
        catch
        {
            // ログ出力の失敗でサンプリングを止めない。
        }
    }

    private static string ProviderLabel(object provider) => provider.GetType().Name;

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();

            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch
                {
                    // ループ終了時の例外は無視する。
                }
            }

            _cts.Dispose();
            _cts = null;
        }

        DisposeProvider(_cpu);
        DisposeProvider(_memory);
        DisposeProvider(_disk);
        DisposeProvider(_network);
        DisposeProvider(_gpu);
        DisposeProvider(_processes);
        DisposeProvider(_thermal);
        DisposeProvider(_volumes);
    }

    /// <summary>テスト用の MetricsHub を生成する。指定しなかったプロバイダは固定値を返すダミーになる。</summary>
    internal static MetricsHub CreateForTest(
        MetricsHubOptions? options = null,
        IMetricProvider<CpuSnapshot>? cpu = null,
        IMetricProvider<DiskSnapshot>? disk = null) =>
        new(
            options ?? new MetricsHubOptions(),
            cpu ?? new NullMetricProvider<CpuSnapshot>(CpuSnapshot.Empty),
            new NullMetricProvider<MemorySnapshot>(MemorySnapshot.Empty),
            disk ?? new NullMetricProvider<DiskSnapshot>(DiskSnapshot.Empty),
            new NullMetricProvider<NetworkSnapshot>(NetworkSnapshot.Empty),
            new NullMetricProvider<GpuSnapshot>(GpuSnapshot.Empty),
            new NullMetricProvider<ProcessSnapshot>(ProcessSnapshot.Empty),
            new NullThermalProvider(),
            new NullMetricProvider<IReadOnlyList<VolumeSnapshot>>(Array.Empty<VolumeSnapshot>()));

    /// <summary>テスト用にスナップショットを直接発行・履歴追加する。</summary>
    internal void PublishSnapshotForTest(MetricsSnapshot snapshot, bool appendHistory = true)
    {
        Publish(
            cpu: snapshot.Cpu,
            memory: snapshot.Memory,
            disk: snapshot.Disk,
            network: snapshot.Network,
            gpu: snapshot.Gpu,
            processes: snapshot.Processes,
            thermal: snapshot.Thermal,
            volumes: snapshot.Volumes,
            appendHistory: appendHistory);
    }

    private static void DisposeProvider<T>(IMetricProvider<T> provider)
    {
        try
        {
            provider.Dispose();
        }
        catch
        {
            // Dispose 中の例外は無視する。
        }
    }

    private sealed class NullMetricProvider<T>(T defaultValue) : IMetricProvider<T>
    {
        public string Name => "Null";
        public bool IsAvailable => true;
        public void Initialize() { }
        public T Sample(TimeSpan elapsed) => defaultValue;
        public void Dispose() { }
    }

    private sealed class NullThermalProvider : IThermalProvider
    {
        public string Name => "NullThermal";
        public bool IsAvailable => true;
        public bool RequiresElevation => false;
        public void Initialize() { }
        public ThermalSnapshot Sample(TimeSpan elapsed) => ThermalSnapshot.Empty;
        public void Dispose() { }
    }

    /// <summary>
    /// 1 つの失敗源（プロバイダ・購読者・レーン）の連続失敗状態を持ち、ログを
    /// 「失敗開始時に 1 回（例外付き）」と「回復時に 1 回（連続失敗回数）」だけに絞る。
    /// 購読者用は複数レーンから同時に触られるため、lock で保護する。
    /// </summary>
    private sealed class FailureTracker(string subject, Action<string, Exception?> log)
    {
        private readonly object _gate = new();
        private int _consecutiveFailures;

        public void RecordFailure(Exception exception)
        {
            bool isFirst;
            lock (_gate)
            {
                isFirst = ++_consecutiveFailures == 1;
            }

            if (isFirst)
            {
                log($"MetricsHub: {subject} で例外が発生しました（回復するまで以降の同種の失敗は記録しません）", exception);
            }
        }

        public void RecordSuccess()
        {
            int failures;
            lock (_gate)
            {
                failures = _consecutiveFailures;
                _consecutiveFailures = 0;
            }

            if (failures > 0)
            {
                log($"MetricsHub: {subject} が回復しました（連続失敗 {failures} 回）", null);
            }
        }
    }
}
