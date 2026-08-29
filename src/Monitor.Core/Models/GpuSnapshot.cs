namespace Monitor.Core.Models;

public sealed record GpuAdapterSnapshot
{
    public string Name { get; init; } = "";
    public long Luid { get; init; }
    public double UsagePercent { get; init; }
    public double Engine3DPercent { get; init; }
    public double EngineCopyPercent { get; init; }
    public double EngineVideoPercent { get; init; }
    public double EngineComputePercent { get; init; }
    public ulong DedicatedUsedBytes { get; init; }
    public ulong DedicatedTotalBytes { get; init; }

    // ↓ NVAPI 由来（非 NVIDIA / 取得失敗なら null）
    public double? TemperatureC { get; init; }
    public double? HotspotTemperatureC { get; init; }
    public double? MemoryTemperatureC { get; init; }
    public double? FanPercent { get; init; }
    public int? FanRpm { get; init; }
    public double? PowerWatts { get; init; }
    public double? PowerLimitWatts { get; init; }
    public double? CoreClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
    public string? DriverVersion { get; init; }

    public static GpuAdapterSnapshot Empty { get; } = new();
}

public sealed record GpuSnapshot
{
    public IReadOnlyList<GpuAdapterSnapshot> Adapters { get; init; } = Array.Empty<GpuAdapterSnapshot>();
    public double TotalUsagePercent { get; init; }

    public static GpuSnapshot Empty { get; } = new();
}
