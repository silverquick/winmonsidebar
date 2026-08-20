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
    public double? TemperatureC { get; init; }
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
