using System.Diagnostics;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;

namespace Monitor.Core;

public sealed class MetricsHubOptions
{
    /// <summary>CPU/Mem/Disk/Net/GPU をサンプリングする間隔。</summary>
    public TimeSpan FastInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>プロセス一覧をサンプリングする間隔。</summary>
    public TimeSpan SlowInterval { get; init; } = TimeSpan.FromSeconds(2);

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

        History = new MetricsHistory();
    }

    /// <summary>
    /// バックグラウンドスレッド上で発火する。購読側が UI へのマーシャリング責任を持つ。
    /// </summary>
    public event Action<MetricsSnapshot>? SnapshotAvailable;

    public MetricsSnapshot? Latest => _latest;

    public MetricsHistory History { get; }

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

        var fastStopwatch = Stopwatch.StartNew();
        var slowStopwatch = Stopwatch.StartNew();
        ProcessSnapshot lastProcesses = ProcessSnapshot.Empty;
        ThermalSnapshot lastThermal = ThermalSnapshot.Empty;
        IReadOnlyList<VolumeSnapshot> lastVolumes = Array.Empty<VolumeSnapshot>();

        using var timer = new PeriodicTimer(_options.FastInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                TimeSpan fastElapsed = fastStopwatch.Elapsed;
                fastStopwatch.Restart();

                CpuSnapshot cpu = SampleSafe(_cpu, fastElapsed, CpuSnapshot.Empty);
                MemorySnapshot memory = SampleSafe(_memory, fastElapsed, MemorySnapshot.Empty);
                DiskSnapshot disk = SampleSafe(_disk, fastElapsed, DiskSnapshot.Empty);
                NetworkSnapshot network = SampleSafe(_network, fastElapsed, NetworkSnapshot.Empty);
                GpuSnapshot gpu = SampleSafe(_gpu, fastElapsed, GpuSnapshot.Empty);

                if (slowStopwatch.Elapsed >= _options.SlowInterval)
                {
                    TimeSpan slowElapsed = slowStopwatch.Elapsed;
                    slowStopwatch.Restart();
                    lastProcesses = SampleSafe(_processes, slowElapsed, lastProcesses);
                    lastThermal = SampleSafe(_thermal, slowElapsed, lastThermal);
                    lastVolumes = SampleSafe(_volumes, slowElapsed, lastVolumes);
                }

                var snapshot = new MetricsSnapshot(
                    Timestamp: DateTimeOffset.UtcNow,
                    Cpu: cpu,
                    Memory: memory,
                    Disk: disk,
                    Network: network,
                    Gpu: gpu,
                    Processes: lastProcesses,
                    Thermal: lastThermal,
                    Volumes: lastVolumes);

                _latest = snapshot;
                History.Append(snapshot);

                try
                {
                    SnapshotAvailable?.Invoke(snapshot);
                }
                catch
                {
                    // 購読側の例外でサンプリングループを止めない。
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose による正常終了。
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
