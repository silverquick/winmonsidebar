using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Monitor.Windows.Native;

/// <summary>Static identity/capacity info for one physical disk, as read once via
/// IOCTL_STORAGE_QUERY_PROPERTY / IOCTL_DISK_GET_LENGTH_INFO. Cheap to re-enumerate but not free
/// (opens every \\.\PhysicalDriveN), so callers should cache and refresh periodically.</summary>
public readonly record struct PhysicalDiskInfo(int DriveNumber, string Model, string BusType, bool IsSsd, ulong CapacityBytes);

/// <summary>One fixed logical volume and the physical disk number it lives on, as read via
/// IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS. Volumes that fail to resolve to a physical disk (network
/// drives, some virtual drives) are omitted by the caller of EnumerateFixedVolumesWithDiskMapping.</summary>
public readonly record struct VolumeToDiskMapping(string DriveLetter, string? Label, ulong TotalBytes, ulong FreeBytes, int DiskNumber);

/// <summary>Current temperature plus the drive-reported warning/critical thresholds, all three parsed
/// from a single STORAGE_TEMPERATURE_DATA_DESCRIPTOR read (see ParseTemperature). Thresholds vary a lot
/// by drive model (SSDs commonly warn around 70C, HDDs around 55C), so a caller building a warning UI
/// should prefer these over any hardcoded constant, and only fall back to one when a drive doesn't
/// report it (WarningC/CriticalC are null). A struct rather than a tuple/out-params to match this file's
/// existing convention for multi-field IOCTL results (see PhysicalDiskInfo, VolumeToDiskMapping above),
/// and non-nullable (with an Empty sentinel of all-null fields) rather than a nullable struct, since
/// "nothing read" and "read but nothing valid" are the same case here and callers already have to check
/// each field individually.</summary>
public readonly record struct DiskTemperatureReading(double? CurrentC, double? WarningC, double? CriticalC)
{
    public static DiskTemperatureReading Empty { get; } = new();
}

/// <summary>
/// Access to physical-disk identity/capacity/temperature and physical-to-logical-volume mapping via
/// low-level storage IOCTLs. Every public method here never throws: failures are represented as an
/// empty list / null, matching the "no exception ever leaves a Provider" rule for this codebase.
/// All handles are opened with dwDesiredAccess=0 (query-only), which does not require administrator
/// privileges.
/// </summary>
public static partial class StorageApi
{
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    // IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, not IOCTL_DISK_GET_LENGTH_INFO: the latter is
    // CTL_CODE(..., FILE_READ_ACCESS) and DeviceIoControl rejects it on a handle opened with
    // dwDesiredAccess=0 (ERROR_ACCESS_DENIED) even without administrator rights. GET_DRIVE_GEOMETRY_EX
    // is CTL_CODE(..., FILE_ANY_ACCESS) and works on the same zero-access handle everything else here
    // uses.
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageDeviceTemperatureProperty = 52;
    private const int PropertyStandardQuery = 0;

