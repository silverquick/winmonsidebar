using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monitor.Windows.Native;

public enum PdhReadStatus { Success, WarmingUp, Failed }

internal static class PdhReadHealth
{
    internal static PdhReadStatus Classify(uint status, bool warmingUp) =>
        status is 0 or 1 ? PdhReadStatus.Success :
        warmingUp && (status is Pdh.PDH_INVALID_DATA or Pdh.PDH_NO_DATA or Pdh.PDH_CSTATUS_INVALID_DATA)
            ? PdhReadStatus.WarmingUp : PdhReadStatus.Failed;

    internal static PdhReadStatus ClassifyApi(uint status, bool warmingUp) =>
        status == 1 ? PdhReadStatus.Failed : Classify(status, warmingUp);

    internal static bool RequiredFailed(bool present, PdhReadStatus status, bool alternateAvailable = false) =>
        present && status == PdhReadStatus.Failed && !alternateAvailable;

    // Only wildcard item statuses: API errors and scalar reads remain strict.
    internal static bool IsTransientItem(uint status) => status is
        0x800007D1 or Pdh.PDH_CSTATUS_INVALID_DATA or Pdh.PDH_INVALID_DATA or
        Pdh.PDH_CALC_NEGATIVE_DENOMINATOR or 0x800007D7 or 0x800007D8;

    internal static bool IsCalculationError(uint status) => status is
        Pdh.PDH_CALC_NEGATIVE_DENOMINATOR or 0x800007D7 or 0x800007D8;
}

/// <summary>One value read from a wildcard-expanded PDH counter, e.g. one CPU core or one disk instance.</summary>
public readonly record struct PdhCounterItem(string InstanceName, double Value);

/// <summary>One value read from a wildcard-expanded PDH counter without string allocation.</summary>
public readonly ref struct PdhItemSpan
{
    public ReadOnlySpan<char> InstanceName { get; }
    public double Value { get; }
    public PdhReadStatus ReadStatus { get; }

    public PdhItemSpan(ReadOnlySpan<char> instanceName, double value, PdhReadStatus readStatus = PdhReadStatus.Success)
    {
        InstanceName = instanceName;
        Value = value;
        ReadStatus = readStatus;
    }
}

