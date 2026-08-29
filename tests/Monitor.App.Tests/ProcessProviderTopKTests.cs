using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core.Models;
using Monitor.Windows.Providers;

namespace Monitor.App.Tests;

[TestClass]
public sealed class ProcessProviderTopKTests
{
    [TestMethod]
    public void SelectTopCandidates_WhenNIsGreaterThanK_ReturnsTopKInCorrectOrder()
    {
        // 10 個の候補に対して K = 4 で選抜する。
        var candidates = new[]
        {
            new ProcessProvider.ProcessCandidate(0, 100, 5.0),
            new ProcessProvider.ProcessCandidate(1, 101, 25.0),
            new ProcessProvider.ProcessCandidate(2, 102, 80.0),
            new ProcessProvider.ProcessCandidate(3, 103, 0.0),
            new ProcessProvider.ProcessCandidate(4, 104, 15.0),
            new ProcessProvider.ProcessCandidate(5, 105, 45.0),
            new ProcessProvider.ProcessCandidate(6, 106, 12.0),
            new ProcessProvider.ProcessCandidate(7, 107, 70.0),
            new ProcessProvider.ProcessCandidate(8, 108, 30.0),
            new ProcessProvider.ProcessCandidate(9, 109, 1.0),
        };

        ProcessProvider.ProcessCandidate[] top = ProcessProvider.SelectTopCandidates(candidates, topCount: 4);

        Assert.AreEqual(4, top.Length);
        Assert.AreEqual(102, top[0].Pid);
        Assert.AreEqual(80.0, top[0].CpuPercent, 0.0001);

        Assert.AreEqual(107, top[1].Pid);
        Assert.AreEqual(70.0, top[1].CpuPercent, 0.0001);

        Assert.AreEqual(105, top[2].Pid);
        Assert.AreEqual(45.0, top[2].CpuPercent, 0.0001);

        Assert.AreEqual(108, top[3].Pid);
        Assert.AreEqual(30.0, top[3].CpuPercent, 0.0001);
    }

    [TestMethod]
    public void SelectTopCandidates_WhenCpuPercentTied_StableTieBreakByPidAscending()
    {
        // 同一 CPU% の候補群に対して、PID 昇順で安定して選抜・ソートされることを検証する。
        var tiedCandidates = new[]
        {
            new ProcessProvider.ProcessCandidate(0, 300, 50.0),
            new ProcessProvider.ProcessCandidate(1, 100, 50.0),
            new ProcessProvider.ProcessCandidate(2, 200, 50.0),
            new ProcessProvider.ProcessCandidate(3, 400, 50.0),
            new ProcessProvider.ProcessCandidate(4, 50, 50.0),
        };

        ProcessProvider.ProcessCandidate[] top = ProcessProvider.SelectTopCandidates(tiedCandidates, topCount: 3);

        Assert.AreEqual(3, top.Length);
        Assert.AreEqual(50, top[0].Pid);
        Assert.AreEqual(50.0, top[0].CpuPercent, 0.0001);

        Assert.AreEqual(100, top[1].Pid);
        Assert.AreEqual(50.0, top[1].CpuPercent, 0.0001);

        Assert.AreEqual(200, top[2].Pid);
        Assert.AreEqual(50.0, top[2].CpuPercent, 0.0001);

        // CPU% 違いと同率が混在するケース
        var mixedCandidates = new[]
        {
            new ProcessProvider.ProcessCandidate(0, 900, 90.0),
            new ProcessProvider.ProcessCandidate(1, 250, 60.0),
            new ProcessProvider.ProcessCandidate(2, 150, 60.0),
            new ProcessProvider.ProcessCandidate(3, 350, 60.0),
            new ProcessProvider.ProcessCandidate(4, 500, 10.0),
        };

        ProcessProvider.ProcessCandidate[] mixedTop = ProcessProvider.SelectTopCandidates(mixedCandidates, topCount: 3);
        Assert.AreEqual(3, mixedTop.Length);
        Assert.AreEqual(900, mixedTop[0].Pid);
        Assert.AreEqual(150, mixedTop[1].Pid);
        Assert.AreEqual(250, mixedTop[2].Pid);
    }

    [TestMethod]
    public void SelectTopCandidates_WhenKIs1_ReturnsSingleBestCandidate()
    {
        var candidates = new[]
        {
            new ProcessProvider.ProcessCandidate(0, 200, 20.0),
            new ProcessProvider.ProcessCandidate(1, 100, 20.0),
            new ProcessProvider.ProcessCandidate(2, 300, 15.0),
        };

        ProcessProvider.ProcessCandidate[] top = ProcessProvider.SelectTopCandidates(candidates, topCount: 1);

        Assert.AreEqual(1, top.Length);
        Assert.AreEqual(100, top[0].Pid);
        Assert.AreEqual(20.0, top[0].CpuPercent, 0.0001);
    }

