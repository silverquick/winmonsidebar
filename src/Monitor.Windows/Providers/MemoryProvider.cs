using System.Diagnostics;
using System.Runtime.InteropServices;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>
/// 物理メモリ・コミット量・内訳（キャッシュ/プール/圧縮メモリ等）・ページファイル・
/// 物理メモリモジュール(SPD)構成を供給する。
///
/// 物理メモリ本体は GlobalMemoryStatusEx から必ず取得する。コミット量は PDH の
/// \Memory\Committed Bytes / \Memory\Commit Limit を優先し、PDH が使えない場合は
/// MEMORYSTATUSEX のページファイル情報にフォールバックする。
/// 内訳(Cache/Pool/Standby/Modified/Free)は PDH の \Memory\* カウンターから、
/// システムキャッシュ・コミットピーク・ハンドル/プロセス/スレッド数は GetPerformanceInfo (psapi,
/// documented) から取る。Pool Paged/Nonpaged は PDH が失敗した場合 GetPerformanceInfo の
/// KernelPaged/KernelNonpaged にフォールバックする。
///
/// 圧縮メモリ("Memory Compression" プロセスのワーキングセット)とページファイル一覧
/// (NtQuerySystemInformation、失敗時は PDH \Paging File(*)\ + FindFirstFileW にフォールバック)は
/// 更新コストがあるため 2 秒に 1 回だけ再取得する。
///
/// メモリモジュール(SPD)は SMBIOS を Initialize() で 1 回だけ読み、以降はキャッシュを返す。
///
/// このプロバイダのどのメソッドも例外を外へ漏らさない。1つのセンサーが死んでも他は生き続ける。
/// </summary>
public sealed class MemoryProvider : IMetricProvider<MemorySnapshot>
{
    private static readonly TimeSpan SlowRefreshInterval = TimeSpan.FromSeconds(2);
    private const string MemoryCompressionProcessName = "Memory Compression";

    private PdhQuery? _pdhQuery;
    private PdhCounter? _committedBytesCounter;
    private PdhCounter? _commitLimitCounter;
    private PdhCounter? _cacheBytesCounter;
    private PdhCounter? _poolPagedCounter;
    private PdhCounter? _poolNonPagedCounter;
    private PdhCounter? _modifiedPageListCounter;
    private PdhCounter? _freeZeroPageListCounter;
    private PdhCounter? _standbyCoreCounter;
    private PdhCounter? _standbyNormalCounter;
    private PdhCounter? _standbyReserveCounter;
    private PdhCounter? _pagingFileUsagePercentTotalCounter;
    private PdhMultiCounter? _pagingFileUsageMultiCounter;
    private PdhMultiCounter? _pagingFileUsagePeakMultiCounter;

    // Initialize() 時に一度だけ読んでキャッシュする静的情報。
    private ulong _installedBytes;
    private IReadOnlyList<MemoryModuleInfo> _modules = Array.Empty<MemoryModuleInfo>();
    private int _slotsUsed;
    private int _slotsTotal;
    private int _representativeSpeedMhz;

    // 2秒に1回だけ更新する情報。
    private DateTime _lastSlowRefreshUtc = DateTime.MinValue;
    private ulong _compressedBytes;
    private IReadOnlyList<PageFileInfo> _pageFiles = Array.Empty<PageFileInfo>();

    private bool _disposed;

    public string Name => "Memory";

    /// <summary>物理メモリ取得は GlobalMemoryStatusEx のみに依存するため、Initialize が失敗しない限り常に true。</summary>
    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        try
        {
            // 物理メモリが取得できるかどうかで可用性を判定する。取れなければこのプロバイダは無意味。
            MEMORYSTATUSEX status = MEMORYSTATUSEX.Create();
            IsAvailable = Kernel32.GlobalMemoryStatusEx(ref status);
        }
        catch
        {
            IsAvailable = false;
        }

