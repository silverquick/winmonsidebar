using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monitor.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_MEMORY_COUNTERS_EX
{
    public uint cb;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivateUsage;
}

internal static partial class Psapi
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessMemoryInfo(SafeProcessHandle hProcess, out PROCESS_MEMORY_COUNTERS_EX ppsmemCounters, uint cb);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumProcesses(uint* lpidProcess, uint cb, out uint lpcbNeeded);
}