    [TestMethod]
    public void CalculateCpuPercent_PidReuse_DoesNotCalculateDiffAndReturnsZero()
    {
        var prevSamples = new Dictionary<int, ProcessProvider.PrevSample>
        {
            [100] = new ProcessProvider.PrevSample(CreationTime100ns: 1000, CpuTime100ns: 500),
        };

        // PID は 100 で同一だが、CreationTime が異なる（PID が再利用された別プロセス）
        double cpuPercent = ProcessProvider.CalculateCpuPercent(
            creation100ns: 2000,
            cpuTime100ns: 600,
            pid: 100,
            prevSamples: prevSamples,
            elapsedSeconds: 1.0,
            processorCount: 4);

        Assert.AreEqual(0.0, cpuPercent, 0.0001, "PID再利用時は前回値との差分計算を行わず 0% を返すべき。");
    }

    [TestMethod]
    public void CalculateCpuPercent_ValidPrevSample_CalculatesCorrectCpuDiffAndClamps()
    {
        var prevSamples = new Dictionary<int, ProcessProvider.PrevSample>
        {
            [100] = new ProcessProvider.PrevSample(CreationTime100ns: 1000, CpuTime100ns: 10_000_000),
        };

        // 1秒経過、2論理コア、CPU時間差分 10_000_000 100ns（= 1秒）→ 全体使用率 50.0%
        double cpuPercent = ProcessProvider.CalculateCpuPercent(
            creation100ns: 1000,
            cpuTime100ns: 20_000_000,
            pid: 100,
            prevSamples: prevSamples,
            elapsedSeconds: 1.0,
            processorCount: 2);

        Assert.AreEqual(50.0, cpuPercent, 0.0001);

        // 異常に大きい差分値でも 100.0% に clamp されること
        double clampedPercent = ProcessProvider.CalculateCpuPercent(
            creation100ns: 1000,
            cpuTime100ns: 100_000_000,
            pid: 100,
            prevSamples: prevSamples,
            elapsedSeconds: 1.0,
            processorCount: 2);

        Assert.AreEqual(100.0, clampedPercent, 0.0001);
    }

    [TestMethod]
    public void ProcessProvider_Constructor_RespectsTopCountBounds()
    {
        var providerDefault = new ProcessProvider();
        Assert.AreEqual(8, providerDefault.TopProcessCount);

        var providerCustom = new ProcessProvider(topProcessCount: 5);
        Assert.AreEqual(5, providerCustom.TopProcessCount);

        var providerZero = new ProcessProvider(topProcessCount: 0);
        Assert.AreEqual(1, providerZero.TopProcessCount, "K < 1 は 1 にクランプされるべき。");

        var providerNegative = new ProcessProvider(topProcessCount: -5);
        Assert.AreEqual(1, providerNegative.TopProcessCount);
    }

    [TestMethod]
    public void ProcessProvider_LiveSampling_MaintainsAllPrevSamplesWhileReturningTopK()
    {
        var provider = new ProcessProvider(topProcessCount: 5);
        provider.Initialize();
        Assert.IsTrue(provider.IsAvailable);

        // 1回目サンプル（基準値収集）
        ProcessSnapshot snapshot1 = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(snapshot1.Processes);
        Assert.IsTrue(snapshot1.Processes.Count <= 5);

        // PrevSamples は全プロセスの情報が格納されている（通常 100 件以上）
        Assert.IsTrue(provider.PrevSamples.Count > snapshot1.Processes.Count,
            $"全プロセスの PrevSample が更新されていること: PrevSamples={provider.PrevSamples.Count}, Returned={snapshot1.Processes.Count}");

        // 2回目サンプル（差分計算）
        ProcessSnapshot snapshot2 = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(snapshot2.Processes);
        Assert.IsTrue(snapshot2.Processes.Count <= 5);

        // 返却されたリストが CPU% 降順（同率は PID 昇順）であることを確認
        for (int i = 0; i < snapshot2.Processes.Count - 1; i++)
        {
            ProcessInfo current = snapshot2.Processes[i];
            ProcessInfo next = snapshot2.Processes[i + 1];

            if (current.CpuPercent == next.CpuPercent)
            {
                Assert.IsTrue(current.Pid <= next.Pid,
                    $"同率 CPU% では PID 昇順であること: current PID={current.Pid}, next PID={next.Pid}");
            }
            else
            {
                Assert.IsTrue(current.CpuPercent >= next.CpuPercent,
                    $"CPU% 降順であること: current CPU%={current.CpuPercent}, next CPU%={next.CpuPercent}");
            }
        }
    }
}