        // PDH によるコミット量・内訳の取得はベストエフォート。失敗してもフォールバックがあるので
        // IsAvailable には影響させない。
        try
        {
            _pdhQuery = PdhQuery.TryCreate();
            if (_pdhQuery is not null)
            {
                _committedBytesCounter = _pdhQuery.AddCounter(@"\Memory\Committed Bytes");
                _commitLimitCounter = _pdhQuery.AddCounter(@"\Memory\Commit Limit");
                _cacheBytesCounter = _pdhQuery.AddCounter(@"\Memory\Cache Bytes");
                _poolPagedCounter = _pdhQuery.AddCounter(@"\Memory\Pool Paged Bytes");
                _poolNonPagedCounter = _pdhQuery.AddCounter(@"\Memory\Pool Nonpaged Bytes");
                _modifiedPageListCounter = _pdhQuery.AddCounter(@"\Memory\Modified Page List Bytes");
                _freeZeroPageListCounter = _pdhQuery.AddCounter(@"\Memory\Free & Zero Page List Bytes");
                _standbyCoreCounter = _pdhQuery.AddCounter(@"\Memory\Standby Cache Core Bytes");
                _standbyNormalCounter = _pdhQuery.AddCounter(@"\Memory\Standby Cache Normal Priority Bytes");
                _standbyReserveCounter = _pdhQuery.AddCounter(@"\Memory\Standby Cache Reserve Bytes");
                _pagingFileUsagePercentTotalCounter = _pdhQuery.AddCounter(@"\Paging File(_Total)\% Usage");
                _pagingFileUsageMultiCounter = _pdhQuery.AddMultiCounter(@"\Paging File(*)\% Usage");
                _pagingFileUsagePeakMultiCounter = _pdhQuery.AddMultiCounter(@"\Paging File(*)\% Usage Peak");

                // レートカウンターではないが、最初の Collect をここで済ませておくことで最初の
                // Sample() から値が入っている状態にする。
                _pdhQuery.Collect();
            }
        }
        catch
        {
            _pdhQuery = null;
        }

        try
        {
            _installedBytes = Kernel32Ext.GetPhysicallyInstalledSystemMemory(out ulong installedKb) ? installedKb * 1024UL : 0;
        }
        catch
        {
            _installedBytes = 0;
        }

        try
        {
            _modules = Smbios.ReadMemoryModules(out _slotsTotal);
            _slotsUsed = _modules.Count;
            _representativeSpeedMhz = ComputeRepresentativeSpeedMhz(_modules);
        }
        catch
        {
            _modules = Array.Empty<MemoryModuleInfo>();
            _slotsUsed = 0;
            _slotsTotal = 0;
            _representativeSpeedMhz = 0;
        }

