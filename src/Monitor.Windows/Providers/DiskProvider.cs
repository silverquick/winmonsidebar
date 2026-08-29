using System.Globalization;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>Supplies <see cref="DiskSnapshot"/> for every physical disk on the machine, combining three
/// sources: PDH <c>\PhysicalDisk(*)\</c> counters for Read/Write/% Disk Time (per-second), StorageApi's
/// IOCTL_STORAGE_QUERY_PROPERTY temperature read (cached for a short interval; this same read also carries
/// the drive's self-reported warning/critical temperature thresholds, cached sticky per drive), and StorageApi's static
/// identity/capacity/volume-mapping enumeration (cached, refreshed every
/// <see cref="StaticInfoRefreshInterval"/> to tolerate disks being added/removed at runtime).
/// Physical disks that PDH does not report an instance for (e.g. it briefly lags a hot-plug) are still
/// included, with Read/Write/Busy reported as 0.</summary>
public sealed class DiskProvider : IMetricProvider<DiskSnapshot>
{
    private const string ReadCounterPath = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    private const string WriteCounterPath = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    private const string BusyCounterPath = @"\PhysicalDisk(*)\% Disk Time";
    private const string TotalInstanceName = "_Total";

    private static readonly TimeSpan StaticInfoRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TemperatureRefreshInterval = TimeSpan.FromSeconds(10);

    private PdhQuery? _query;
    private PdhMultiCounter? _readCounter;
    private PdhMultiCounter? _writeCounter;
    private PdhMultiCounter? _busyCounter;

    private IReadOnlyList<PhysicalDiskInfo> _physicalDisks = Array.Empty<PhysicalDiskInfo>();
    private IReadOnlyList<VolumeToDiskMapping> _volumes = Array.Empty<VolumeToDiskMapping>();
    private DateTime _lastStaticRefreshUtc = DateTime.MinValue;
    private DateTime _lastTemperatureRefreshUtc = DateTime.MinValue;

    // Warning/critical temperature thresholds are static per drive, but they arrive for free in the same
    // per-sample temperature IOCTL call (see Sample()) rather than the separate 30-second static-info
    // refresh, which doesn't touch temperature at all today - adding them there would mean a second,
    // redundant IOCTL per drive every refresh instead of reusing data already being read every sample.
    // Kept sticky (last known-good value per field) so a single transient read failure - which already
    // nulls out that sample's current temperature - doesn't also blank out an otherwise-known threshold.
    private readonly Dictionary<int, (double? Warning, double? Critical)> _temperatureThresholds = new();
    private readonly Dictionary<int, DiskTemperatureReading> _temperatureReadings = new();

    public string Name => "Disk";

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        try
        {
            PdhQuery? query = PdhQuery.TryCreate();
            if (query is not null)
            {
                PdhMultiCounter? readCounter = query.AddMultiCounter(ReadCounterPath);
                PdhMultiCounter? writeCounter = query.AddMultiCounter(WriteCounterPath);
                PdhMultiCounter? busyCounter = query.AddMultiCounter(BusyCounterPath);

                if (readCounter is not null && writeCounter is not null && busyCounter is not null)
                {
                    _query = query;
                    _readCounter = readCounter;
                    _writeCounter = writeCounter;
                    _busyCounter = busyCounter;

                    // Rate counters need a first Collect() here so the first real Sample() already has a
                    // meaningful (non-zero) baseline; the second Collect happens on the first Sample() call.
                    _query.Collect();
                }
                else
                {
                    query.Dispose();
                }
            }
        }
        catch
        {
            _query = null;
        }

        RefreshStaticInfo();
        RefreshTemperatures();

