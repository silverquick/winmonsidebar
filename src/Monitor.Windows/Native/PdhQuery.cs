using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monitor.Windows.Native;

/// <summary>One value read from a wildcard-expanded PDH counter, e.g. one CPU core or one disk instance.</summary>
public readonly record struct PdhCounterItem(string InstanceName, double Value);

/// <summary>One value read from a wildcard-expanded PDH counter without string allocation.</summary>
public readonly ref struct PdhItemSpan
{
    public ReadOnlySpan<char> InstanceName { get; }
    public double Value { get; }

    public PdhItemSpan(ReadOnlySpan<char> instanceName, double value)
    {
        InstanceName = instanceName;
        Value = value;
    }
}

/// <summary>Ref-struct enumerator that walks the native PDH counter value item buffer directly.</summary>
public ref struct PdhCounterEnumerator
{
    private readonly IntPtr _buffer;
    private readonly int _itemCount;
    private int _index;
    private PdhItemSpan _current;

    internal PdhCounterEnumerator(IntPtr buffer, int itemCount)
    {
        _buffer = buffer;
        _itemCount = itemCount;
        _index = -1;
        _current = default;
    }

    public PdhItemSpan Current => _current;

    public bool MoveNext()
    {
        _index++;
        if (_index < _itemCount && _buffer != IntPtr.Zero)
        {
            unsafe
            {
                var pItem = (PDH_FMT_COUNTERVALUE_ITEM_W*)_buffer + _index;
                ReadOnlySpan<char> name = pItem->szName == IntPtr.Zero
                    ? ReadOnlySpan<char>.Empty
                    : MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)pItem->szName);
                double val = PdhCounter.IsErrorStatus(pItem->FmtValue.CStatus) ? 0.0 : pItem->FmtValue.doubleValue;
                _current = new PdhItemSpan(name, val);
                return true;
            }
        }

        return false;
    }

    public PdhCounterEnumerator GetEnumerator() => this;
}

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
public sealed class PdhMultiCounter : IDisposable
{
    internal delegate uint PdhGetCounterArrayFunc(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer);

    private readonly IntPtr _handle;
    private readonly PdhGetCounterArrayFunc _getArrayFunc;
    private IntPtr _buffer = IntPtr.Zero;
    private uint _bufferCapacity = 0;
    private uint _itemCount = 0;
    private bool _disposed;

    internal PdhMultiCounter(string path, IntPtr handle)
        : this(path, handle, Pdh.PdhGetFormattedCounterArrayW)
    {
    }

    internal PdhMultiCounter(string path, IntPtr handle, PdhGetCounterArrayFunc getArrayFunc)
    {
        Path = path;
        _handle = handle;
        _getArrayFunc = getArrayFunc;
    }

    public string Path { get; }

    ~PdhMultiCounter()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferCapacity = 0;
            _itemCount = 0;
        }
    }

    /// <summary>Reads all current instance values without string or array allocations by returning a ref-struct enumerator.</summary>
    public PdhCounterEnumerator Enumerate()
    {
        if (_disposed || _handle == IntPtr.Zero)
        {
            return new PdhCounterEnumerator(IntPtr.Zero, 0);
        }

        const uint format = Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100;

        try
        {
            uint bufferSize = _bufferCapacity;
            uint itemCount = 0;

            if (_buffer == IntPtr.Zero || bufferSize == 0)
            {
                uint status = _getArrayFunc(_handle, format, ref bufferSize, ref itemCount, IntPtr.Zero);
                if (status != Pdh.PDH_MORE_DATA || bufferSize == 0)
                {
                    _itemCount = 0;
                    return new PdhCounterEnumerator(IntPtr.Zero, 0);
                }

                _buffer = Marshal.AllocHGlobal((int)bufferSize);
                _bufferCapacity = bufferSize;
            }

            uint getStatus = _getArrayFunc(_handle, format, ref bufferSize, ref itemCount, _buffer);
            if (getStatus == Pdh.PDH_MORE_DATA)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal((int)bufferSize);
                _bufferCapacity = bufferSize;

                getStatus = _getArrayFunc(_handle, format, ref bufferSize, ref itemCount, _buffer);
            }

            if (getStatus != 0)
            {
                _itemCount = 0;
                return new PdhCounterEnumerator(IntPtr.Zero, 0);
            }

            _itemCount = itemCount;
            return new PdhCounterEnumerator(_buffer, (int)itemCount);
        }
        catch
        {
            _itemCount = 0;
            return new PdhCounterEnumerator(IntPtr.Zero, 0);
        }
    }

    /// <summary>Reads all current instance values into managed PdhCounterItem objects. Backward compatibility method.</summary>
    public IReadOnlyList<PdhCounterItem> GetValues()
    {
        var results = new List<PdhCounterItem>();
        foreach (PdhItemSpan item in Enumerate())
        {
            results.Add(new PdhCounterItem(item.InstanceName.ToString(), item.Value));
        }
        return results;
    }
}

/// <summary>Managed wrapper around a PDH query (HQUERY). Not thread-safe; intended for exclusive use by
/// the background sampling thread in MetricsHub. Always use PdhAddEnglishCounterW under the hood so counter
/// paths work regardless of the OS display language.</summary>
public sealed class PdhQuery : IDisposable
{
    private readonly SafePdhQueryHandle _handle;
    private readonly List<PdhMultiCounter> _multiCounters = new();
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

            var counter = new PdhMultiCounter(englishPath, counterHandle);
            _multiCounters.Add(counter);
            return counter;
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
        foreach (PdhMultiCounter counter in _multiCounters)
        {
            counter.Dispose();
        }
        _multiCounters.Clear();
        _handle.Dispose();
    }
}
