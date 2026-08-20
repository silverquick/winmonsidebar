using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>
/// 全プロセスを CPU 使用率降順でサンプリングする。サイドバー UI は上位数件しか表示しないが、
/// 正しい順位を出すために毎回全プロセスを走査する。2 秒間隔（MetricsHub の SlowInterval）で
/// バックグラウンドスレッドから呼ばれる想定。
/// </summary>
public sealed partial class ProcessProvider : IMetricProvider<ProcessSnapshot>
{
    // サイドバーは狭いので上位数件しか表示しないが、極端に多いプロセス数（数百）の環境で
    // 毎フレームの整形コストが膨らまないよう、上限を超えたら CPU 降順の上位のみ返す。
    private const int MaxProcessesReturned = 300;
    private const int TopNWhenExceeded = 100;
    private const int ImagePathBufferLength = 1024;

    private const string GpuEngineUtilizationCounterPath = @"\GPU Engine(*)\Utilization Percentage";

    /// <summary>
    /// 直近サンプル時点の値。CreationTime100ns は PID 再利用を検出するための識別子として保持する
    /// （PID が一致していても生成時刻が異なれば別プロセスとみなし、差分計算を行わず 0 を返す）。
    /// </summary>
    private readonly record struct PrevSample(ulong CreationTime100ns, ulong CpuTime100ns, ulong IoBytes);

    private Dictionary<int, PrevSample> _prevSamples = new();
    private PdhQuery? _pdhQuery;
    private PdhMultiCounter? _gpuEngineCounter;
    private bool _disposed;

