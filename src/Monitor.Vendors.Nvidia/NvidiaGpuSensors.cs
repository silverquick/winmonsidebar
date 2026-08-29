using System.Globalization;
using System.Text;
using Monitor.Vendors.Nvidia.Native;

namespace Monitor.Vendors.Nvidia;

/// <summary>One physical NVIDIA GPU's sensor reading. Every field that NVAPI can plausibly fail to
/// provide (wrong driver, non-NVIDIA-owned metric, undocumented/blocked API) is nullable; a null
/// here always means "could not be read", never "read as zero/absent".</summary>
public sealed record NvidiaGpuReading
{
    public int Index { get; init; }
    public string Name { get; init; } = "";

    /// <summary>NVAPI has no public call that returns a Win32 LUID compatible with DXGI's. The
    /// closest thing, NvAPI_GetGPUIDfromPhysicalGPU, returns an NVAPI-internal 32-bit tag in a
    /// different ID space - matching it against a DXGI LUID would silently produce a wrong pairing,
    /// which is worse than not pairing at all, so this is always null. Callers with exactly one
    /// NVIDIA GPU (the overwhelmingly common case) can safely match NvidiaGpuReading.Index against
    /// DXGI adapter enumeration order instead.</summary>
    public long? Luid { get; init; }

    public double? TemperatureC { get; init; }
    public double? HotspotTemperatureC { get; init; }
    public double? MemoryTemperatureC { get; init; }
    public double? FanPercent { get; init; }
    public int? FanRpm { get; init; }
    public double? PowerWatts { get; init; }
    public double? PowerLimitWatts { get; init; }
    public double? CoreClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
    public ulong? DedicatedTotalBytes { get; init; }
}

/// <summary>
/// Reads NVIDIA GPU sensors (temperature, fan, power, clocks, VRAM, utilization) via NVAPI.
/// Requires no elevation and works whenever an NVIDIA GPU + driver + nvapi64.dll are present.
///
/// Every public member is exception-safe: construction failure returns null from
/// <see cref="TryCreate"/> instead of throwing, and <see cref="Read"/> never throws - a failure on
/// one GPU or one metric degrades that value to null rather than losing the whole reading.
///
/// Hotspot/memory-junction temperature (<see cref="NvidiaGpuReading.HotspotTemperatureC"/> /
/// <see cref="NvidiaGpuReading.MemoryTemperatureC"/>) come from an undocumented NVAPI call
/// (NvAPI_GPU_GetThermalSensors) with no official struct documentation; the layout used here is
/// reverse-engineered by the community (see remarks on <see cref="NvApi.NvThermalSensorsEx"/>) and
/// NVIDIA has been observed locking this sensor out entirely on newer (Blackwell) GPUs. Values are
/// range-checked (0-150C) before being surfaced; anything implausible or any call failure yields
/// null rather than a fabricated number.
/// </summary>
public sealed class NvidiaGpuSensors : IDisposable
{
    private readonly NvApi.NvPhysicalGpuHandle[] _handles;
    private readonly string?[] _names;
    private readonly ulong?[] _dedicatedTotalBytes;

    /// <summary>Clock-frequency struct version (1-3) that this driver accepted, discovered once on
    /// first successful read and reused afterwards. -1 until discovered.</summary>
    private int _clockVersion = -1;

    /// <summary>NVML device handles, indexed to line up 1:1 with <see cref="_handles"/> by enumeration
    /// order. NVML and NVAPI are separate driver components with independently-ordered device lists;
    /// this project only trusts the pairing when both APIs report the same device count (the common
    /// case), and otherwise treats NVML as unavailable rather than risk pairing the wrong GPU's power
    /// numbers with another GPU's temperature/clock readings. Null if NVML failed to initialize.</summary>
    private readonly nint[]? _nvmlHandles;

    private bool _nvmlInitialized;
    private bool _disposed;

    private NvidiaGpuSensors(NvApi.NvPhysicalGpuHandle[] handles, string?[] names, ulong?[] dedicatedTotalBytes, string? driverVersion, nint[]? nvmlHandles, bool nvmlInitialized)
    {
        _handles = handles;
        _names = names;
        _dedicatedTotalBytes = dedicatedTotalBytes;
        DriverVersion = driverVersion;
        _nvmlHandles = nvmlHandles;
        _nvmlInitialized = nvmlInitialized;
    }

