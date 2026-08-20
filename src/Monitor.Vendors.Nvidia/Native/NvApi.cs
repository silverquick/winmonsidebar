using System.Runtime.InteropServices;
using System.Text;

namespace Monitor.Vendors.Nvidia.Native;

/// <summary>
/// P/Invoke layer over nvapi64.dll.
///
/// nvapi64.dll exports essentially one function, <c>nvapi_QueryInterface</c>, which resolves an
/// opaque numeric "function ID" to a function pointer. All real entry points (Initialize, GetGPU*,
/// ...) are obtained through it and invoked via <see cref="Marshal.GetDelegateForFunctionPointer{T}"/>
/// using Cdecl delegates. The IDs below are NOT invented from memory: they were cross-checked against
/// three independent public sources that all agree exactly -
///   1. NVIDIA's own public header dump: https://github.com/NVIDIA/nvapi/blob/main/nvapi_interface.h
///      (documented/public function IDs only)
///   2. LibreHardwareMonitor's current NvApi.cs interop layer (actively maintained, MPL-2.0)
///   3. openhardwaremonitor's NVAPI.cs and falahati/NvAPIWrapper's FunctionId.cs (independent projects)
/// Every ID that appears in the official NVIDIA header matches the community sources bit-for-bit.
/// The only IDs NOT in NVIDIA's public header are the private/undocumented ones
/// (GetCoolerSettings, ClientFanCoolersGetStatus, ClientPowerTopologyGetStatus, GetThermalSensors) -
/// those are cross-checked between LibreHardwareMonitor and NvAPIWrapper only, and are treated as
/// best-effort: <see cref="TryInitialize"/> resolves each one independently via QueryInterface, and a
/// NULL function pointer (unsupported on this driver/GPU) simply leaves the corresponding delegate
/// property null, which callers must treat as "not available" rather than call into.
///
/// Struct "version" fields follow NVAPI's convention: version = sizeof(struct) | (versionNumber &lt;&lt; 16).
/// Getting this wrong is the most common NVAPI bug (-5 NVAPI_INCOMPATIBLE_STRUCT_VERSION), so it is
/// always computed from <c>Marshal.SizeOf</c> at runtime (<see cref="MakeVersion{T}"/>) rather than
/// hand-computed, mirroring how every reference implementation above does it.
/// </summary>
internal static partial class NvApi
{
    public const int MaxPhysicalGpus = 64;
    public const int ShortStringMax = 64;
    public const int MaxThermalSensorsPerGpu = 3;
    public const int MaxGpuPublicClocks = 32;
    public const int MaxGpuUtilizations = 8;
    public const int MaxCoolersPerGpu = 20;
    public const int MaxFanCoolersStatusItems = 32;
    public const int MaxPowerTopologyEntries = 4;
    public const int ThermalSensorReservedCount = 8;
    public const int ThermalSensorTemperatureCount = 32;

    private const string Dll64 = "nvapi64.dll";

    // ---- Function IDs (see class remarks for provenance) --------------------------------------

    private static class FunctionId
    {
        public const uint Initialize = 0x0150E828;
        public const uint Unload = 0xD22BDD7E;
        public const uint EnumPhysicalGPUs = 0xE5AC921F;
        public const uint GPU_GetFullName = 0xCEEE8E9F;
        public const uint SYS_GetDriverAndBranchVersion = 0x2926AAAD;
        public const uint GPU_GetThermalSettings = 0xE3640A56;
        public const uint GPU_GetTachReading = 0x5F608315;
        public const uint GPU_GetAllClockFrequencies = 0xDCB616C3;
        public const uint GPU_GetDynamicPstatesInfoEx = 0x60DED2ED;
        public const uint GPU_GetMemoryInfoEx = 0xC0599498;

        // Private/undocumented - not in NVIDIA's public nvapi_interface.h. Cross-checked between
        // LibreHardwareMonitor and falahati/NvAPIWrapper only. Resolved defensively at runtime.
        public const uint GPU_GetCoolerSettings = 0xDA141340;
        public const uint GPU_ClientFanCoolersGetStatus = 0x35AED5E8;
        public const uint GPU_ClientPowerTopologyGetStatus = 0xEDCF624E;
        public const uint GPU_GetThermalSensors = 0x65FE3AAD;
    }

    // ---- Handles / enums ------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct NvPhysicalGpuHandle
    {
        private readonly nint _handle;
    }

    /// <summary>Subset of NvAPI_Status actually inspected by this project; every other nonzero value
    /// is still a failure, it just isn't named here.</summary>
    public enum NvStatus
    {
        Ok = 0,
        Error = -1,
        InvalidArgument = -5,
        IncompatibleStructVersion = -9,
        NotSupported = -104,
        FunctionNotFound = -136,
    }

