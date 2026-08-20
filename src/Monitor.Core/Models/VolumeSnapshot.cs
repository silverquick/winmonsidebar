namespace Monitor.Core.Models;

public enum VolumeKind
{
    Unknown,
    Fixed,
    Network,
    Removable,
    CdRom,
    Ram,
}

/// <summary>One logical drive (drive letter), independent of whether it maps to a local physical disk.
/// Network drives (<see cref="Kind"/> == <see cref="VolumeKind.Network"/>) never resolve to a
/// <see cref="PhysicalDriveNumber"/>; local fixed/removable drives do when the mapping can be
/// resolved. A drive whose capacity query failed is still present in the list with
/// <see cref="IsReady"/> == false and zeroed capacity fields, never omitted.</summary>
public sealed record VolumeSnapshot
{
    public string DriveLetter { get; init; } = "";      // "C:"
    public string? Label { get; init; }                 // "System"
    public string? FileSystem { get; init; }             // "NTFS"
    public VolumeKind Kind { get; init; }
    public string? NetworkPath { get; init; }            // Network のとき "\\yuzu.local\00000000"
    public ulong TotalBytes { get; init; }
    public ulong FreeBytes { get; init; }
    public ulong UsedBytes { get; init; }
    public double UsedPercent { get; init; }
    public int? PhysicalDriveNumber { get; init; }       // 紐づく物理ディスク。NW なら null
    public bool IsReady { get; init; }

    public static VolumeSnapshot Empty { get; } = new();
}