        // Available if either PDH counters work or StorageApi found at least one physical disk -
        // either source alone is still useful (e.g. PDH missing on a locked-down RDP session should not
        // hide disk identity/temperature, and PDH-only should not be blocked by a StorageApi hiccup).
        IsAvailable = _query is not null || _physicalDisks.Count > 0;
    }

    public DiskSnapshot Sample(TimeSpan elapsed)
    {
        if (!IsAvailable)
        {
            return DiskSnapshot.Empty;
        }

        try
        {
            if (DateTime.UtcNow - _lastStaticRefreshUtc >= StaticInfoRefreshInterval)
            {
                RefreshStaticInfo();
            }

            if (DateTime.UtcNow - _lastTemperatureRefreshUtc >= TemperatureRefreshInterval)
            {
                RefreshTemperatures();
            }

            (Dictionary<int, PdhDiskRates> byDrive, PdhDiskRates? total) = ReadPdhRates();

            var devices = new List<DiskDeviceSnapshot>(_physicalDisks.Count);

            foreach (PhysicalDiskInfo disk in _physicalDisks)
            {
                byDrive.TryGetValue(disk.DriveNumber, out PdhDiskRates rates);

                _temperatureReadings.TryGetValue(disk.DriveNumber, out DiskTemperatureReading temperatureReading);
                _temperatureThresholds.TryGetValue(disk.DriveNumber, out (double? Warning, double? Critical) thresholds);
                double? warningC = thresholds.Warning;
                double? criticalC = thresholds.Critical;
                List<LogicalVolumeSnapshot> volumes = BuildVolumes(disk.DriveNumber);

                string displayName = volumes.Count > 0
                    ? string.Join(' ', volumes.Select(v => v.DriveLetter))
                    : string.Create(CultureInfo.InvariantCulture, $"Disk {disk.DriveNumber}");

                devices.Add(new DiskDeviceSnapshot
                {
                    PhysicalDriveNumber = disk.DriveNumber,
                    Model = disk.Model,
                    BusType = disk.BusType,
                    IsSsd = disk.IsSsd,
                    CapacityBytes = disk.CapacityBytes,
                    ReadBytesPerSec = rates.Read,
                    WriteBytesPerSec = rates.Write,
                    BusyPercent = rates.Busy,
                    TemperatureC = temperatureReading.CurrentC,
                    WarningTemperatureC = warningC,
                    CriticalTemperatureC = criticalC,
                    Volumes = volumes,
                    DisplayName = displayName,
                });
            }

            devices.Sort((a, b) => a.PhysicalDriveNumber.CompareTo(b.PhysicalDriveNumber));

            double totalRead = total?.Read ?? devices.Sum(d => d.ReadBytesPerSec);
            double totalWrite = total?.Write ?? devices.Sum(d => d.WriteBytesPerSec);
            double totalBusy = total?.Busy ?? (devices.Count > 0 ? devices.Max(d => d.BusyPercent) : 0.0);

            return new DiskSnapshot
            {
                Devices = devices,
                TotalReadBytesPerSec = totalRead,
                TotalWriteBytesPerSec = totalWrite,
                BusyPercent = ClampPercent(totalBusy),
            };
        }
        catch
        {
            return DiskSnapshot.Empty;
        }
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
        _readCounter = null;
        _writeCounter = null;
        _busyCounter = null;
        _physicalDisks = Array.Empty<PhysicalDiskInfo>();
        _volumes = Array.Empty<VolumeToDiskMapping>();
        _temperatureThresholds.Clear();
        _temperatureReadings.Clear();
        IsAvailable = false;
    }

    /// <summary>Merges this sample's warning/critical threshold reading into the sticky per-drive cache
    /// (field by field, so a threshold that came back null this sample - e.g. a transient IOCTL hiccup -
    /// doesn't clobber a previously known-good value for that same field) and returns the resulting pair
    /// to use for this sample.</summary>
    private (double? Warning, double? Critical) UpdateAndGetThresholds(int driveNumber, DiskTemperatureReading reading)
    {
        _temperatureThresholds.TryGetValue(driveNumber, out (double? Warning, double? Critical) previous);

        double? warning = reading.WarningC ?? previous.Warning;
        double? critical = reading.CriticalC ?? previous.Critical;

        if (warning.HasValue || critical.HasValue)
        {
            _temperatureThresholds[driveNumber] = (warning, critical);
        }

        return (warning, critical);
    }

    /// <summary>ディスクごとの温度 IOCTL をまとめて実行し、短時間キャッシュする。
    /// 温度は 1 秒単位で変化を追う必要がなく、この周期にすることで常駐時のハンドル操作を削減する。</summary>
    private void RefreshTemperatures()
    {
        foreach (PhysicalDiskInfo disk in _physicalDisks)
        {
            DiskTemperatureReading reading;
            try
            {
                reading = StorageApi.TryReadTemperature(disk.DriveNumber);
            }
            catch
            {
                reading = default;
            }

            _temperatureReadings[disk.DriveNumber] = reading;
            UpdateAndGetThresholds(disk.DriveNumber, reading);
        }

        _lastTemperatureRefreshUtc = DateTime.UtcNow;
    }

    /// <summary>Re-reads physical disk identity/capacity and physical&lt;-&gt;logical volume mapping from
    /// StorageApi. Cheap-ish (opens every \\.\PhysicalDriveN and every fixed \\.\X: once) but not free,
    /// hence only called from Initialize() and every StaticInfoRefreshInterval thereafter. Never throws.</summary>
    private void RefreshStaticInfo()
    {
        try
        {
            _physicalDisks = StorageApi.EnumeratePhysicalDisks();
        }
        catch
        {
            _physicalDisks = Array.Empty<PhysicalDiskInfo>();
        }

        try
        {
            _volumes = StorageApi.EnumerateFixedVolumesWithDiskMapping();
        }
        catch
        {
            _volumes = Array.Empty<VolumeToDiskMapping>();
        }

        _lastStaticRefreshUtc = DateTime.UtcNow;
    }

    private List<LogicalVolumeSnapshot> BuildVolumes(int driveNumber)
    {
        var result = new List<LogicalVolumeSnapshot>();

        foreach (VolumeToDiskMapping volume in _volumes)
        {
            if (volume.DiskNumber != driveNumber)
            {
                continue;
            }

            double usedPercent = volume.TotalBytes > 0
                ? ClampPercent(100.0 * (volume.TotalBytes - volume.FreeBytes) / volume.TotalBytes)
                : 0.0;

            result.Add(new LogicalVolumeSnapshot
            {
                DriveLetter = volume.DriveLetter,
                Label = volume.Label,
                TotalBytes = volume.TotalBytes,
                FreeBytes = volume.FreeBytes,
                UsedPercent = usedPercent,
            });
        }

        return result;
    }

    /// <summary>Collects and reads the three PDH counters, splitting instance values into a per-physical-
    /// -drive-number lookup (parsed from the leading digit token of instance names like "0 C:") and the
    /// separate "_Total" instance. Returns an empty dictionary and null total if PDH is unavailable.</summary>
    private (Dictionary<int, PdhDiskRates> ByDrive, PdhDiskRates? Total) ReadPdhRates()
    {
        var byDrive = new Dictionary<int, PdhDiskRates>();

        if (_query is null || _readCounter is null || _writeCounter is null || _busyCounter is null)
        {
            return (byDrive, null);
        }

        _query.Collect();

        Dictionary<string, double> reads = ToLookup(_readCounter.GetValues());
        Dictionary<string, double> writes = ToLookup(_writeCounter.GetValues());
        Dictionary<string, double> busies = ToLookup(_busyCounter.GetValues());

        var instanceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in reads.Keys) instanceNames.Add(name);
        foreach (string name in writes.Keys) instanceNames.Add(name);
        foreach (string name in busies.Keys) instanceNames.Add(name);

        PdhDiskRates? total = null;

        foreach (string instanceName in instanceNames)
        {
            double read = reads.GetValueOrDefault(instanceName, 0.0);
            double write = writes.GetValueOrDefault(instanceName, 0.0);
            double busy = ClampPercent(busies.GetValueOrDefault(instanceName, 0.0));
            var rates = new PdhDiskRates(read, write, busy);

            if (string.Equals(instanceName, TotalInstanceName, StringComparison.OrdinalIgnoreCase))
            {
                total = rates;
                continue;
            }

            if (TryParseDriveNumber(instanceName, out int driveNumber))
            {
                byDrive[driveNumber] = rates;
            }
        }

        return (byDrive, total);
    }

    /// <summary>Parses the leading physical-disk-number token from a PDH PhysicalDisk instance name,
    /// e.g. "0 C:" -&gt; 0, "1 D: E:" -&gt; 1. Returns false for anything that does not start with digits
    /// (notably "_Total").</summary>
    private static bool TryParseDriveNumber(string instanceName, out int driveNumber)
    {
        string trimmed = instanceName.Trim();
        int spaceIndex = trimmed.IndexOf(' ');
        string numberToken = spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
        return int.TryParse(numberToken, NumberStyles.None, CultureInfo.InvariantCulture, out driveNumber);
    }

    private static double ClampPercent(double value) => Math.Clamp(value, 0.0, 100.0);

    private static Dictionary<string, double> ToLookup(IReadOnlyList<PdhCounterItem> items)
    {
        var lookup = new Dictionary<string, double>(items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (PdhCounterItem item in items)
        {
            lookup[item.InstanceName] = item.Value;
        }

        return lookup;
    }

    private readonly record struct PdhDiskRates(double Read, double Write, double Busy);
}