/// <summary>Ref-struct enumerator that walks the native PDH counter value item buffer directly.</summary>
public ref struct PdhCounterEnumerator
{
    private readonly IntPtr _buffer;
    private readonly PdhMultiCounter? _owner;
    private readonly int _itemCount;
    private int _index;
    private PdhItemSpan _current;

    internal PdhCounterEnumerator(IntPtr buffer, int itemCount, PdhMultiCounter? owner = null)
    {
        _owner = owner;
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
                PdhReadStatus readStatus = _owner?.RecordItemStatus(pItem->FmtValue.CStatus)
                    ?? PdhReadHealth.Classify(pItem->FmtValue.CStatus, warmingUp: false);
                double val = PdhCounter.IsErrorStatus(pItem->FmtValue.CStatus) ? 0.0 : pItem->FmtValue.doubleValue;
                _current = new PdhItemSpan(name, val, readStatus);
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
    internal delegate uint GetValueFunc(IntPtr handle, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    private readonly GetValueFunc _getValue;
    public uint LastNativeStatus { get; private set; }
    private int _readAttempts;
    internal PdhQuery? Query { get; set; }
    public PdhReadStatus LastReadStatus { get; private set; }
    public bool LastReadFailed => LastReadStatus == PdhReadStatus.Failed;
    private readonly IntPtr _handle;

    internal PdhCounter(string path, IntPtr handle) : this(path, handle, Pdh.PdhGetFormattedCounterValue) { }

    internal PdhCounter(string path, IntPtr handle, GetValueFunc getValue)
    {
        _getValue = getValue;
        Path = path;
        _handle = handle;
    }

    public string Path { get; }

    /// <summary>True after the most recent GetDouble() call returned a real (non-error) value.</summary>
    public bool HasValue { get; private set; }

    /// <summary>Reads the current formatted value. Never throws; returns 0 and sets HasValue=false on any
    /// PDH error status. LastReadStatus distinguishes initial warmup from a failed read.</summary>
    public double GetDouble()
    {
        bool warmingUp = Query is null ? ++_readAttempts == 1 : Query.CollectionCount <= 1;
        try
        {
            uint status = _getValue(_handle, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100, out _, out PDH_FMT_COUNTERVALUE value);
            LastNativeStatus = status != 0 ? status : value.CStatus;
            LastReadStatus = status != 0 ? PdhReadHealth.ClassifyApi(status, warmingUp) : PdhReadHealth.Classify(value.CStatus, warmingUp);
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
            LastReadStatus = PdhReadStatus.Failed;
            LastNativeStatus = Pdh.PDH_INVALID_ARGUMENT;
            return 0.0;
        }
    }

    internal static bool IsErrorStatus(uint cstatus)
        => cstatus is not (Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA);
}

/// <summary>A wildcard-instance PDH counter (e.g. "\Processor Information(*)\% Processor Utility" or
/// "\PhysicalDisk(*)\% Disk Time"), returning one value per matched instance.</summary>
public sealed class PdhMultiCounter : IDisposable
{
    /// <summary>Accept valid items despite transient errors on other instances. All-invalid reads
    /// and API errors still fail. Opt-in so device-specific disk health stays strict.</summary>
    public bool TolerateInstanceChurn { get; set; }
    public uint LastNativeStatus { get; private set; }
    private bool _hasValidItem;
    private bool _hasFatalItem;
    private PdhReadStatus _itemStatus;
    private int _readAttempts;
    private bool _warmingUp;
    internal PdhQuery? Query { get; set; }
    public PdhReadStatus LastReadStatus { get; private set; }
    public bool LastReadFailed => LastReadStatus == PdhReadStatus.Failed;

    public bool LastApiReadFailed { get; private set; }

    internal PdhReadStatus RecordItemStatus(uint status)
    {
        PdhReadStatus itemStatus = PdhReadHealth.Classify(status, _warmingUp);
        if (itemStatus == PdhReadStatus.Success) _hasValidItem = true;
        else
        {
            if (!_hasFatalItem) LastNativeStatus = status;
            if (!PdhReadHealth.IsTransientItem(status)) _hasFatalItem = true;
        }
        if (itemStatus > _itemStatus) _itemStatus = itemStatus;
        LastReadStatus = TolerateInstanceChurn && _hasValidItem && !_hasFatalItem
            ? PdhReadStatus.Success : _itemStatus;
        return itemStatus;
    }

    private PdhCounterEnumerator Empty(uint status)
    {
        if (status == 0) status = Pdh.PDH_NO_DATA;
        _itemCount = 0;
        LastNativeStatus = status;
        LastReadStatus = PdhReadHealth.ClassifyApi(status, _warmingUp);
        LastApiReadFailed = LastReadFailed;
        return new PdhCounterEnumerator(IntPtr.Zero, 0);
    }

    private PdhCounterEnumerator Values(uint itemCount)
    {
        _itemCount = itemCount;
        _hasValidItem = false;
        _hasFatalItem = false;
        _itemStatus = PdhReadStatus.Success;
        LastNativeStatus = itemCount == 0 ? Pdh.PDH_NO_DATA : 0;
        LastReadStatus = itemCount == 0 && !_warmingUp ? PdhReadStatus.Failed : PdhReadStatus.Success;
        LastApiReadFailed = false;
        return new PdhCounterEnumerator(_buffer, (int)itemCount, this);
    }

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
        _warmingUp = Query is null ? ++_readAttempts == 1 : Query.CollectionCount <= 1;
        if (_disposed || _handle == IntPtr.Zero)
        {
            return Empty(Pdh.PDH_INVALID_ARGUMENT);
        }

        const uint format = Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100;
        const int maxAttempts = 3;

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                // 1. Fast path / steady state with existing buffer
                if (_buffer != IntPtr.Zero && _bufferCapacity > 0)
                {
                    uint suppliedSize = _bufferCapacity;
                    uint itemCount = 0;
                    uint status = _getArrayFunc(_handle, format, ref suppliedSize, ref itemCount, _buffer);

                    if (status == 0)
                    {
                        return Values(itemCount);
                    }

                    if (status != Pdh.PDH_MORE_DATA && status != Pdh.PDH_INVALID_ARGUMENT)
                    {
                        return Empty(status);
                    }

                    // Insufficient existing buffer: DO NOT trust suppliedSize.
                    // Free existing buffer and re-probe size from NULL/0.
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                    _bufferCapacity = 0;
                    _itemCount = 0;
                }

                // 2. Reliable probe with NULL/0
                uint requiredSize = 0;
                uint probeItemCount = 0;
                uint probeStatus = _getArrayFunc(_handle, format, ref requiredSize, ref probeItemCount, IntPtr.Zero);

                if (probeStatus != Pdh.PDH_MORE_DATA || requiredSize == 0)
                {
                    return Empty(probeStatus == Pdh.PDH_MORE_DATA ? Pdh.PDH_INVALID_ARGUMENT : probeStatus);
                }

                _buffer = Marshal.AllocHGlobal((int)requiredSize);
                _bufferCapacity = requiredSize;

                uint fetchSize = _bufferCapacity;
                uint fetchItemCount = 0;
                uint fetchStatus = _getArrayFunc(_handle, format, ref fetchSize, ref fetchItemCount, _buffer);

                if (fetchStatus == 0)
                {
                    return Values(fetchItemCount);
                }

                if (fetchStatus == Pdh.PDH_MORE_DATA || fetchStatus == Pdh.PDH_INVALID_ARGUMENT)
                {
                    // Instance count increased between probe and fetch.
                    // Discard fetchSize and re-probe from NULL/0 on next attempt.
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                    _bufferCapacity = 0;
                    _itemCount = 0;
                    continue;
                }

                // Other unexpected failure status
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferCapacity = 0;
                return Empty(fetchStatus);
            }

            return Empty(Pdh.PDH_INVALID_ARGUMENT);
        }
        catch
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferCapacity = 0;
            }
            return Empty(Pdh.PDH_INVALID_ARGUMENT);
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
    public uint LastCollectionStatus { get; private set; }
    internal int CollectionCount { get; private set; }
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

            return new PdhCounter(englishPath, counterHandle) { Query = this };
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

            var counter = new PdhMultiCounter(englishPath, counterHandle) { Query = this };
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
        CollectionCount++;
        if (_disposed || _handle.IsInvalid)
        {
            LastCollectionStatus = Pdh.PDH_INVALID_ARGUMENT;
            return false;
        }

        try
        {
            LastCollectionStatus = Pdh.PdhCollectQueryData(_handle.DangerousGetHandle());
            return PdhReadHealth.ClassifyApi(LastCollectionStatus, CollectionCount == 1) != PdhReadStatus.Failed;
        }
        catch
        {
            LastCollectionStatus = Pdh.PDH_INVALID_ARGUMENT;
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
