namespace Monitor.Core.Models;

public sealed record CpuSnapshot
{
    public string ModelName { get; init; } = "";
    public double TotalUsagePercent { get; init; }
    public IReadOnlyList<double> PerCoreUsagePercent { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> PerCoreClockMhz { get; init; } = Array.Empty<double>();
    public double CurrentClockMhz { get; init; }
    public double BaseClockMhz { get; init; }
    public int PhysicalCoreCount { get; init; }
    public int LogicalCoreCount { get; init; }
    public double? PackageTemperatureC { get; init; }   // LHM 由来。無ければ null
    public double? PackagePowerWatts { get; init; }     // LHM 由来。無ければ null

    public static CpuSnapshot Empty { get; } = new();
}