    private const int MaxPhysicalDriveIndex = 32;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>Enumerates every physical disk that can be opened (drive numbers 0..31, non-contiguous
    /// gaps are tolerated), returning identity/bus-type/SSD-vs-HDD/capacity for each. Never throws.</summary>
    public static IReadOnlyList<PhysicalDiskInfo> EnumeratePhysicalDisks()
    {
        var results = new List<PhysicalDiskInfo>();

        for (int driveNumber = 0; driveNumber < MaxPhysicalDriveIndex; driveNumber++)
        {
            try
            {
                using SafeFileHandle handle = OpenPhysicalDrive(driveNumber);
                if (handle.IsInvalid)
                {
                    continue;
                }

                string model = "";
                string busType = "Unknown";
                if (TryQueryStorageProperty(handle, StorageDeviceProperty, PropertyStandardQuery, 1024, out byte[] descriptor))
                {
                    (model, busType) = ParseDeviceDescriptor(descriptor);
                }

                bool? incursSeekPenalty = TryQueryStorageProperty(handle, StorageDeviceSeekPenaltyProperty, PropertyStandardQuery, 32, out byte[] seekPenalty)
                    ? ParseSeekPenalty(seekPenalty)
                    : null;

                bool isSsd = incursSeekPenalty.HasValue
                    ? !incursSeekPenalty.Value
                    : string.Equals(busType, "NVMe", StringComparison.OrdinalIgnoreCase);

                ulong capacityBytes = TryReadCapacity(handle) ?? 0UL;

                results.Add(new PhysicalDiskInfo(
                    driveNumber,
                    string.IsNullOrWhiteSpace(model) ? $"Disk {driveNumber}" : model,
                    busType,
                    isSsd,
                    capacityBytes));
            }
            catch
            {
                // Skip this drive number; keep enumerating the rest.
            }
        }

        return results;
    }

    /// <summary>Reads the current composite temperature of one physical disk, plus its drive-reported
    /// warning/critical thresholds, via a single IOCTL_STORAGE_QUERY_PROPERTY/
    /// StorageDeviceTemperatureProperty call (the thresholds live in the same
    /// STORAGE_TEMPERATURE_DATA_DESCRIPTOR buffer as the temperature reading itself, so this never issues
    /// more than the one IOCTL). Returns DiskTemperatureReading.Empty (never throws) if the disk does not
    /// support temperature reporting (e.g. ERROR_INVALID_FUNCTION) or reports only out-of-range
    /// values.</summary>
    public static DiskTemperatureReading TryReadTemperature(int driveNumber)
    {
        try
        {
            using SafeFileHandle handle = OpenPhysicalDrive(driveNumber);
            if (handle.IsInvalid)
            {
                return DiskTemperatureReading.Empty;
            }

            if (!TryQueryStorageProperty(handle, StorageDeviceTemperatureProperty, PropertyStandardQuery, 1024, out byte[] data))
            {
                return DiskTemperatureReading.Empty;
            }

            return ParseTemperature(data);
        }
        catch
        {
            return DiskTemperatureReading.Empty;
        }
    }

    /// <summary>Enumerates fixed (local, non-removable) logical volumes and resolves each one to the
    /// physical disk number it lives on via IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS. Volumes that are not
    /// ready, throw on their DriveInfo properties, or fail to resolve to a physical disk (network
    /// drives, some virtual/cloud drives) are silently skipped. Never throws.</summary>
    public static IReadOnlyList<VolumeToDiskMapping> EnumerateFixedVolumesWithDiskMapping()
    {
        var results = new List<VolumeToDiskMapping>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return results;
        }

        foreach (DriveInfo drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                string letter = drive.Name.TrimEnd('\\');
                int? diskNumber = TryGetDiskNumberForVolume($@"\\.\{letter}");
                if (diskNumber is null)
                {
                    continue;
                }

                string? label;
                try
                {
                    label = drive.VolumeLabel;
                }
                catch
                {
                    label = null;
                }

                ulong total = 0;
                try
                {
                    total = (ulong)drive.TotalSize;
                }
                catch
                {
                    // leave at 0
                }

                ulong free = 0;
                try
                {
                    free = (ulong)drive.AvailableFreeSpace;
                }
                catch
                {
                    // leave at 0
                }

                results.Add(new VolumeToDiskMapping(letter, string.IsNullOrEmpty(label) ? null : label, total, free, diskNumber.Value));
            }
            catch
            {
                // Skip this volume; keep enumerating the rest.
            }
        }