    public string Name => "Process";

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        try
        {
            // Process.GetProcesses() は標準ライブラリのみで完結し、失敗しても例外にはならないため
            // このプロバイダ自体は常に利用可能とする。GPU 使用率だけは PDH が使えない環境で 0 になる。
            _pdhQuery = PdhQuery.TryCreate();
            _gpuEngineCounter = _pdhQuery?.AddMultiCounter(GpuEngineUtilizationCounterPath);

            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public ProcessSnapshot Sample(TimeSpan elapsed)
    {
        if (!IsAvailable)
        {
            return ProcessSnapshot.Empty;
        }

        try
        {
            return SampleCore(elapsed);
        }
        catch
        {
            return ProcessSnapshot.Empty;
        }
    }

    private ProcessSnapshot SampleCore(TimeSpan elapsed)
    {
        double elapsedSeconds = elapsed.TotalSeconds > 0 ? elapsed.TotalSeconds : 0.0;
        int processorCount = Math.Max(1, Environment.ProcessorCount);

        IReadOnlyDictionary<int, double> gpuByPid = SampleGpuByPid();

        // 前回値は「今回生きているプロセスの分」だけを新しい辞書に積み直す。
        // こうすることで終了済みプロセスのエントリは自然に削除され、辞書のリークを防げる。
        var nextPrevSamples = new Dictionary<int, PrevSample>();
        var results = new List<ProcessInfo>();

        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    ProcessInfo? info = BuildProcessInfo(process, elapsedSeconds, processorCount, gpuByPid, nextPrevSamples);
                    if (info is not null)
                    {
                        results.Add(info);
                    }
                }
                catch
                {
                    // 1プロセス分の取得失敗は無視し、他のプロセスのサンプリングを継続する。
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        _prevSamples = nextPrevSamples;

        results.Sort((a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
        if (results.Count > MaxProcessesReturned)
        {
            results = results.GetRange(0, TopNWhenExceeded);
        }

        return new ProcessSnapshot(results);
    }

    private ProcessInfo? BuildProcessInfo(
        Process process,
        double elapsedSeconds,
        int processorCount,
        IReadOnlyDictionary<int, double> gpuByPid,
        Dictionary<int, PrevSample> nextPrevSamples)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch
        {
            // PID すら取れない場合は何も作れないので諦める。
            return null;
        }

        string name;
        try
        {
            name = process.ProcessName;
        }
        catch
        {
            name = string.Empty;
        }

        ulong workingSet = 0;
        try
        {
            workingSet = (ulong)Math.Max(0, process.WorkingSet64);
        }
        catch
        {
            // 保護されたプロセスなどでは取得できないことがあるが、0 のまま続行する。
        }

        double cpuPercent = 0.0;
        double diskBytesPerSec = 0.0;
        string? executablePath = null;

        // PROCESS_QUERY_LIMITED_INFORMATION は保護プロセス(System, Registry, csrss 等)に対しては
        // それでも失敗しうる。その場合は名前・PID・ワーキングセットだけの ProcessInfo になる。
        using SafeProcessHandle handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (!handle.IsInvalid)
        {
            ulong? creationTime100ns = null;
            ulong? cpuTime100ns = null;
            try
            {
                if (Kernel32.GetProcessTimes(handle, out FILETIME creation, out _, out FILETIME kernel, out FILETIME user))
                {
                    creationTime100ns = creation.ToUInt64();
                    cpuTime100ns = kernel.ToUInt64() + user.ToUInt64();
                }
            }
            catch
            {
                // 取得できなければ CPU% は 0 のまま。
            }

            ulong? ioBytes = null;
            try
            {
                if (Kernel32.GetProcessIoCounters(handle, out IO_COUNTERS io))
                {
                    ioBytes = io.ReadTransferCount + io.WriteTransferCount;
                }
            }
            catch
            {
                // 取得できなければディスク I/O は 0 のまま。
            }

            if (creationTime100ns is ulong creation100ns)
            {
                // PID は再利用されるため、生成時刻が前回と一致する場合のみ差分を採用する。
                // 一致しない（= 別プロセス）場合は前回値を使わず 0 を返す。
                if (_prevSamples.TryGetValue(pid, out PrevSample prev) && prev.CreationTime100ns == creation100ns)
                {
                    if (cpuTime100ns is ulong cpuNow && elapsedSeconds > 0)
                    {
                        ulong cpuDiff = cpuNow >= prev.CpuTime100ns ? cpuNow - prev.CpuTime100ns : 0;
                        double denom = elapsedSeconds * processorCount * 10_000_000.0;
                        cpuPercent = denom > 0 ? cpuDiff / denom * 100.0 : 0.0;
                        cpuPercent = Math.Clamp(cpuPercent, 0.0, 100.0);
                    }

                    if (ioBytes is ulong ioNow && elapsedSeconds > 0)
                    {
                        ulong ioDiff = ioNow >= prev.IoBytes ? ioNow - prev.IoBytes : 0;
                        diskBytesPerSec = ioDiff / elapsedSeconds;
                    }
                }

                nextPrevSamples[pid] = new PrevSample(creation100ns, cpuTime100ns ?? 0, ioBytes ?? 0);
            }

            executablePath = TryGetExecutablePath(handle);
        }

        double gpuPercent = gpuByPid.TryGetValue(pid, out double gpu) ? gpu : 0.0;

        return new ProcessInfo(pid, name, cpuPercent, workingSet, diskBytesPerSec, gpuPercent, executablePath);
    }

    private static unsafe string? TryGetExecutablePath(SafeProcessHandle handle)
    {
        try
        {
            Span<char> buffer = stackalloc char[ImagePathBufferLength];
            uint size = ImagePathBufferLength;
            fixed (char* p = buffer)
            {
                if (Kernel32.QueryFullProcessImageNameW(handle, 0, p, ref size) && size > 0)
                {
                    return new string(buffer[..(int)size]);
                }
            }

            // Idle/System などハンドルは開けても画像パスを持たないプロセスは常にここで失敗する。
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// PDH の "\GPU Engine(*)\Utilization Percentage" を集計し、PID ごとの GPU 使用率を算出する。
    /// インスタンス名には "pid_1234_...engtype_3D" のような形式で PID とエンジン種別が含まれる。
    /// 同一 PID・同一エンジン種別の値は合算し（複数アダプタ分など）、最終的には
    /// エンジン種別ごとの合計のうち最大値をそのプロセスの GPU% とする。
    /// </summary>
    private Dictionary<int, double> SampleGpuByPid()
    {
        var result = new Dictionary<int, double>();

        if (_pdhQuery is null || _gpuEngineCounter is null)
        {
            return result;
        }

        try
        {
            if (!_pdhQuery.Collect())
            {
                return result;
            }

            IReadOnlyList<PdhCounterItem> items = _gpuEngineCounter.GetValues();
            if (items.Count == 0)
            {
                return result;
            }

            var perPidEngineType = new Dictionary<int, Dictionary<string, double>>();
            foreach (PdhCounterItem item in items)
            {
                Match pidMatch = PidRegex().Match(item.InstanceName);
                if (!pidMatch.Success || !int.TryParse(pidMatch.Groups[1].Value, out int pid))
                {
                    continue;
                }

                Match engineMatch = EngTypeRegex().Match(item.InstanceName);
                string engineType = engineMatch.Success ? engineMatch.Groups[1].Value : item.InstanceName;

                if (!perPidEngineType.TryGetValue(pid, out Dictionary<string, double>? engineMap))
                {
                    engineMap = new Dictionary<string, double>();
                    perPidEngineType[pid] = engineMap;
                }

                engineMap[engineType] = engineMap.GetValueOrDefault(engineType) + item.Value;
            }

            foreach ((int pid, Dictionary<string, double> engineMap) in perPidEngineType)
            {
                double max = 0.0;
                foreach (double value in engineMap.Values)
                {
                    if (value > max)
                    {
                        max = value;
                    }
                }

                result[pid] = Math.Clamp(max, 0.0, 100.0);
            }

            return result;
        }
        catch
        {
            return new Dictionary<int, double>();
        }
    }

    [GeneratedRegex(@"pid_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PidRegex();

    [GeneratedRegex(@"engtype_(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex EngTypeRegex();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pdhQuery?.Dispose();
    }
}
