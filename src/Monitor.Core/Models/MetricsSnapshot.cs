namespace Monitor.Core.Models;

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
