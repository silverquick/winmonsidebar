namespace Monitor.Core.Models;

/// <summary>存在するプロバイダの連続取得失敗。未対応・非搭載とは別の状態。</summary>
[Flags]
public enum MetricSources
{
    None = 0, Cpu = 1, Memory = 2, Disk = 4, Network = 8, Gpu = 16,
    Processes = 32, Thermal = 64, Volumes = 128,
    Fast = Cpu | Memory | Disk | Network | Gpu,
    All = Fast | Processes | Thermal | Volumes,
}

public sealed record MetricsSnapshot(
    DateTimeOffset Timestamp,
    CpuSnapshot Cpu,
    MemorySnapshot Memory,
    DiskSnapshot Disk,
    NetworkSnapshot Network,
    GpuSnapshot Gpu,
    ProcessSnapshot Processes,
    ThermalSnapshot Thermal,
    IReadOnlyList<VolumeSnapshot> Volumes)
{
    public MetricSources UnavailableSources { get; init; }

    public static MetricsSnapshot Empty { get; } = new(
        Timestamp: DateTimeOffset.UnixEpoch,
        Cpu: CpuSnapshot.Empty,
        Memory: MemorySnapshot.Empty,
        Disk: DiskSnapshot.Empty,
        Network: NetworkSnapshot.Empty,
        Gpu: GpuSnapshot.Empty,
        Processes: ProcessSnapshot.Empty,
        Thermal: ThermalSnapshot.Empty,
        Volumes: Array.Empty<VolumeSnapshot>());
}