    /// <summary>e.g. "581.29". Null if the driver-version query is unsupported or failed.</summary>
    public string? DriverVersion { get; }

    /// <summary>Initializes NVAPI and enumerates physical GPUs. Returns null - never throws - if
    /// nvapi64.dll is missing, there is no NVIDIA GPU, or NvAPI_Initialize/EnumPhysicalGPUs fails.</summary>
    public static NvidiaGpuSensors? TryCreate()
    {
        try
        {
            if (!NvApi.TryInitialize() || NvApi.EnumPhysicalGPUs is null)
            {
                return null;
            }

            var handles = new NvApi.NvPhysicalGpuHandle[NvApi.MaxPhysicalGpus];
            NvApi.NvStatus status = NvApi.EnumPhysicalGPUs(handles, out int count);
            if (status != NvApi.NvStatus.Ok || count <= 0)
            {
                return null;
            }

            Array.Resize(ref handles, count);

            var names = new string?[count];
            var dedicatedTotalBytes = new ulong?[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = TryGetName(handles[i]);
                dedicatedTotalBytes[i] = TryGetDedicatedTotalBytes(handles[i]);
            }

            string? driverVersion = TryGetDriverVersion();

            (nint[]? nvmlHandles, bool nvmlInitialized) = TryCreateNvml(count);

            return new NvidiaGpuSensors(handles, names, dedicatedTotalBytes, driverVersion, nvmlHandles, nvmlInitialized);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort NVML init. Never throws and never fails GPU sensor creation as a whole -
    /// NVML only feeds absolute power watts, everything else keeps working via NVAPI regardless.</summary>
    private static (nint[]? Handles, bool Initialized) TryCreateNvml(int nvApiGpuCount)
    {
        try
        {
            if (Native.Nvml.NvmlInit() != Native.Nvml.NvmlReturn.Success)
            {
                return (null, false);
            }

            if (Native.Nvml.NvmlDeviceGetCount(out uint nvmlCount) != Native.Nvml.NvmlReturn.Success
                || nvmlCount != (uint)nvApiGpuCount)
            {
                // Device count mismatch: enumeration order between the two APIs cannot be trusted
                // to line up. Leave NVML shut down rather than risk pairing the wrong GPU.
                Native.Nvml.NvmlShutdown();
                return (null, false);
            }

            var handles = new nint[nvmlCount];
            for (uint i = 0; i < nvmlCount; i++)
            {
                if (Native.Nvml.NvmlDeviceGetHandleByIndex(i, out nint handle) != Native.Nvml.NvmlReturn.Success)
                {
                    Native.Nvml.NvmlShutdown();
                    return (null, false);
                }

                handles[i] = handle;
            }

            return (handles, true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>Reads every enumerated GPU. Never throws: a failure reading one GPU yields a
    /// near-empty reading for that index (name/index still populated) rather than aborting the
    /// whole call, so one bad sensor never hides the rest of the machine's readings.</summary>
    public IReadOnlyList<NvidiaGpuReading> Read()
    {
        if (_disposed)
        {
            return Array.Empty<NvidiaGpuReading>();
        }

        var results = new List<NvidiaGpuReading>(_handles.Length);
        for (int i = 0; i < _handles.Length; i++)
        {
            try
            {
                results.Add(ReadOne(i, _handles[i], _names[i] ?? ""));
            }
            catch
            {
                results.Add(new NvidiaGpuReading { Index = i, Name = _names[i] ?? "" });
            }
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            NvApi.Unload?.Invoke();
        }
        catch
        {
            // Best-effort cleanup only.
        }

        if (_nvmlInitialized)
        {
            try
            {
                Native.Nvml.NvmlShutdown();
            }
            catch
            {
                // Best-effort cleanup only.
            }
            finally
            {
                _nvmlInitialized = false;
            }
        }
    }

    private NvidiaGpuReading ReadOne(int index, NvApi.NvPhysicalGpuHandle handle, string name)
    {
        double? coreTemp = TryGetCoreTemperature(handle);
        (double? hotspot, double? memTemp) = TryGetExtraTemperatures(handle, name);
        (double? fanPercent, int? fanRpm) = TryGetFan(handle);
        (double? power, double? powerLimit) = TryGetPowerWatts(index);
        (double? coreClock, double? memClock) = TryGetClocksMhz(handle);
        ulong? dedicatedTotal = (uint)index < (uint)_dedicatedTotalBytes.Length ? _dedicatedTotalBytes[index] : null;

        return new NvidiaGpuReading
        {
            Index = index,
            Name = name,
            Luid = null,
            TemperatureC = coreTemp,
            HotspotTemperatureC = hotspot,
            MemoryTemperatureC = memTemp,
            FanPercent = fanPercent,
            FanRpm = fanRpm,
            PowerWatts = power,
            PowerLimitWatts = powerLimit,
            CoreClockMhz = coreClock,
            MemoryClockMhz = memClock,
            DedicatedTotalBytes = dedicatedTotal,
        };
    }

    private static string? TryGetName(NvApi.NvPhysicalGpuHandle handle)
    {
        if (NvApi.GpuGetFullName is null)
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder(NvApi.ShortStringMax);
            return NvApi.GpuGetFullName(handle, builder) == NvApi.NvStatus.Ok ? builder.ToString().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetDriverVersion()
    {
        if (NvApi.SysGetDriverAndBranchVersion is null)
        {
            return null;
        }

        try
        {
            var branch = new StringBuilder(NvApi.ShortStringMax);
            if (NvApi.SysGetDriverAndBranchVersion(out uint rawVersion, branch) != NvApi.NvStatus.Ok)
            {
                return null;
            }

            // NVAPI encodes e.g. "581.29" as 58129.
            uint major = rawVersion / 100;
            uint minor = rawVersion % 100;
            return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor:00}");
        }
        catch
        {
            return null;
        }
    }

    private static double? TryGetCoreTemperature(NvApi.NvPhysicalGpuHandle handle)
    {
        if (NvApi.GpuGetThermalSettings is null)
        {
            return null;
        }

        try
        {
            var settings = new NvApi.NvThermalSettings
            {
                Version = NvApi.MakeVersion<NvApi.NvThermalSettings>(2),
                Count = NvApi.MaxThermalSensorsPerGpu,
            };

            if (NvApi.GpuGetThermalSettings(handle, (int)NvApi.NvThermalTarget.All, ref settings) != NvApi.NvStatus.Ok
                || settings.Sensor is null || settings.Count == 0)
            {
                return null;
            }

            int count = Math.Min((int)settings.Count, settings.Sensor.Length);

            // Prefer the sensor explicitly targeting the GPU core; fall back to the first sensor.
            for (int i = 0; i < count; i++)
            {
                if (settings.Sensor[i].Target == (int)NvApi.NvThermalTarget.Gpu)
                {
                    return SanitizeTemperature(settings.Sensor[i].CurrentTemp);
                }
            }

            return count > 0 ? SanitizeTemperature(settings.Sensor[0].CurrentTemp) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Hotspot / memory-junction temperature via the undocumented GetThermalSensors call.
    /// See the class remarks: best-effort, range-checked, null on anything implausible.</summary>
    private static (double? Hotspot, double? Memory) TryGetExtraTemperatures(NvApi.NvPhysicalGpuHandle handle, string name)
    {
        if (NvApi.GpuGetThermalSensors is null)
        {
            return (null, null);
        }

        try
        {
            // Index-to-sensor mapping differs by GPU generation (reverse-engineered; see remarks).
            int hotspotIndex;
            int memoryIndex;
            if (name.StartsWith("NVIDIA GeForce RTX 50", StringComparison.OrdinalIgnoreCase))
            {
                hotspotIndex = 1;
                memoryIndex = 2;
            }
            else if (name.StartsWith("NVIDIA GeForce RTX 40", StringComparison.OrdinalIgnoreCase))
            {
                hotspotIndex = 1;
                memoryIndex = 7;
            }
            else
            {
                hotspotIndex = 1;
                memoryIndex = 9;
            }

            // Empirically, Mask must be exactly the bits for the slots being requested - 0xFFFFFFFF
            // (or any bit beyond the sensor's populated range) makes the whole call fail with
            // NVAPI_DATA_NOT_FOUND, while requesting only valid individual bits (verified against
            // this project's reference RTX 3070 Ti for indices 0-9) succeeds and returns exactly
            // those slots.
            uint mask = (1u << hotspotIndex) | (1u << memoryIndex);

            var sensors = new NvApi.NvThermalSensorsEx
            {
                Version = NvApi.MakeVersion<NvApi.NvThermalSensorsEx>(2),
                Mask = mask,
            };

            if (NvApi.GpuGetThermalSensors(handle, ref sensors) != NvApi.NvStatus.Ok || sensors.Temperatures is null)
            {
                return (null, null);
            }

            double? hotspot = ReadFixedPointTemperature(sensors.Temperatures, hotspotIndex);
            double? memory = ReadFixedPointTemperature(sensors.Temperatures, memoryIndex);
            return (hotspot, memory);
        }
        catch
        {
            return (null, null);
        }
    }

    private static double? ReadFixedPointTemperature(int[] temperatures, int index)
    {
        if (index < 0 || index >= temperatures.Length)
        {
            return null;
        }

        // Values are 8.8 fixed-point (raw / 256 = degrees C).
        return SanitizeTemperature(temperatures[index] / 256.0);
    }

    private static double? SanitizeTemperature(double celsius)
        => celsius is > 0.0 and < 150.0 ? celsius : null;

    private static (double? Percent, int? Rpm) TryGetFan(NvApi.NvPhysicalGpuHandle handle)
    {
        double? percent = null;
        int? rpm = null;

        if (NvApi.GpuClientFanCoolersGetStatus is not null)
        {
            try
            {
                var status = new NvApi.NvFanCoolersStatus
                {
                    Version = NvApi.MakeVersion<NvApi.NvFanCoolersStatus>(1),
                    Items = new NvApi.NvFanCoolersStatusItem[NvApi.MaxFanCoolersStatusItems],
                };

                if (NvApi.GpuClientFanCoolersGetStatus(handle, ref status) == NvApi.NvStatus.Ok
                    && status.Count > 0 && status.Items is { Length: > 0 })
                {
                    NvApi.NvFanCoolersStatusItem first = status.Items[0];
                    rpm = first.CurrentRpm <= 20000 ? (int)first.CurrentRpm : null;
                    percent = first.CurrentLevel <= 100 ? first.CurrentLevel : null;
                }
            }
            catch
            {
                // Fall through to the legacy calls below.
            }
        }

        if (rpm is null && NvApi.GpuGetTachReading is not null)
        {
            try
            {
                if (NvApi.GpuGetTachReading(handle, out int valueRpm) == NvApi.NvStatus.Ok && valueRpm is >= 0 and <= 20000)
                {
                    rpm = valueRpm;
                }
            }
            catch
            {
                // Leave rpm null.
            }
        }

        if (percent is null && NvApi.GpuGetCoolerSettings is not null)
        {
            try
            {
                var settings = new NvApi.NvCoolerSettings
                {
                    Version = NvApi.MakeVersion<NvApi.NvCoolerSettings>(2),
                    Cooler = new NvApi.NvCooler[NvApi.MaxCoolersPerGpu],
                };

                if (NvApi.GpuGetCoolerSettings(handle, NvApi.NvCoolerTarget.All, ref settings) == NvApi.NvStatus.Ok
                    && settings.Count > 0 && settings.Cooler is { Length: > 0 })
                {
                    int level = settings.Cooler[0].CurrentLevel;
                    percent = level is >= 0 and <= 100 ? level : null;
                }
            }
            catch
            {
                // Leave percent null.
            }
        }

        return (percent, rpm);
    }

    /// <summary>
    /// Absolute GPU power draw / power limit in watts, sourced from NVML (nvml.dll) rather than
    /// NVAPI - see <see cref="Native.Nvml"/>'s remarks for why. NVAPI_GPU_ClientPowerTopologyGetStatus
    /// was implemented and cross-checked against nvidia-smi (see the detailed derivation on
    /// <see cref="NvApi.NvPowerTopologyEntry"/>): its result is per-cent-mille of the GPU's
    /// *currently configured* power limit, not absolute milliwatts, and NVAPI exposes no verified
    /// call for the absolute-watt baseline that percentage is relative to. NVML's
    /// nvmlDeviceGetPowerUsage / nvmlDeviceGetPowerManagementLimit are documented, stable calls that
    /// return milliwatts directly - the same quantities nvidia-smi's power.draw/power.limit report -
    /// so this project uses NVML for this one metric instead of guessing at NVAPI's baseline. Null
    /// (both values) if NVML failed to initialize, the device-count pairing with NVAPI didn't line
    /// up (see <see cref="TryCreateNvml"/>), or the call itself fails.
    /// </summary>
    private (double? Watts, double? LimitWatts) TryGetPowerWatts(int index)
    {
        if (_nvmlHandles is null || !_nvmlInitialized || index < 0 || index >= _nvmlHandles.Length)
        {
            return (null, null);
        }

        try
        {
            nint device = _nvmlHandles[index];

            double? watts = null;
            if (Native.Nvml.NvmlDeviceGetPowerUsage(device, out uint usageMw) == Native.Nvml.NvmlReturn.Success)
            {
                watts = usageMw / 1000.0;
            }

            double? limitWatts = null;
            if (Native.Nvml.NvmlDeviceGetPowerManagementLimit(device, out uint limitMw) == Native.Nvml.NvmlReturn.Success)
            {
                limitWatts = limitMw / 1000.0;
            }

            return (watts, limitWatts);
        }
        catch
        {
            return (null, null);
        }
    }

    private (double? CoreMhz, double? MemoryMhz) TryGetClocksMhz(NvApi.NvPhysicalGpuHandle handle)
    {
        if (NvApi.GpuGetAllClockFrequencies is null)
        {
            return (null, null);
        }

        // Try the version this driver already told us it accepts; otherwise probe 3 -> 1 once and
        // remember whichever succeeds first (mirrors LibreHardwareMonitor's approach).
        int[] versionsToTry = _clockVersion > 0 ? [_clockVersion] : [3, 2, 1];

        foreach (int version in versionsToTry)
        {
            try
            {
                var clocks = new NvApi.NvGpuClockFrequencies
                {
                    Version = NvApi.MakeVersion<NvApi.NvGpuClockFrequencies>(version),
                };

                NvApi.NvStatus status = NvApi.GpuGetAllClockFrequencies(handle, ref clocks);
                if (status != NvApi.NvStatus.Ok || clocks.Clocks is null)
                {
                    continue;
                }

                _clockVersion = version;

                double? core = ReadClockMhz(clocks.Clocks, (int)NvApi.NvGpuPublicClockId.Graphics);
                double? memory = ReadClockMhz(clocks.Clocks, (int)NvApi.NvGpuPublicClockId.Memory);
                return (core, memory);
            }
            catch
            {
                // Try the next version.
            }
        }

        return (null, null);
    }

    private static double? ReadClockMhz(NvApi.NvClockDomainInfo[] clocks, int index)
    {
        if (index < 0 || index >= clocks.Length || !clocks[index].IsPresent)
        {
            return null;
        }

        double mhz = clocks[index].Frequency / 1000.0;
        return mhz > 0.0 ? mhz : null;
    }

    private static ulong? TryGetDedicatedTotalBytes(NvApi.NvPhysicalGpuHandle handle)
    {
        if (NvApi.GpuGetMemoryInfoEx is null)
        {
            return null;
        }

        try
        {
            var info = new NvApi.NvMemoryInfoEx
            {
                Version = NvApi.MakeVersion<NvApi.NvMemoryInfoEx>(1),
            };

            if (NvApi.GpuGetMemoryInfoEx(handle, ref info) != NvApi.NvStatus.Ok || info.DedicatedVideoMemoryBytes == 0)
            {
                return null;
            }

            return info.DedicatedVideoMemoryBytes;
        }
        catch
        {
            return null;
        }
    }
}
