using Monitor.Core.Alerts;

namespace Monitor.Core.Models;

public sealed record LogicalVolumeSnapshot
{
    public string DriveLetter { get; init; } = "";   // "C:"
    public string? Label { get; init; }
    public ulong TotalBytes { get; init; }
    public ulong FreeBytes { get; init; }
    public double UsedPercent { get; init; }
}

public sealed record DiskDeviceSnapshot
{
    public int PhysicalDriveNumber { get; init; } = -1;
    public string Model { get; init; } = "";          // "WD Blue SN580 1TB"
    public string BusType { get; init; } = "";        // "NVMe" / "SATA" / "USB"
    public bool IsSsd { get; init; }
    public ulong CapacityBytes { get; init; }
    public double ReadBytesPerSec { get; init; }
    public double WriteBytesPerSec { get; init; }
    public double BusyPercent { get; init; }
    public AlertLevel BusyAlertLevel { get; init; } = AlertLevel.None;
    public double? TemperatureC { get; init; }

    /// <summary>ドライブ自身が申告する警告温度（摂氏）。SSD は 70°C 前後、HDD は 55°C 前後など機種ごとに
    /// 大きく異なるため、一律の定数閾値より正確。取得できないドライブでは null。</summary>
    public double? WarningTemperatureC { get; init; }

    /// <summary>ドライブ自身が申告する臨界温度（摂氏）。機種ごとに大きく異なるため、一律の定数閾値より
    /// 正確。取得できないドライブでは null。</summary>
    public double? CriticalTemperatureC { get; init; }

    public IReadOnlyList<LogicalVolumeSnapshot> Volumes { get; init; } = Array.Empty<LogicalVolumeSnapshot>();

    /// <summary>UI 表示用の短い識別名。ドライブレターがあれば "C: D:"、無ければ "Disk 3"。</summary>
    public string DisplayName { get; init; } = "";

    public static DiskDeviceSnapshot Empty { get; } = new();
}

public sealed record DiskSnapshot
{
    public IReadOnlyList<DiskDeviceSnapshot> Devices { get; init; } = Array.Empty<DiskDeviceSnapshot>();
    public double TotalReadBytesPerSec { get; init; }
    public double TotalWriteBytesPerSec { get; init; }
    public double BusyPercent { get; init; }

    public static DiskSnapshot Empty { get; } = new();
}
