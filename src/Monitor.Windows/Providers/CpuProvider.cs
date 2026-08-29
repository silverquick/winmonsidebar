using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>
/// CPU の全体使用率・コアごとの使用率・クロックを供給する。
/// 全体使用率は Kernel32.GetSystemTimes の差分、コアごとの使用率と現在クロックは PDH を使う。
/// PDH が使えない環境でも全体使用率だけは機能するように、それぞれ独立に失敗を許容する。
/// PackageTemperatureC / PackagePowerWatts はこのプロバイダの範囲外（常に null）。
/// LibreHardwareMonitor 側のプロバイダが埋め、MetricsHub/ViewModel 層で合成する。
/// </summary>
public sealed partial class CpuProvider : IMetricProvider<CpuSnapshot>, IDetailSamplingProvider
{
    private const string PerCoreCounterPath = @"\Processor Information(*)\% Processor Time";
    private const string PerCoreClockCounterPath = @"\Processor Information(*)\% Processor Performance";
    private const string PerformancePercentCounterPath = @"\Processor Information(_Total)\% Processor Performance";

    private const int RelationProcessorCore = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    private PdhQuery? _pdhQuery;
    private PdhMultiCounter? _perCoreCounter;
    private PdhMultiCounter? _perCoreClockCounter;
    private PdhCounter? _performancePercentCounter;

    private bool _hasPrevSystemTimes;
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;

    private double _baseClockMhz;
    private string _modelName = "";
    private int _physicalCoreCount;
    private int _detailSamplingEnabled = 1;
    private readonly List<(long Key, double Value)> _coreBuffer = new();

    public string Name => "CPU";

    public bool IsAvailable { get; private set; }

    public void SetDetailSamplingEnabled(bool enabled) =>
        Volatile.Write(ref _detailSamplingEnabled, enabled ? 1 : 0);

