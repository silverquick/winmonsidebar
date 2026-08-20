using System.Runtime.InteropServices;

namespace Monitor.Vendors.Nvidia.Native;

/// <summary>
/// P/Invoke layer over nvml.dll (NVIDIA Management Library).
///
/// Unlike nvapi64.dll (<see cref="NvApi"/>), NVML exports its entry points directly by name -
/// this is the same public, documented, stable ABI that nvidia-smi.exe itself is built on, so no
/// QueryInterface-style resolution is needed. The NVIDIA driver installs nvml.dll into
/// %SystemRoot%\System32 (confirmed present there on this project's reference machine alongside
/// nvapi64.dll), so a plain DllImport-style name lookup resolves it via the normal Windows DLL
/// search order without any explicit path.
///
/// This project uses NVML for exactly one purpose NVAPI cannot honestly provide: absolute GPU
/// power draw / power limit in watts (see remarks on <see cref="NvApi.NvPowerTopologyEntry"/> for
/// why NVAPI's power topology call is unusable for this). <see cref="NvmlDeviceGetPowerUsage"/> and
/// <see cref="NvmlDeviceGetPowerManagementLimit"/> are documented NVML calls returning milliwatts
/// directly - the exact quantities nvidia-smi's <c>power.draw</c> / <c>power.limit</c> report.
/// </summary>
internal static partial class Nvml
{
    private const string Dll = "nvml.dll";

    public enum NvmlReturn : int
    {
        Success = 0,
    }

    [LibraryImport(Dll, EntryPoint = "nvmlInit_v2")]
    public static partial NvmlReturn NvmlInit();

    [LibraryImport(Dll, EntryPoint = "nvmlShutdown")]
    public static partial NvmlReturn NvmlShutdown();

    [LibraryImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static partial NvmlReturn NvmlDeviceGetCount(out uint deviceCount);

    [LibraryImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static partial NvmlReturn NvmlDeviceGetHandleByIndex(uint index, out nint device);

    [LibraryImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static partial NvmlReturn NvmlDeviceGetPowerUsage(nint device, out uint milliwatts);

    [LibraryImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
    public static partial NvmlReturn NvmlDeviceGetPowerManagementLimit(nint device, out uint milliwatts);
}