        (_, _, ulong initialPageSize) = ReadPerformanceInfo();
        RefreshSlowMetrics(initialPageSize);
    }

    public MemorySnapshot Sample(TimeSpan elapsed)
    {
        if (!IsAvailable)
        {
            return MemorySnapshot.Empty;
        }

        try
        {
            MEMORYSTATUSEX status = MEMORYSTATUSEX.Create();
            if (!Kernel32.GlobalMemoryStatusEx(ref status))
            {
                return MemorySnapshot.Empty;
            }

            ulong total = status.ullTotalPhys;
            ulong available = status.ullAvailPhys;
            ulong used = total > available ? total - available : 0;
            double usedPercent = total > 0 ? (double)used / total * 100.0 : 0.0;

            _pdhQuery?.Collect();

            ulong committed = ReadCounterUlongOrZero(_committedBytesCounter, out bool committedOk);
            ulong commitLimit = ReadCounterUlongOrZero(_commitLimitCounter, out bool limitOk);
            if (!committedOk || !limitOk)
            {
                (committed, commitLimit) = CommitFallback(status);
            }

            ulong cache = ReadCounterUlongOrZero(_cacheBytesCounter, out _);
            ulong poolPagedPdh = ReadCounterUlongOrZero(_poolPagedCounter, out bool poolPagedOk);
            ulong poolNonPagedPdh = ReadCounterUlongOrZero(_poolNonPagedCounter, out bool poolNonPagedOk);
            ulong modified = ReadCounterUlongOrZero(_modifiedPageListCounter, out _);
            ulong free = ReadCounterUlongOrZero(_freeZeroPageListCounter, out _);
            ulong standby = ReadCounterUlongOrZero(_standbyCoreCounter, out _)
                + ReadCounterUlongOrZero(_standbyNormalCounter, out _)
                + ReadCounterUlongOrZero(_standbyReserveCounter, out _);
            double pdhPageFileUsagePercent = ReadCounterDoubleOrZero(_pagingFileUsagePercentTotalCounter);

            (PERFORMANCE_INFORMATION perf, bool perfOk, ulong pageSize) = ReadPerformanceInfo();

            ulong systemCacheBytes = perfOk ? (ulong)perf.SystemCache * pageSize : 0;
            ulong commitPeakBytes = perfOk ? (ulong)perf.CommitPeak * pageSize : 0;
            int handleCount = perfOk ? (int)perf.HandleCount : 0;
            int processCount = perfOk ? (int)perf.ProcessCount : 0;
            int threadCount = perfOk ? (int)perf.ThreadCount : 0;

            ulong poolPaged = poolPagedOk ? poolPagedPdh : (perfOk ? (ulong)perf.KernelPaged * pageSize : 0);
            ulong poolNonPaged = poolNonPagedOk ? poolNonPagedPdh : (perfOk ? (ulong)perf.KernelNonpaged * pageSize : 0);

            if (DateTime.UtcNow - _lastSlowRefreshUtc >= SlowRefreshInterval)
            {
                RefreshSlowMetrics(pageSize);
            }

            ulong hardwareReserved = _installedBytes > total ? _installedBytes - total : 0;

            ulong pageFileTotal = 0, pageFileUsed = 0, pageFilePeak = 0;
            foreach (PageFileInfo pf in _pageFiles)
            {
                pageFileTotal += pf.TotalBytes;
                pageFileUsed += pf.UsedBytes;
                pageFilePeak += pf.PeakBytes;
            }

            double pageFileUsagePercent = pageFileTotal > 0
                ? 100.0 * pageFileUsed / pageFileTotal
                : pdhPageFileUsagePercent;

            return new MemorySnapshot
            {
                TotalBytes = total,
                AvailableBytes = available,
                UsedBytes = used,
                UsedPercent = usedPercent,
                CommittedBytes = committed,
                CommitLimitBytes = commitLimit,
                CachedBytes = cache,
                StandbyBytes = standby,
                ModifiedBytes = modified,
                FreeBytes = free,
                CompressedBytes = _compressedBytes,
                PoolPagedBytes = poolPaged,
                PoolNonPagedBytes = poolNonPaged,
                SystemCacheBytes = systemCacheBytes,
                InstalledBytes = _installedBytes,
                HardwareReservedBytes = hardwareReserved,
                CommitPeakBytes = commitPeakBytes,
                HandleCount = handleCount,
                ProcessCount = processCount,
                ThreadCount = threadCount,
                PageFiles = _pageFiles,
                PageFileTotalBytes = pageFileTotal,
                PageFileUsedBytes = pageFileUsed,
                PageFilePeakBytes = pageFilePeak,
                PageFileUsagePercent = Math.Clamp(pageFileUsagePercent, 0.0, 100.0),
                Modules = _modules,
                SlotsUsed = _slotsUsed,
                SlotsTotal = _slotsTotal,
                SpeedMhz = _representativeSpeedMhz,
            };
        }
        catch
        {
            return MemorySnapshot.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pdhQuery?.Dispose();
        _pdhQuery = null;
        _committedBytesCounter = null;
        _commitLimitCounter = null;
        _cacheBytesCounter = null;
        _poolPagedCounter = null;
        _poolNonPagedCounter = null;
        _modifiedPageListCounter = null;
        _freeZeroPageListCounter = null;
        _standbyCoreCounter = null;
        _standbyNormalCounter = null;
        _standbyReserveCounter = null;
        _pagingFileUsagePercentTotalCounter = null;
        _pagingFileUsageMultiCounter = null;
        _pagingFileUsagePeakMultiCounter = null;
    }

    // ---- コミット量 ----------------------------------------------------------------------

    private static (ulong Committed, ulong CommitLimit) CommitFallback(in MEMORYSTATUSEX status)
    {
        ulong totalPageFile = status.ullTotalPageFile;
        ulong availPageFile = status.ullAvailPageFile;
        ulong committed = totalPageFile > availPageFile ? totalPageFile - availPageFile : 0;
        return (committed, totalPageFile);
    }

    // ---- 2秒に1回だけ更新する情報 ---------------------------------------------------------

    /// <summary>圧縮メモリとページファイル一覧を再取得する。Sample() から SlowRefreshInterval おきに
    /// 呼ばれるほか、Initialize() でも一度呼んで初期値を埋める。例外を投げない。</summary>
    private void RefreshSlowMetrics(ulong pageSize)
    {
        _lastSlowRefreshUtc = DateTime.UtcNow;

        try
        {
            _compressedBytes = ReadCompressedBytes();
        }
        catch
        {
            _compressedBytes = 0;
        }

        try
        {
            _pageFiles = ReadPageFiles(pageSize);
        }
        catch
        {
            _pageFiles = Array.Empty<PageFileInfo>();
        }
    }

    /// <summary>"Memory Compression" システムプロセスのワーキングセットを読む。メモリ圧縮が無効な
    /// 環境（プロセスが存在しない）では 0 を返す。Process は必ず Dispose する。</summary>
    private static ulong ReadCompressedBytes()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(MemoryCompressionProcessName);
        }
        catch
        {
            return 0;
        }

        try
        {
            foreach (Process p in processes)
            {
                try
                {
                    return (ulong)p.WorkingSet64;
                }
                catch
                {
                    // このプロセスが読めなくても他は生きているかもしれないので続行。
                }
            }

            return 0;
        }
        finally
        {
            foreach (Process p in processes)
            {
                p.Dispose();
            }
        }
    }

    private IReadOnlyList<PageFileInfo> ReadPageFiles(ulong pageSize)
    {
        try
        {
            IReadOnlyList<PageFileInfo> native = ReadPageFilesNative(pageSize);
            if (native.Count > 0)
            {
                return native;
            }
        }
        catch
        {
            // フォールバックへ。
        }

        try
        {
            return ReadPageFilesFallback();
        }
        catch
        {
            return Array.Empty<PageFileInfo>();
        }
    }

    /// <summary>NtQuerySystemInformation(SystemPagefileInformation) で連結リストを読む。2パス呼び出し
    /// (STATUS_INFO_LENGTH_MISMATCH でバッファを広げる)。失敗したら空リストを返す（呼び出し側が
    /// フォールバックする）。</summary>
    private static IReadOnlyList<PageFileInfo> ReadPageFilesNative(ulong pageSize)
    {
        const int SystemPagefileInformation = 18;
        const uint StatusInfoLengthMismatch = 0xC0000004;

        int bufferSize = 4096;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                int status = Ntdll.NtQuerySystemInformation(SystemPagefileInformation, buffer, bufferSize, out int returnLength);
                uint unsignedStatus = unchecked((uint)status);

                if (unsignedStatus == StatusInfoLengthMismatch)
                {
                    bufferSize = returnLength > bufferSize ? returnLength : bufferSize * 2;
                    continue;
                }

                if (status != 0)
                {
                    return Array.Empty<PageFileInfo>();
                }

                return ParsePagefileBuffer(buffer, pageSize);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return Array.Empty<PageFileInfo>();
    }

    private static IReadOnlyList<PageFileInfo> ParsePagefileBuffer(IntPtr buffer, ulong pageSize)
    {
        var results = new List<PageFileInfo>();
        int offset = 0;

        // SYSTEM_PAGEFILE_INFORMATION (x64): +0 NextEntryOffset, +4 TotalSize, +8 TotalInUse,
        // +12 PeakUsage, +16 UNICODE_STRING PageFileName (Length:u16 @16, [pad], Buffer:IntPtr @24).
        while (true)
        {
            uint nextEntryOffset = (uint)Marshal.ReadInt32(buffer, offset);
            uint totalPages = (uint)Marshal.ReadInt32(buffer, offset + 4);
            uint inUsePages = (uint)Marshal.ReadInt32(buffer, offset + 8);
            uint peakPages = (uint)Marshal.ReadInt32(buffer, offset + 12);
            short nameLengthBytes = Marshal.ReadInt16(buffer, offset + 16);
            IntPtr namePtr = Marshal.ReadIntPtr(buffer, offset + 24);

            string rawName = nameLengthBytes > 0 && namePtr != IntPtr.Zero
                ? Marshal.PtrToStringUni(namePtr, nameLengthBytes / 2) ?? string.Empty
                : string.Empty;
            string path = NormalizeNativePath(rawName);

            if (!string.IsNullOrEmpty(path))
            {
                ulong totalBytes = totalPages * pageSize;
                ulong usedBytes = inUsePages * pageSize;
                ulong peakBytes = peakPages * pageSize;
                double usagePercent = totalBytes > 0 ? 100.0 * usedBytes / totalBytes : 0.0;

                results.Add(new PageFileInfo
                {
                    Path = path,
                    TotalBytes = totalBytes,
                    UsedBytes = usedBytes,
                    PeakBytes = peakBytes,
                    UsagePercent = Math.Clamp(usagePercent, 0.0, 100.0),
                });
            }

            if (nextEntryOffset == 0)
            {
                break;
            }

            offset += (int)nextEntryOffset;
        }

        return results;
    }

    private static string NormalizeNativePath(string raw)
    {
        const string nativePrefix = @"\??\";
        return raw.StartsWith(nativePrefix, StringComparison.Ordinal) ? raw[nativePrefix.Length..] : raw;
    }

    /// <summary>NtQuerySystemInformation が使えない場合のフォールバック。使用率は PDH
    /// \Paging File(*)\% Usage (+ Peak) から、サイズは FindFirstFileW のディレクトリ列挙から得る
    /// (pagefile.sys は開けないが列挙ならサイズが読める)。</summary>
    private IReadOnlyList<PageFileInfo> ReadPageFilesFallback()
    {
        if (_pagingFileUsageMultiCounter is null)
        {
            return Array.Empty<PageFileInfo>();
        }

        IReadOnlyList<PdhCounterItem> usageItems = _pagingFileUsageMultiCounter.GetValues();
        if (usageItems.Count == 0)
        {
            return Array.Empty<PageFileInfo>();
        }

        var peakLookup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_pagingFileUsagePeakMultiCounter is not null)
        {
            foreach (PdhCounterItem item in _pagingFileUsagePeakMultiCounter.GetValues())
            {
                peakLookup[item.InstanceName] = item.Value;
            }
        }

        var results = new List<PageFileInfo>();

        foreach (PdhCounterItem item in usageItems)
        {
            if (string.Equals(item.InstanceName, "_Total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = NormalizeInstancePath(item.InstanceName);
            ulong totalBytes = TryGetFileSize(path);
            double usagePercent = Math.Clamp(item.Value, 0.0, 100.0);
            double peakPercent = Math.Clamp(peakLookup.GetValueOrDefault(item.InstanceName, usagePercent), 0.0, 100.0);

            ulong usedBytes = totalBytes > 0 ? (ulong)(totalBytes * (usagePercent / 100.0)) : 0;
            ulong peakBytes = totalBytes > 0 ? (ulong)(totalBytes * (peakPercent / 100.0)) : 0;

            results.Add(new PageFileInfo
            {
                Path = path,
                TotalBytes = totalBytes,
                UsedBytes = usedBytes,
                PeakBytes = peakBytes,
                UsagePercent = usagePercent,
            });
        }

        return results;
    }

    /// <summary>PDH のインスタンス名 (例 "c:\pagefile.sys") をドライブ文字大文字の表示用パスに直す。</summary>
    private static string NormalizeInstancePath(string instanceName)
    {
        if (instanceName.Length >= 2 && instanceName[1] == ':')
        {
            return char.ToUpperInvariant(instanceName[0]) + instanceName[1..];
        }

        return instanceName;
    }

    private static ulong TryGetFileSize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        try
        {
            IntPtr handle = Kernel32Ext.FindFirstFileW(path, out WIN32_FIND_DATA data);
            if (handle == Kernel32Ext.InvalidHandleValue)
            {
                return 0;
            }

            try
            {
                return ((ulong)data.nFileSizeHigh << 32) | data.nFileSizeLow;
            }
            finally
            {
                Kernel32Ext.FindClose(handle);
            }
        }
        catch
        {
            return 0;
        }
    }

    // ---- GetPerformanceInfo ---------------------------------------------------------------

    private static (PERFORMANCE_INFORMATION Info, bool Ok, ulong PageSize) ReadPerformanceInfo()
    {
        try
        {
            bool ok = Kernel32Ext.GetPerformanceInfo(out PERFORMANCE_INFORMATION info, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>());
            ulong pageSize = ok && (ulong)info.PageSize > 0 ? (ulong)info.PageSize : 4096UL;
            return (info, ok, pageSize);
        }
        catch
        {
            return (default, false, 4096UL);
        }
    }

    // ---- PDH ヘルパー ----------------------------------------------------------------------

    private static ulong ReadCounterUlongOrZero(PdhCounter? counter, out bool ok)
    {
        if (counter is null)
        {
            ok = false;
            return 0;
        }

        double value = counter.GetDouble();
        ok = counter.HasValue;
        return ok && value > 0 ? (ulong)value : 0;
    }

    private static double ReadCounterDoubleOrZero(PdhCounter? counter)
    {
        if (counter is null)
        {
            return 0.0;
        }

        double value = counter.GetDouble();
        return counter.HasValue ? value : 0.0;
    }

    // ---- モジュール(SPD) --------------------------------------------------------------------

    /// <summary>各モジュールの ConfiguredSpeedMhz(実動作速度) の最頻値を代表値とする。SMBIOS が古く
    /// その値を持たない場合は SpeedMhz(定格速度) の最頻値にフォールバックする。</summary>
    private static int ComputeRepresentativeSpeedMhz(IReadOnlyList<MemoryModuleInfo> modules)
    {
        int configuredMode = ModeOfPositive(modules.Select(m => m.ConfiguredSpeedMhz));
        return configuredMode > 0 ? configuredMode : ModeOfPositive(modules.Select(m => m.SpeedMhz));
    }

    private static int ModeOfPositive(IEnumerable<int> values)
    {
        var positive = values.Where(v => v > 0).ToList();
        if (positive.Count == 0)
        {
            return 0;
        }

        return positive
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .First();
    }
}

