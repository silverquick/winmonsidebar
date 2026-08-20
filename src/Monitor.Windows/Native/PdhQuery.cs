using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monitor.Windows.Native;

/// <summary>One value read from a wildcard-expanded PDH counter, e.g. one CPU core or one disk instance.</summary>
public readonly record struct PdhCounterItem(string InstanceName, double Value);

/// <summary>SafeHandle wrapper around a PDH query handle (HQUERY). Closing it via PdhCloseQuery also
/// invalidates every counter handle that was added to it, so counters themselves do not need a SafeHandle.</summary>
internal sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdhQueryHandle() : base(true)
    {
    }

    public SafePdhQueryHandle(IntPtr preexistingHandle) : base(true)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle() => Pdh.PdhCloseQuery(handle) == 0;
}

/// <summary>A single-instance PDH counter (e.g. "\Processor Information(_Total)\% Processor Utility").</summary>
public sealed class PdhCounter
{
    private readonly IntPtr _handle;

    internal PdhCounter(string path, IntPtr handle)
    {
        Path = path;
        _handle = handle;
    }

    public string Path { get; }

    /// <summary>True after the most recent GetDouble() call returned a real (non-error) value.</summary>
    public bool HasValue { get; private set; }

    /// <summary>Reads the current formatted value. Never throws; returns 0 and sets HasValue=false on any
    /// PDH error status (including "not enough data yet" on the first sample after AddCounter).</summary>
    public double GetDouble()
    {
        try
        {
            uint status = Pdh.PdhGetFormattedCounterValue(_handle, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100, out _, out PDH_FMT_COUNTERVALUE value);
            if (status != 0 || IsErrorStatus(value.CStatus))
            {
                HasValue = false;
                return 0.0;
            }

            HasValue = true;
            return value.doubleValue;
        }
        catch
        {
            HasValue = false;
            return 0.0;
        }
    }

    internal static bool IsErrorStatus(uint cstatus)
        => cstatus == Pdh.PDH_INVALID_DATA || cstatus == Pdh.PDH_NO_DATA || cstatus == Pdh.PDH_CALC_NEGATIVE_DENOMINATOR;
}

/// <summary>A wildcard-instance PDH counter (e.g. "\Processor Information(*)\% Processor Utility" or
/// "\PhysicalDisk(*)\% Disk Time"), returning one value per matched instance.</summary>
public sealed class PdhMultiCounter
{
    private readonly IntPtr _handle;

    internal PdhMultiCounter(string path, IntPtr handle)
    {
        Path = path;
        _handle = handle;
    }

    public string Path { get; }

    /// <summary>Reads all current instance values. Never throws; returns an empty list on any failure
    /// (including "not enough data yet" on the first sample after AddMultiCounter).</summary>
    public IReadOnlyList<PdhCounterItem> GetValues()
    {
        const uint format = Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100;

        try
        {
            uint bufferSize = 0;
            uint itemCount = 0;
            uint status = Pdh.PdhGetFormattedCounterArrayW(_handle, format, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != Pdh.PDH_MORE_DATA || bufferSize == 0)
            {
                return Array.Empty<PdhCounterItem>();
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = Pdh.PdhGetFormattedCounterArrayW(_handle, format, ref bufferSize, ref itemCount, buffer);
                if (status != 0)
                {
                    return Array.Empty<PdhCounterItem>();
                }

                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
                var results = new List<PdhCounterItem>((int)itemCount);
                for (int i = 0; i < itemCount; i++)
                {
                    IntPtr itemPtr = IntPtr.Add(buffer, i * itemSize);
                    PDH_FMT_COUNTERVALUE_ITEM_W item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(itemPtr);

                    string name = item.szName != IntPtr.Zero
                        ? Marshal.PtrToStringUni(item.szName) ?? string.Empty
                        : string.Empty;
                    double value = PdhCounter.IsErrorStatus(item.FmtValue.CStatus) ? 0.0 : item.FmtValue.doubleValue;

                    results.Add(new PdhCounterItem(name, value));
                }

                return results;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return Array.Empty<PdhCounterItem>();
        }
    }
}

/// <summary>Managed wrapper around a PDH query (HQUERY). Not thread-safe; intended for exclusive use by
/// the background sampling thread in MetricsHub. Always use PdhAddEnglishCounterW under the hood so counter
/// paths work regardless of the OS display language.</summary>
public sealed class PdhQuery : IDisposable
{
    private readonly SafePdhQueryHandle _handle;
    private bool _disposed;

    private PdhQuery(SafePdhQueryHandle handle)
    {
        _handle = handle;
    }

    /// <summary>Opens a new PDH query. Returns null (never throws) if PDH is unavailable in this environment.</summary>
    public static PdhQuery? TryCreate()
    {
        try
        {
            uint status = Pdh.PdhOpenQueryW(null, IntPtr.Zero, out IntPtr rawHandle);
            if (status != 0 || rawHandle == IntPtr.Zero)
            {
                return null;
            }

            return new PdhQuery(new SafePdhQueryHandle(rawHandle));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Adds a single-instance counter. Returns null (never throws) if the counter path does not
    /// exist on this machine (e.g. a GPU engine counter with no GPU present).</summary>
    public PdhCounter? AddCounter(string englishPath)
    {
        if (_disposed || _handle.IsInvalid)
        {
            return null;
        }

        try
        {
            uint status = Pdh.PdhAddEnglishCounterW(_handle.DangerousGetHandle(), englishPath, IntPtr.Zero, out IntPtr counterHandle);
            if (status != 0 || counterHandle == IntPtr.Zero)
            {
                return null;
            }

            return new PdhCounter(englishPath, counterHandle);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Adds a wildcard-instance counter (path containing "(*)"). Returns null (never throws) if
    /// the counter object does not exist on this machine.</summary>
    public PdhMultiCounter? AddMultiCounter(string englishPath)
    {
        if (_disposed || _handle.IsInvalid)
        {
            return null;
        }

        try
        {
            uint status = Pdh.PdhAddEnglishCounterW(_handle.DangerousGetHandle(), englishPath, IntPtr.Zero, out IntPtr counterHandle);
            if (status != 0 || counterHandle == IntPtr.Zero)
            {
                return null;
            }

            return new PdhMultiCounter(englishPath, counterHandle);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Collects a new sample for every counter on this query. Rate-based counters only produce a
    /// meaningful value starting from the second Collect() call. Returns false (never throws) on failure.</summary>
    public bool Collect()
    {
        if (_disposed || _handle.IsInvalid)
        {
            return false;
        }

        try
        {
            return Pdh.PdhCollectQueryData(_handle.DangerousGetHandle()) == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
