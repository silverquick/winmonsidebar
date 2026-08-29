using System.Diagnostics;
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
    /// <summary>
    /// 直近サンプル時点の値。CreationTime100ns は PID 再利用を検出するための識別子として保持する
    /// （PID が一致していても生成時刻が異なれば別プロセスとみなし、差分計算を行わず 0 を返す）。
    /// </summary>
    private readonly record struct PrevSample(ulong CreationTime100ns, ulong CpuTime100ns);

    private Dictionary<int, PrevSample> _prevSamples = new();
    private bool _disposed;

    public string Name => "Process";

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        try
        {
            // Process.GetProcesses() は標準ライブラリのみで完結し、失敗しても例外にはならないため、
            // このプロバイダ自体は常に利用可能とする。
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
                    ProcessInfo? info = BuildProcessInfo(process, elapsedSeconds, processorCount, nextPrevSamples);
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

                }

                nextPrevSamples[pid] = new PrevSample(creation100ns, cpuTime100ns ?? 0);
            }
        }

        return new ProcessInfo(pid, name, cpuPercent, workingSet);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