    public void Initialize()
    {
        try
        {
            _baseClockMhz = ReadBaseClockMhz();
            _modelName = ReadModelName();
            _physicalCoreCount = ReadPhysicalCoreCount();

            _pdhQuery = PdhQuery.TryCreate();
            if (_pdhQuery is not null)
            {
                _perCoreCounter = _pdhQuery.AddMultiCounter(PerCoreCounterPath);
                _perCoreClockCounter = _pdhQuery.AddMultiCounter(PerCoreClockCounterPath);
                _performancePercentCounter = _pdhQuery.AddCounter(PerformancePercentCounterPath);
                _pdhQuery.Collect();
            }

            // GetSystemTimes によるトータル使用率は PDH 不要で機能するため、
            // このプロバイダ自体は常に IsAvailable = true とする。
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public CpuSnapshot Sample(TimeSpan elapsed)
    {
        if (!IsAvailable)
        {
            return CpuSnapshot.Empty;
        }

        try
        {
            double totalUsagePercent = SampleTotalUsagePercent();
            bool includeDetails = Volatile.Read(ref _detailSamplingEnabled) != 0;
            IReadOnlyList<double> perCoreUsagePercent;
            IReadOnlyList<double> perCoreClockMhz;
            if (includeDetails)
            {
                perCoreUsagePercent = SamplePerCoreUsagePercent();
                perCoreClockMhz = SamplePerCoreClockMhz();
            }
            else
            {
                // 現在クロックの単一カウンターを更新するためだけに1回 Collect する。
                _pdhQuery?.Collect();
                perCoreUsagePercent = Array.Empty<double>();
                perCoreClockMhz = Array.Empty<double>();
            }
            double currentClockMhz = SampleCurrentClockMhz();

            return new CpuSnapshot
            {
                ModelName = _modelName,
                TotalUsagePercent = totalUsagePercent,
                PerCoreUsagePercent = perCoreUsagePercent,
                PerCoreClockMhz = perCoreClockMhz,
                CurrentClockMhz = currentClockMhz,
                BaseClockMhz = _baseClockMhz,
                PhysicalCoreCount = _physicalCoreCount,
                LogicalCoreCount = Environment.ProcessorCount,
                PackageTemperatureC = null,
                PackagePowerWatts = null,
            };
        }
        catch
        {
            return CpuSnapshot.Empty;
        }
    }

    private double SampleTotalUsagePercent()
    {
        try
        {
            if (!Kernel32.GetSystemTimes(out FILETIME idleFt, out FILETIME kernelFt, out FILETIME userFt))
            {
                return 0.0;
            }

            ulong idle = idleFt.ToUInt64();
            ulong kernel = kernelFt.ToUInt64();
            ulong user = userFt.ToUInt64();

            if (!_hasPrevSystemTimes)
            {
                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;
                _hasPrevSystemTimes = true;
                return 0.0;
            }

            ulong idleDelta = idle - _prevIdle;
            ulong kernelDelta = kernel - _prevKernel;
            ulong userDelta = user - _prevUser;

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;

            ulong total = kernelDelta + userDelta;
            double usage = total == 0 ? 0.0 : (1.0 - (double)idleDelta / total) * 100.0;

            return Math.Clamp(usage, 0.0, 100.0);
        }
        catch
        {
            return 0.0;
        }
    }

    private IReadOnlyList<double> SamplePerCoreUsagePercent()
    {
        if (_perCoreCounter is null || _pdhQuery is null)
        {
            return Array.Empty<double>();
        }

        try
        {
            _pdhQuery.Collect();

            _coreBuffer.Clear();
            foreach (PdhItemSpan item in _perCoreCounter.Enumerate())
            {
                if (TryParseCoreInstance(item.InstanceName, out long sortKey))
                {
                    _coreBuffer.Add((sortKey, Math.Clamp(item.Value, 0.0, 100.0)));
                }
            }

            if (_coreBuffer.Count == 0)
            {
                return Array.Empty<double>();
            }

            _coreBuffer.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            var cores = new double[_coreBuffer.Count];
            for (int i = 0; i < _coreBuffer.Count; i++)
            {
                cores[i] = _coreBuffer[i].Value;
            }

            return cores;
        }
        catch
        {
            return Array.Empty<double>();
        }
    }

    private IReadOnlyList<double> SamplePerCoreClockMhz()
    {
        if (_perCoreClockCounter is null || _pdhQuery is null || _baseClockMhz <= 0.0)
        {
            return Array.Empty<double>();
        }

        try
        {
            // 直近の Collect() は SamplePerCoreUsagePercent 内で既に行われているため、ここでは
            // 同じサンプル時点の値をそのまま読む（GetValues 自体は Collect を必要としない）。
            _coreBuffer.Clear();
            foreach (PdhItemSpan item in _perCoreClockCounter.Enumerate())
            {
                if (TryParseCoreInstance(item.InstanceName, out long sortKey))
                {
                    _coreBuffer.Add((sortKey, _baseClockMhz * Math.Max(0.0, item.Value) / 100.0));
                }
            }

            if (_coreBuffer.Count == 0)
            {
                return Array.Empty<double>();
            }

            // PerCoreUsagePercent と対応が崩れないよう、同じ sortKey で同じ規則で並べる。
            _coreBuffer.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            var clocks = new double[_coreBuffer.Count];
            for (int i = 0; i < _coreBuffer.Count; i++)
            {
                clocks[i] = _coreBuffer[i].Value;
            }

            return clocks;
        }
        catch
        {
            return Array.Empty<double>();
        }
    }

    private double SampleCurrentClockMhz()
    {
        if (_performancePercentCounter is null)
        {
            return _baseClockMhz;
        }

        try
        {
            double perf = _performancePercentCounter.GetDouble();
            if (!_performancePercentCounter.HasValue)
            {
                return _baseClockMhz;
            }

            return _baseClockMhz * perf / 100.0;
        }
        catch
        {
            return _baseClockMhz;
        }
    }

    internal static bool TryParseCoreInstance(ReadOnlySpan<char> instanceName, out long sortKey)
    {
        sortKey = 0;
        ReadOnlySpan<char> trimmed = instanceName.Trim();
        if (IsTotalInstance(trimmed))
        {
            return false;
        }

        int comma = trimmed.IndexOf(',');
        if (comma < 0)
        {
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int proc) && proc >= 0)
            {
                sortKey = proc;
                return true;
            }
            return false;
        }

        ReadOnlySpan<char> groupSpan = trimmed[..comma];
        ReadOnlySpan<char> procSpan = trimmed[(comma + 1)..];

        if (int.TryParse(groupSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int group) && group >= 0 &&
            int.TryParse(procSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num) && num >= 0)
        {
            sortKey = ((long)group << 32) | (uint)num;
            return true;
        }

        return false;
    }