// ---- P/Invoke 宣言 ----------------------------------------------------------------------
// このプロバイダ専用のネイティブ宣言。既存の Native フォルダ (Kernel32.cs / Psapi.cs) は別担当と
// 競合する制約のため触らず、ここに閉じ込める。

[StructLayout(LayoutKind.Sequential)]
internal struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WIN32_FIND_DATA
{
    public uint dwFileAttributes;
    public FILETIME ftCreationTime;
    public FILETIME ftLastAccessTime;
    public FILETIME ftLastWriteTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0;
    public uint dwReserved1;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string cFileName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
    public string cAlternateFileName;
}

/// <summary>ntdll.dll の undocumented だが広く使われる NtQuerySystemInformation。
/// SystemPagefileInformation(18) のみに使用する。</summary>
internal static partial class Ntdll
{
    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);
}

/// <summary>この Provider が必要とする追加の kernel32/psapi 宣言。GetPerformanceInfo と
/// GetPhysicallyInstalledSystemMemory は LibraryImport (blittable のみ)、FindFirstFileW は
/// WIN32_FIND_DATA が固定長文字列マーシャリングを要求するため DllImport を使う
/// (プロジェクトの「マーシャリング制約に合わない箇所だけ DllImport」規則に従う)。</summary>
internal static partial class Kernel32Ext
{
    public static readonly IntPtr InvalidHandleValue = new(-1);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(IntPtr hFindFile);
}