        return results;
    }

    /// <summary>Resolves the physical disk number that one logical drive letter lives on via
    /// IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS. Returns null (never throws) for drives that do not map to
    /// a single physical disk (network drives, some virtual/cloud drives) or that fail to open.
    /// <paramref name="driveLetter"/> may be given as "C:", "C:\" or "C" - it is normalized internally.</summary>
    public static int? TryGetPhysicalDriveNumber(string driveLetter)
    {
        try
        {
            string trimmed = driveLetter.TrimEnd('\\');
            if (!trimmed.EndsWith(':'))
            {
                trimmed += ":";
            }

            return TryGetDiskNumberForVolume($@"\\.\{trimmed}");
        }
        catch
        {
            return null;
        }
    }

    private static SafeFileHandle OpenPhysicalDrive(int driveNumber)
        => CreateFileW($@"\\.\PhysicalDrive{driveNumber}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    private static int? TryGetDiskNumberForVolume(string volumePath)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(volumePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }

            IntPtr outBuffer = Marshal.AllocHGlobal(1024);
            try
            {
                bool ok = DeviceIoControl(handle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, outBuffer, 1024, out uint returned, IntPtr.Zero);
                if (!ok || returned < 12)
                {
                    return null;
                }

                int extentCount = Marshal.ReadInt32(outBuffer, 0);
                if (extentCount <= 0)
                {
                    return null;
                }

                // VOLUME_DISK_EXTENTS { ULONG NumberOfDiskExtents; DISK_EXTENT Extents[]; }, DISK_EXTENT
                // starts with a ULONG DiskNumber but is itself 8-byte aligned (it also holds two
                // LARGE_INTEGER fields), so 4 bytes of padding sit between NumberOfDiskExtents and the
                // first extent: DiskNumber of Extents[0] is at absolute offset 8, not 4.
                return Marshal.ReadInt32(outBuffer, 8);
            }
            finally
            {
                Marshal.FreeHGlobal(outBuffer);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ulong? TryReadCapacity(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX { DISK_GEOMETRY Geometry; LARGE_INTEGER DiskSize; UCHAR Data[1]; }, where
        // DISK_GEOMETRY is { LARGE_INTEGER Cylinders; MEDIA_TYPE MediaType; DWORD TracksPerCylinder;
        // DWORD SectorsPerTrack; DWORD BytesPerSector; } (24 bytes). DiskSize is the total byte count
        // and, being a LARGE_INTEGER, is naturally 8-byte-aligned right after Geometry at offset 24.
        const int outputSize = 64;
        IntPtr outBuffer = Marshal.AllocHGlobal(outputSize);
        try
        {
            bool ok = DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0, outBuffer, outputSize, out uint returned, IntPtr.Zero);
            if (!ok || returned < 32)
            {
                return null;
            }

            long length = Marshal.ReadInt64(outBuffer, 24);
            return length > 0 ? (ulong)length : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    /// <summary>Sends IOCTL_STORAGE_QUERY_PROPERTY and returns the raw output buffer (trimmed to the
    /// number of bytes actually written). Returns false (never throws) on any failure, including the
    /// ERROR_INVALID_FUNCTION a disk reports when it does not support the requested property.</summary>
    private static bool TryQueryStorageProperty(SafeFileHandle handle, int propertyId, int queryType, int outputSize, out byte[] output)
    {
        output = Array.Empty<byte>();

        // STORAGE_PROPERTY_QUERY { ULONG PropertyId; ULONG QueryType; UCHAR AdditionalParameters[1]; },
        // 12 bytes once padded to 4-byte alignment. AdditionalParameters is left zeroed.
        IntPtr inBuffer = Marshal.AllocHGlobal(12);
        IntPtr outBuffer = Marshal.AllocHGlobal(outputSize);
        try
        {
            Marshal.WriteInt32(inBuffer, 0, propertyId);
            Marshal.WriteInt32(inBuffer, 4, queryType);
            Marshal.WriteInt32(inBuffer, 8, 0);

            bool ok = DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, inBuffer, 12, outBuffer, (uint)outputSize, out uint returned, IntPtr.Zero);
            if (!ok)
            {
                return false;
            }

            int length = (int)Math.Min(returned, (uint)outputSize);
            output = new byte[length];
            Marshal.Copy(outBuffer, output, 0, length);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    /// <summary>Parses a STORAGE_DEVICE_DESCRIPTOR buffer into (Model, BusType). Model prefers ProductId
    /// (where the full ATA/NVMe model string usually lives) and falls back to VendorId.</summary>
    private static (string Model, string BusType) ParseDeviceDescriptor(byte[] data)
    {
        // Offsets (x64, default ULONG/UCHAR/enum layout, no 8-byte members):
        //   0 Version, 4 Size, 8 DeviceType, 9 DeviceTypeModifier, 10 RemovableMedia, 11 CommandQueueing,
        //   12 VendorIdOffset, 16 ProductIdOffset, 20 ProductRevisionOffset, 24 SerialNumberOffset,
        //   28 BusType, 32 RawPropertiesLength, 36 RawDeviceProperties[]
        if (data.Length < 32)
        {
            return ("", "Unknown");
        }

        uint vendorOffset = BitConverter.ToUInt32(data, 12);
        uint productOffset = BitConverter.ToUInt32(data, 16);
        uint busTypeRaw = BitConverter.ToUInt32(data, 28);

        string vendor = ReadAnsiStringAt(data, vendorOffset);
        string product = ReadAnsiStringAt(data, productOffset);
        string model = !string.IsNullOrWhiteSpace(product) ? product : vendor;

        return (model, MapBusType((int)busTypeRaw));
    }

    private static string ReadAnsiStringAt(byte[] data, uint offset)
    {
        // Offset 0 is the documented "not present" sentinel (it would point back at the descriptor's
        // own Version field, never a real string).
        if (offset == 0 || offset >= (uint)data.Length)
        {
            return "";
        }

        int start = (int)offset;
        int end = start;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return end > start ? Encoding.ASCII.GetString(data, start, end - start).Trim() : "";
    }

    private static string MapBusType(int busType) => busType switch
    {
        0x01 => "SCSI",
        0x02 => "ATAPI",
        0x03 => "ATA",
        0x04 => "1394",
        0x05 => "SSA",
        0x06 => "Fibre",
        0x07 => "USB",
        0x08 => "RAID",
        0x09 => "iSCSI",
        0x0A => "SAS",
        0x0B => "SATA",
        0x0C => "SD",
        0x0D => "MMC",
        0x0E => "Virtual",
        0x0F => "FileBackedVirtual",
        0x10 => "Spaces",
        0x11 => "NVMe",
        0x12 => "SCM",
        0x13 => "UFS",
        _ => "Unknown",
    };

    /// <summary>Parses a DEVICE_SEEK_PENALTY_DESCRIPTOR buffer. Returns null if the buffer is too short
    /// to contain the IncursSeekPenalty byte (offset 8).</summary>
    private static bool? ParseSeekPenalty(byte[] data)
        => data.Length > 8 ? data[8] != 0 : null;

    /// <summary>Parses a STORAGE_TEMPERATURE_DATA_DESCRIPTOR buffer into the current composite
    /// temperature (sensor Index 0, falling back to the first reported sensor if no entry is explicitly
    /// indexed 0) plus the drive-reported warning/critical thresholds from the header. Returns
    /// DiskTemperatureReading.Empty for an empty/undersized buffer. Each of the three fields is validated
    /// independently against the same "invalid reading" convention used elsewhere in this codebase's
    /// callers (&lt;=0 or &gt;150 degrees C is treated as absent) - drives commonly report 0, or don't
    /// implement a field at all and leave it zeroed, so every field can be individually null even when
    /// the others are valid.</summary>
    private static DiskTemperatureReading ParseTemperature(byte[] data)
    {
        // Header: 0 Version, 4 Size, 8 CriticalTemperature, 10 WarningTemperature, 12 InfoCount,
        // 14 Reserved0[2], 16 Reserved1[2*ULONG]; TemperatureInfo[] starts at 24, 16 bytes each with
        // Index at +0 (WORD) and Temperature at +2 (SHORT).
        const int headerSize = 24;
        const int entrySize = 16;

        if (data.Length < headerSize + entrySize)
        {
            return DiskTemperatureReading.Empty;
        }

        double? critical = ValidateTemperature(BitConverter.ToInt16(data, 8));
        double? warning = ValidateTemperature(BitConverter.ToInt16(data, 10));

        // A drive that reports a warning threshold at or above its own critical threshold isn't giving a
        // usable pair - either it doesn't really implement these fields and is echoing back zeroed/
        // garbage memory that happened to pass the >0/<=150 check, or it's a firmware quirk. Either way,
        // showing it to the user as a meaningful warning/critical boundary would be misleading, so the
        // whole pair is distrusted rather than picking one of the two arbitrarily.
        if (warning.HasValue && critical.HasValue && warning.Value >= critical.Value)
        {
            warning = null;
            critical = null;
        }

        ushort infoCount = BitConverter.ToUInt16(data, 12);
        int available = (data.Length - headerSize) / entrySize;
        int count = Math.Min(infoCount, available);

        short? temperature = null;
        if (count > 0)
        {
            for (int i = 0; i < count; i++)
            {
                int entryOffset = headerSize + (i * entrySize);
                ushort index = BitConverter.ToUInt16(data, entryOffset);
                if (index == 0)
                {
                    temperature = BitConverter.ToInt16(data, entryOffset + 2);
                    break;
                }
            }

            // No entry explicitly indexed 0: fall back to whatever the first reported sensor is.
            temperature ??= BitConverter.ToInt16(data, headerSize + 2);
        }

        return new DiskTemperatureReading(ValidateTemperature(temperature), warning, critical);
    }

    /// <summary>Applies the "invalid reading" convention (&lt;=0 or &gt;150 degrees C is treated as
    /// absent/not-implemented) shared by the current-temperature, warning-threshold and
    /// critical-threshold fields of STORAGE_TEMPERATURE_DATA_DESCRIPTOR.</summary>
    private static double? ValidateTemperature(short? value)
        => value is > 0 and <= 150 ? value.Value : null;
}