    internal static bool IsTotalInstance(ReadOnlySpan<char> instanceName)
    {
        return instanceName.Equals("_Total", StringComparison.OrdinalIgnoreCase)
            || instanceName.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase);
    }

    private static double ReadBaseClockMhz()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            object? value = key?.GetValue("~MHz");
            if (value is int mhz)
            {
                return mhz;
            }

            return 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static string ReadModelName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            object? value = key?.GetValue("ProcessorNameString");
            return value is string s ? s.Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// GetLogicalProcessorInformationEx(RelationProcessorCore, ...) で物理コア数を数える。
    /// SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX は可変長構造体の連続なので、各エントリの
    /// Relationship(4byte)/Size(4byte) ヘッダだけを読み、Size 分ポインタを進めて次のエントリへ進む。
    /// 失敗時は Environment.ProcessorCount / 2 にフォールバックする。
    /// </summary>
    private static int ReadPhysicalCoreCount()
    {
        try
        {
            uint length = 0;
            bool ok = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
            if (ok || length == 0)
            {
                return FallbackPhysicalCoreCount();
            }

            if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER)
            {
                return FallbackPhysicalCoreCount();
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                ok = GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length);
                if (!ok)
                {
                    return FallbackPhysicalCoreCount();
                }

                int count = 0;
                IntPtr current = buffer;
                long remaining = length;
                while (remaining > 0)
                {
                    int relationship = Marshal.ReadInt32(current);
                    int size = Marshal.ReadInt32(current, 4);
                    if (size <= 0)
                    {
                        break;
                    }

                    if (relationship == RelationProcessorCore)
                    {
                        count++;
                    }

                    current = IntPtr.Add(current, size);
                    remaining -= size;
                }

                return count > 0 ? count : FallbackPhysicalCoreCount();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return FallbackPhysicalCoreCount();
        }
    }

    private static int FallbackPhysicalCoreCount()
    {
        int logical = Environment.ProcessorCount;
        return Math.Max(1, logical / 2);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLogicalProcessorInformationEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    public void Dispose()
    {
        _pdhQuery?.Dispose();
        _pdhQuery = null;
        _perCoreCounter = null;
        _perCoreClockCounter = null;
        _performancePercentCounter = null;
    }

    /// <summary>
    /// PDH の Processor Information instance name ("0,0" "0,1" ... "1,0" ...) を、カンマ区切りの各要素を
    /// 数値としてソートする Comparer。論理コアの並び順を正しくするために文字列ソートは使わない。
    /// </summary>
    private sealed class PdhCoreInstanceComparer : IComparer<PdhCounterItem>
    {
        public static readonly PdhCoreInstanceComparer Instance = new();

        public int Compare(PdhCounterItem x, PdhCounterItem y)
        {
            int[] xParts = ParseParts(x.InstanceName);
            int[] yParts = ParseParts(y.InstanceName);

            int length = Math.Max(xParts.Length, yParts.Length);
            for (int i = 0; i < length; i++)
            {
                int xValue = i < xParts.Length ? xParts[i] : 0;
                int yValue = i < yParts.Length ? yParts[i] : 0;
                int cmp = xValue.CompareTo(yValue);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return 0;
        }

        private static int[] ParseParts(string instanceName)
        {
            string[] parts = instanceName.Split(',');
            var result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = int.TryParse(parts[i], out int value) ? value : 0;
            }

            return result;
        }
    }
}
