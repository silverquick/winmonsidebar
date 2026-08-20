namespace Monitor.Core.Models;

public readonly record struct SensorReading(string Name, double Value);

/// <summary>
/// マザーボード/CPU の温度・ファン・電力。LibreHardwareMonitor 由来（管理者時のみ中身が入る）。
/// </summary>
public sealed record ThermalSnapshot
{
    public bool IsElevated { get; init; }
    public bool IsAvailable { get; init; }
    public string Source { get; init; } = "none";        // "LibreHardwareMonitor" / "none"
    public double? CpuPackageTemperatureC { get; init; }
    public IReadOnlyList<SensorReading> CpuCoreTemperatures { get; init; } = Array.Empty<SensorReading>();
    public double? CpuPackagePowerWatts { get; init; }
    public double? MotherboardTemperatureC { get; init; }
    public double? VrmTemperatureC { get; init; }
    public IReadOnlyList<SensorReading> Fans { get; init; } = Array.Empty<SensorReading>();  // RPM

    /// <summary>LHM が拾えた追加の温度（チップセット等）。名前そのままで並べる。</summary>
    public IReadOnlyList<SensorReading> OtherTemperatures { get; init; } = Array.Empty<SensorReading>();

    /// <summary>LHM から取れたディスク温度。PhysicalDriveNumber ではなくモデル名で照合する。</summary>
    public IReadOnlyList<SensorReading> StorageTemperatures { get; init; } = Array.Empty<SensorReading>();

    public static ThermalSnapshot Empty { get; } = new();
}
