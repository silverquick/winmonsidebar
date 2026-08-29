using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>
/// 全プロセスをサンプリングし、CPU 使用率上位 K 件のみをスナップショットとして生成・返却する。
/// サイドバー UI は上位数件しか表示しないため、全プロセスの ProcessInfo 実体化や全ソートは行わず、
/// bounded min-heap により上位 K 件のみを選抜する。
/// ただし、正確な順位算出および PID 再利用検出のために、全プロセスの PID / 生成時刻 / CPU 時間は取得し、
/// 前回収集情報（PrevSamples）は全件更新する。
/// 2 秒間隔（MetricsHub の SlowInterval）でバックグラウンドスレッドから呼ばれる想定。
/// </summary>
public sealed partial class ProcessProvider : IMetricProvider<ProcessSnapshot>
{
    private readonly int _topProcessCount;

    /// <summary>
    /// 直近サンプル時点の値。CreationTime100ns は PID 再利用を検出するための識別子として保持する
    /// （PID が一致していても生成時刻が異なれば別プロセスとみなし、差分計算を行わず 0 を返す）。
    /// </summary>
    internal readonly record struct PrevSample(ulong CreationTime100ns, ulong CpuTime100ns);

    /// <summary>
    /// 順位選抜用の軽量な値型候補。ProcessInfo オブジェクトやプロセス名文字列の割り当てを抑える。
    /// </summary>
    internal readonly record struct ProcessCandidate(int Index, int Pid, double CpuPercent);

    /// <summary>
    /// Bounded min-heap 用の比較器。
    /// min-heap の根（Peek）には「現在の上位 K 件の中で最も優先度の低い（脱落候補の）要素」が来るようにする。
    /// 優先度: CPU 使用率が高い順 ＞ PID が小さい順（同率時の tie-break）。
    /// したがって、CPU% が低いもの、または同率で PID が大きいものを「小さい（根に近い）」と判定する。
    /// </summary>
    internal sealed class CandidateMinHeapComparer : IComparer<ProcessCandidate>
    {
        public static CandidateMinHeapComparer Instance { get; } = new();

        public int Compare(ProcessCandidate x, ProcessCandidate y)
        {
            int cpuCompare = x.CpuPercent.CompareTo(y.CpuPercent);
            if (cpuCompare != 0)
            {
                return cpuCompare;
            }

            // 同率時は PID 昇順が優先（優先度が高い）。
            // min-heap では優先度の低い（PID の大きい）要素を根にするため、y と x を逆順で比較する。
            return y.Pid.CompareTo(x.Pid);
        }
    }

    private Dictionary<int, PrevSample> _prevSamples = new();
    private bool _disposed;

    public ProcessProvider(int topProcessCount = 8)
    {
        _topProcessCount = Math.Max(1, topProcessCount);
    }

    public int TopProcessCount => _topProcessCount;

    internal IReadOnlyDictionary<int, PrevSample> PrevSamples => _prevSamples;

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

        Process[] processes = Process.GetProcesses();

        // 前回値は「今回生きているプロセスの分」だけを新しい辞書に積み直す。
        // こうすることで終了済みプロセスのエントリは自然に削除され、辞書のリークを防げる。
        var nextPrevSamples = new Dictionary<int, PrevSample>(processes.Length);

        try
        {
            var heap = new PriorityQueue<ProcessCandidate, ProcessCandidate>(_topProcessCount, CandidateMinHeapComparer.Instance);

            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                int pid;
                try
                {
                    pid = process.Id;
                }
                catch
                {
                    // PID すら取れない場合は何も作れないので諦める。
                    continue;
                }

                double cpuPercent = 0.0;
                // PROCESS_QUERY_LIMITED_INFORMATION は保護プロセス(System, Registry, csrss 等)に対しては
                // それでも失敗しうる。その場合は CPU% は 0 のままになる。
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
                        cpuPercent = CalculateCpuPercent(
                            creation100ns,
                            cpuTime100ns,
                            pid,
                            _prevSamples,
                            elapsedSeconds,
                            processorCount);

                        nextPrevSamples[pid] = new PrevSample(creation100ns, cpuTime100ns ?? 0);
                    }
                }

                AddCandidate(heap, new ProcessCandidate(i, pid, cpuPercent), _topProcessCount);
            }

            _prevSamples = nextPrevSamples;

            ProcessCandidate[] topCandidates = DrainTopCandidates(heap);

            // 選抜された上位 K 件についてのみ、対象 Process の破棄前に名前とワーキングセットを取得して ProcessInfo を実体化する。
            var results = new List<ProcessInfo>(topCandidates.Length);
            foreach (ProcessCandidate candidate in topCandidates)
            {
                Process process = processes[candidate.Index];

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

                results.Add(new ProcessInfo(candidate.Pid, name, candidate.CpuPercent, workingSet));
            }

            return new ProcessSnapshot(results);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static double CalculateCpuPercent(
        ulong creation100ns,
        ulong? cpuTime100ns,
        int pid,
        IReadOnlyDictionary<int, PrevSample> prevSamples,
        double elapsedSeconds,
        int processorCount)
    {
        // PID は再利用されるため、生成時刻が前回と一致する場合のみ差分を採用する。
        // 一致しない（= 別プロセス）場合は前回値を使わず 0 を返す。
        if (prevSamples.TryGetValue(pid, out PrevSample prev) && prev.CreationTime100ns == creation100ns)
        {
            if (cpuTime100ns is ulong cpuNow && elapsedSeconds > 0)
            {
                ulong cpuDiff = cpuNow >= prev.CpuTime100ns ? cpuNow - prev.CpuTime100ns : 0;
                double denom = elapsedSeconds * processorCount * 10_000_000.0;
                double cpuPercent = denom > 0 ? cpuDiff / denom * 100.0 : 0.0;
                return Math.Clamp(cpuPercent, 0.0, 100.0);
            }
        }

        return 0.0;
    }

    internal static void AddCandidate(
        PriorityQueue<ProcessCandidate, ProcessCandidate> heap,
        ProcessCandidate candidate,
        int topCount)
    {
        if (heap.Count < topCount)
        {
            heap.Enqueue(candidate, candidate);
        }
        else if (CandidateMinHeapComparer.Instance.Compare(candidate, heap.Peek()) > 0)
        {
            heap.Dequeue();
            heap.Enqueue(candidate, candidate);
        }
    }

    internal static ProcessCandidate[] DrainTopCandidates(PriorityQueue<ProcessCandidate, ProcessCandidate> heap)
    {
        int count = heap.Count;
        var topCandidates = new ProcessCandidate[count];
        // min-heap から取り出すと小さい（優先度の低い）順に出てくるため、
        // 配列の後ろから詰めることで CPU% 降順（同率は PID 昇順）にする。
        for (int i = count - 1; i >= 0; i--)
        {
            topCandidates[i] = heap.Dequeue();
        }

        return topCandidates;
    }

    internal static ProcessCandidate[] SelectTopCandidates(IEnumerable<ProcessCandidate> candidates, int topCount)
    {
        int k = Math.Max(1, topCount);
        var heap = new PriorityQueue<ProcessCandidate, ProcessCandidate>(k, CandidateMinHeapComparer.Instance);

        foreach (ProcessCandidate candidate in candidates)
        {
            AddCandidate(heap, candidate, k);
        }

        return DrainTopCandidates(heap);
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