    public enum NvThermalTarget
    {
        None = 0,
        Gpu = 1,
        Memory = 2,
        PowerSupply = 4,
        Board = 8,
        All = 15,
    }

    public enum NvCoolerTarget
    {
        None = 0,
        Gpu = 1,
        Memory = 2,
        PowerSupply = 4,
        All = 7,
    }

    public enum NvPowerTopologyDomain : uint
    {
        Gpu = 0,
        Board = 1,
    }

    /// <summary>Indices into <see cref="NvGpuClockFrequencies.Clocks"/> for the clocks this project
    /// cares about (there are up to <see cref="MaxGpuPublicClocks"/> slots; most are unused/absent).</summary>
    public enum NvGpuPublicClockId
    {
        Graphics = 0,
        Memory = 4,
        Processor = 7,
        Video = 8,
    }

    // ---- Structs (all Pack = 8, matching every reference implementation on x64) ------------------

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvThermalSensor
    {
        public int Controller;
        public int DefaultMinTemp;
        public int DefaultMaxTemp;
        public int CurrentTemp;
        public int Target; // NvThermalTarget
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvThermalSettings
    {
        public uint Version;
        public uint Count;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxThermalSensorsPerGpu)]
        public NvThermalSensor[] Sensor;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvClockDomainInfo
    {
        public uint IsPresentRaw;
        public uint Frequency; // kHz

        public readonly bool IsPresent => (IsPresentRaw & 1) != 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvGpuClockFrequencies
    {
        public uint Version;
        public uint Reserved;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxGpuPublicClocks)]
        public NvClockDomainInfo[] Clocks;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvDynamicPState
    {
        public bool IsPresent;
        public int Percentage;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvDynamicPStatesInfo
    {
        public uint Version;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxGpuUtilizations)]
        public NvDynamicPState[] Utilizations;
    }

    /// <summary>
    /// All memory fields are raw bytes, NOT kilobytes despite some community sources naming the
    /// corresponding fields "...InkB" - confirmed empirically on this project's reference machine
    /// (RTX 3070 Ti): DedicatedVideoMemoryBytes read back as exactly 8589934592 = 8192 MiB * 1024 *
    /// 1024, matching nvidia-smi's fixed 8192 MiB total to the byte. Using KB here (as the older,
    /// 32-bit-field NvMemoryInfo struct does) would have overshot by exactly 1024x, which is what a
    /// first pass at this struct did before being caught by that cross-check.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NvMemoryInfoEx
    {
        public uint Version;
        public ulong DedicatedVideoMemoryBytes;
        public ulong AvailableDedicatedVideoMemoryBytes;
        public ulong SystemVideoMemoryBytes;
        public ulong SharedSystemMemoryBytes;
        public ulong CurrentAvailableDedicatedVideoMemoryBytes;
        public ulong DedicatedVideoMemoryEvictionsSizeBytes;
        public ulong DedicatedVideoMemoryEvictionCount;
        public ulong DedicatedVideoMemoryPromotionsSizeBytes;
        public ulong DedicatedVideoMemoryPromotionCount;
    }

    /// <summary>
    /// The power field here is NOT absolute milliwatts despite that being how some community
    /// sources (LibreHardwareMonitor's interop layer) name/consume it. It is empirically confirmed
    /// on this project's reference machine (RTX 3070 Ti) to be per-cent-mille (raw/100000 = fraction)
    /// of the GPU's *currently configured* power limit (nvidia-smi's power.limit, itself a runtime
    /// value, not a fixed constant): across independent samples, (raw / 100000.0) * power.limit
    /// landed within 0.2% of nvidia-smi's simultaneous power.draw every time
    /// (e.g. raw=26940, power.limit=290.00W -> 78.13W computed vs. 78.30W measured). This matches
    /// falahati/NvAPIWrapper's independent documentation of the sibling PowerPolicies calls, which
    /// names the equivalent fields "...InPCM" (per cent mille) rather than watts.
    /// Since NVAPI itself exposes no verified call for the absolute-watt power-limit baseline this
    /// percentage is relative to, this project does not attempt that conversion from this struct and
    /// this field/struct is unused - <see cref="Monitor.Vendors.Nvidia.NvidiaGpuSensors"/>'s
    /// PowerWatts/PowerLimitWatts instead come from NVML (<see cref="Monitor.Vendors.Nvidia.Native.Nvml"/>),
    /// a separate driver component that exposes the absolute watt figures directly.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvPowerTopologyEntry
    {
        public NvPowerTopologyDomain Domain;
        public uint Reserved;
        public uint PowerUsagePerCentMille;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvPowerTopology
    {
        public int Version;
        public uint Count;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxPowerTopologyEntries, ArraySubType = UnmanagedType.Struct)]
        public NvPowerTopologyEntry[] Entries;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvCooler
    {
        public int Type;
        public int Controller;
        public int DefaultMin;
        public int DefaultMax;
        public int CurrentMin;
        public int CurrentMax;
        public int CurrentLevel;
        public int DefaultPolicy;
        public int CurrentPolicy;
        public int Target;
        public int ControlType;
        public int Active;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvCoolerSettings
    {
        public uint Version;
        public uint Count;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxCoolersPerGpu)]
        public NvCooler[] Cooler;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvFanCoolersStatusItem
    {
        public uint CoolerId;
        public uint CurrentRpm;
        public uint CurrentMinLevel;
        public uint CurrentMaxLevel;
        public uint CurrentLevel;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8, ArraySubType = UnmanagedType.U4)]
        public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvFanCoolersStatus
    {
        public uint Version;
        public uint Count;
        public ulong Reserved1;
        public ulong Reserved2;
        public ulong Reserved3;
        public ulong Reserved4;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxFanCoolersStatusItems)]
        public NvFanCoolersStatusItem[] Items;
    }

    /// <summary>Undocumented struct backing the private GetThermalSensors call (hotspot / memory
    /// junction temperature). Field layout and the per-GPU-generation Temperatures[] index meaning
    /// come from LibreHardwareMonitor's current (actively maintained) interop layer - there is no
    /// official NVIDIA documentation for this call. Best-effort only: see
    /// <see cref="NvidiaGpuSensors"/> remarks for how failures/implausible values are handled.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NvThermalSensorsEx
    {
        public uint Version;
        public uint Mask;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ThermalSensorReservedCount)]
        public int[] Reserved;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ThermalSensorTemperatureCount)]
        public int[] Temperatures;
    }

    // ---- Delegates (all Cdecl - this is the calling convention nvapi_QueryInterface's targets use) -

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_UnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_EnumPhysicalGPUsDelegate([Out] NvPhysicalGpuHandle[] gpuHandles, out int gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetFullNameDelegate(NvPhysicalGpuHandle gpuHandle, StringBuilder name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_SYS_GetDriverAndBranchVersionDelegate(out uint driverVersion, StringBuilder branchString);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetThermalSettingsDelegate(NvPhysicalGpuHandle gpuHandle, int sensorIndex, ref NvThermalSettings settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetTachReadingDelegate(NvPhysicalGpuHandle gpuHandle, out int valueRpm);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetAllClockFrequenciesDelegate(NvPhysicalGpuHandle gpuHandle, ref NvGpuClockFrequencies frequencies);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetDynamicPstatesInfoExDelegate(NvPhysicalGpuHandle gpuHandle, ref NvDynamicPStatesInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetMemoryInfoExDelegate(NvPhysicalGpuHandle gpuHandle, ref NvMemoryInfoEx info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetCoolerSettingsDelegate(NvPhysicalGpuHandle gpuHandle, NvCoolerTarget target, ref NvCoolerSettings settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_ClientFanCoolersGetStatusDelegate(NvPhysicalGpuHandle gpuHandle, ref NvFanCoolersStatus status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_ClientPowerTopologyGetStatusDelegate(NvPhysicalGpuHandle gpuHandle, ref NvPowerTopology topology);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate NvStatus NvAPI_GPU_GetThermalSensorsDelegate(NvPhysicalGpuHandle gpuHandle, ref NvThermalSensorsEx sensors);

    // ---- Resolved entry points (null = unsupported on this driver/GPU; callers must check) --------

    public static bool IsAvailable { get; private set; }

    public static NvAPI_UnloadDelegate? Unload { get; private set; }
    public static NvAPI_EnumPhysicalGPUsDelegate? EnumPhysicalGPUs { get; private set; }
    public static NvAPI_GPU_GetFullNameDelegate? GpuGetFullName { get; private set; }
    public static NvAPI_SYS_GetDriverAndBranchVersionDelegate? SysGetDriverAndBranchVersion { get; private set; }
    public static NvAPI_GPU_GetThermalSettingsDelegate? GpuGetThermalSettings { get; private set; }
    public static NvAPI_GPU_GetTachReadingDelegate? GpuGetTachReading { get; private set; }
    public static NvAPI_GPU_GetAllClockFrequenciesDelegate? GpuGetAllClockFrequencies { get; private set; }
    public static NvAPI_GPU_GetDynamicPstatesInfoExDelegate? GpuGetDynamicPstatesInfoEx { get; private set; }
    public static NvAPI_GPU_GetMemoryInfoExDelegate? GpuGetMemoryInfoEx { get; private set; }
    public static NvAPI_GPU_GetCoolerSettingsDelegate? GpuGetCoolerSettings { get; private set; }
    public static NvAPI_GPU_ClientFanCoolersGetStatusDelegate? GpuClientFanCoolersGetStatus { get; private set; }
    public static NvAPI_GPU_ClientPowerTopologyGetStatusDelegate? GpuClientPowerTopologyGetStatus { get; private set; }
    public static NvAPI_GPU_GetThermalSensorsDelegate? GpuGetThermalSensors { get; private set; }

    [LibraryImport(Dll64, EntryPoint = "nvapi_QueryInterface")]
    private static partial nint QueryInterfaceRaw(uint id);

    /// <summary>Resolves a function ID to a typed delegate. Returns null (never throws) if the DLL
    /// is missing, the entry point is absent, or this driver/GPU does not implement the ID - the
    /// per-ID runtime check the task calls for instead of trusting IDs blindly.</summary>
    private static T? GetDelegate<T>(uint id) where T : Delegate
    {
        nint ptr;
        try
        {
            ptr = QueryInterfaceRaw(id);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        return ptr == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>Encodes an NVAPI struct version: size (from the actual marshaled struct, not a
    /// hand-counted guess) packed with the version number NVIDIA assigns to that struct shape.</summary>
    public static uint MakeVersion<T>(int versionNumber) where T : struct
        => (uint)Marshal.SizeOf<T>() | ((uint)versionNumber << 16);

    /// <summary>Idempotent. Returns true only if nvapi64.dll is present, NvAPI_Initialize succeeded,
    /// and EnumPhysicalGPUs (the one entry point everything else is useless without) resolved. Never
    /// throws.</summary>
    public static bool TryInitialize()
    {
        if (IsAvailable)
        {
            return true;
        }

        try
        {
            var initialize = GetDelegate<NvAPI_InitializeDelegate>(FunctionId.Initialize);
            if (initialize is null || initialize() != NvStatus.Ok)
            {
                return false;
            }

            Unload = GetDelegate<NvAPI_UnloadDelegate>(FunctionId.Unload);
            EnumPhysicalGPUs = GetDelegate<NvAPI_EnumPhysicalGPUsDelegate>(FunctionId.EnumPhysicalGPUs);
            GpuGetFullName = GetDelegate<NvAPI_GPU_GetFullNameDelegate>(FunctionId.GPU_GetFullName);
            SysGetDriverAndBranchVersion = GetDelegate<NvAPI_SYS_GetDriverAndBranchVersionDelegate>(FunctionId.SYS_GetDriverAndBranchVersion);
            GpuGetThermalSettings = GetDelegate<NvAPI_GPU_GetThermalSettingsDelegate>(FunctionId.GPU_GetThermalSettings);
            GpuGetTachReading = GetDelegate<NvAPI_GPU_GetTachReadingDelegate>(FunctionId.GPU_GetTachReading);
            GpuGetAllClockFrequencies = GetDelegate<NvAPI_GPU_GetAllClockFrequenciesDelegate>(FunctionId.GPU_GetAllClockFrequencies);
            GpuGetDynamicPstatesInfoEx = GetDelegate<NvAPI_GPU_GetDynamicPstatesInfoExDelegate>(FunctionId.GPU_GetDynamicPstatesInfoEx);
            GpuGetMemoryInfoEx = GetDelegate<NvAPI_GPU_GetMemoryInfoExDelegate>(FunctionId.GPU_GetMemoryInfoEx);
            GpuGetCoolerSettings = GetDelegate<NvAPI_GPU_GetCoolerSettingsDelegate>(FunctionId.GPU_GetCoolerSettings);
            GpuClientFanCoolersGetStatus = GetDelegate<NvAPI_GPU_ClientFanCoolersGetStatusDelegate>(FunctionId.GPU_ClientFanCoolersGetStatus);
            GpuClientPowerTopologyGetStatus = GetDelegate<NvAPI_GPU_ClientPowerTopologyGetStatusDelegate>(FunctionId.GPU_ClientPowerTopologyGetStatus);
            GpuGetThermalSensors = GetDelegate<NvAPI_GPU_GetThermalSensorsDelegate>(FunctionId.GPU_GetThermalSensors);

            IsAvailable = EnumPhysicalGPUs is not null;
            return IsAvailable;
        }
        catch
        {
            // Never let a driver/version quirk escape into caller code - "no sensors" beats a crash.
            IsAvailable = false;
            return false;
        }
    }
}
