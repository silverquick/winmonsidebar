using System.Diagnostics;
using Monitor.Core.Abstractions;
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
    private CpuSnapshot _lastCpu = CpuSnapshot.Empty;
    private MemorySnapshot _lastMemory = MemorySnapshot.Empty;
    private DiskSnapshot _lastDisk = DiskSnapshot.Empty;
    private NetworkSnapshot _lastNetwork = NetworkSnapshot.Empty;
    private GpuSnapshot _lastGpu = GpuSnapshot.Empty;
    private ProcessSnapshot _lastProcesses = ProcessSnapshot.Empty;
    private ThermalSnapshot _lastThermal = ThermalSnapshot.Empty;
    private IReadOnlyList<VolumeSnapshot> _lastVolumes = Array.Empty<VolumeSnapshot>();

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
            TimeSpan elapsed = stopwatch.Elapsed;
            stopwatch.Restart();

            CpuSnapshot cpu = SampleSafe(_cpu, elapsed, CpuSnapshot.Empty);
            MemorySnapshot memory = SampleSafe(_memory, elapsed, MemorySnapshot.Empty);
            DiskSnapshot disk = SampleSafe(_disk, elapsed, DiskSnapshot.Empty);
            NetworkSnapshot network = SampleSafe(_network, elapsed, NetworkSnapshot.Empty);
            GpuSnapshot gpu = SampleSafe(_gpu, elapsed, GpuSnapshot.Empty);

            Publish(cpu: cpu, memory: memory, disk: disk, network: network, gpu: gpu, appendHistory: true);
        }
    }

    private async Task RunProcessLaneAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        ProcessSnapshot last = ProcessSnapshot.Empty;
        using var timer = new PeriodicTimer(_options.ProcessInterval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            TimeSpan elapsed = stopwatch.Elapsed;
            stopwatch.Restart();
            last = Volatile.Read(ref _processSamplingEnabled) != 0
                ? SampleSafe(_processes, elapsed, last)
                : ProcessSnapshot.Empty;
            Publish(processes: last);
        }
    }

    private async Task RunThermalLaneAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        ThermalSnapshot last = ThermalSnapshot.Empty;
        using var timer = new PeriodicTimer(_options.ThermalInterval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            TimeSpan elapsed = stopwatch.Elapsed;
            stopwatch.Restart();
            last = SampleSafe(_thermal, elapsed, last);
            Publish(thermal: last);
        }
    }

    private async Task RunVolumeLaneAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<VolumeSnapshot> last = Array.Empty<VolumeSnapshot>();
        using var timer = new PeriodicTimer(_options.VolumeInterval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            TimeSpan elapsed = stopwatch.Elapsed;
            stopwatch.Restart();
            last = SampleSafe(_volumes, elapsed, last);
            Publish(volumes: last);
        }
    }

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
        }
        catch
        {
            // 購読側の例外でサンプリングループを止めない。
        }
    }

    private static T SampleSafe<T>(IMetricProvider<T> provider, TimeSpan elapsed, T fallback)
    {
        try
        {
            if (!provider.IsAvailable)
            {
                return fallback;
            }

            return provider.Sample(elapsed);
        }
        catch
        {
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

    private static void TryInitialize<T>(IMetricProvider<T> provider)
    {
        try
        {
            provider.Initialize();
        }
        catch
        {
            // Provider の契約上ここには到達しないはずだが、防御的に無視する。
        }
    }

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
}
