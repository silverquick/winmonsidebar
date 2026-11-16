using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;

namespace Monitor.App.Tests;

/// <summary>
/// B-01 / B-02: サンプリングレーンが例外で止まらないこと、失敗が連打されずにログへ残ることの検証。
/// </summary>
[TestClass]
public sealed class MetricsHubResilienceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>fast レーンだけを短い間隔で回し、他レーンは事実上動かさない。</summary>
    private static MetricsHubOptions FastOnlyOptions(ConcurrentQueue<(string Message, Exception? Exception)> logs) => new()
    {
        FastInterval = TimeSpan.FromMilliseconds(5),
        ProcessInterval = TimeSpan.FromHours(1),
        ThermalInterval = TimeSpan.FromHours(1),
        VolumeInterval = TimeSpan.FromHours(1),
        Log = (message, exception) => logs.Enqueue((message, exception)),
    };

    [TestMethod]
    public async Task ProviderThrows_PublishesFallback_AndLogsOnlyOnFailureStartAndRecovery()
    {
        var logs = new ConcurrentQueue<(string Message, Exception? Exception)>();
        var cpu = new ScriptedCpuProvider(failingSamples: 3, signalAfterSamples: 6);
        var published = new ConcurrentQueue<CpuSnapshot>();

        var hub = MetricsHub.CreateForTest(FastOnlyOptions(logs), cpu: cpu);
        hub.SnapshotAvailable += snapshot => published.Enqueue(snapshot.Cpu);
        hub.Start();
        try
        {
            Assert.IsTrue(cpu.Reached.Wait(WaitTimeout), "CPU provider should be sampled repeatedly.");
        }
        finally
        {
            await hub.DisposeAsync();
        }

        // fast レーンは唯一の公開元なので、公開順 = サンプリング順。最初の 3 回はフォールバック値。
        CpuSnapshot[] cpus = published.ToArray();
        Assert.IsTrue(cpus.Length >= 6, $"Expected at least 6 snapshots, got {cpus.Length}.");
        for (int i = 0; i < 3; i++)
        {
            Assert.AreSame(CpuSnapshot.Empty, cpus[i], $"Snapshot #{i} should fall back to CpuSnapshot.Empty.");
        }

        Assert.AreSame(cpu.Value, cpus[3], "After recovery the provider value should be published.");

        var cpuLogs = logs.Where(l => l.Message.Contains(nameof(ScriptedCpuProvider))).ToArray();
        Assert.AreEqual(2, cpuLogs.Length, "Expected exactly one failure-start log and one recovery log: " + string.Join(" | ", cpuLogs.Select(l => l.Message)));
        Assert.IsInstanceOfType<InvalidOperationException>(cpuLogs[0].Exception);
        Assert.IsNull(cpuLogs[1].Exception);
        StringAssert.Contains(cpuLogs[1].Message, "連続失敗 3 回");
    }

    [TestMethod]
    public async Task LaneBodyThrows_LaneContinues_AndLogsOnce()
    {
        var logs = new ConcurrentQueue<(string Message, Exception? Exception)>();
        var hub = MetricsHub.CreateForTest(FastOnlyOptions(logs));
        int fastIterations = 0;
        using var reached = new ManualResetEventSlim();

        hub.LaneIterationHookForTest = lane =>
        {
            if (lane != "fast")
            {
                return;
            }

            int n = Interlocked.Increment(ref fastIterations);
            if (n == 6)
            {
                reached.Set();
            }

            if (n is 2 or 3)
            {
                throw new InvalidOperationException("lane body failure");
            }
        };

        int publishedCount = 0;
        hub.SnapshotAvailable += _ => Interlocked.Increment(ref publishedCount);

        hub.Start();
        try
        {
            Assert.IsTrue(reached.Wait(WaitTimeout), "Fast lane should keep iterating after exceptions.");
        }
        finally
        {
            await hub.DisposeAsync();
        }

        // 1・4・5 周目は例外なしで Publish まで到達している（6 周目は Dispose と競合し得るので数えない）。
        Assert.IsTrue(Volatile.Read(ref publishedCount) >= 3, "Iterations without exceptions should still publish.");

        var laneLogs = logs.Where(l => l.Message.Contains("fast レーン")).ToArray();
        Assert.AreEqual(2, laneLogs.Length, string.Join(" | ", laneLogs.Select(l => l.Message)));
        Assert.IsInstanceOfType<InvalidOperationException>(laneLogs[0].Exception);
        StringAssert.Contains(laneLogs[1].Message, "連続失敗 2 回");
    }

    [TestMethod]
    public async Task DiskAlertTrackerThrows_PublishesRawDisk()
    {
        var logs = new ConcurrentQueue<(string Message, Exception? Exception)>();

        // Devices が null だと DiskBusyAlertTracker.Update が NullReferenceException を投げる。
        var brokenDisk = new DiskSnapshot { Devices = null! };
        var disk = new FixedProvider<DiskSnapshot>(brokenDisk);
        using var published = new ManualResetEventSlim();
        DiskSnapshot? publishedDisk = null;

        var hub = MetricsHub.CreateForTest(FastOnlyOptions(logs), disk: disk);
        hub.SnapshotAvailable += snapshot =>
        {
            publishedDisk ??= snapshot.Disk;
            published.Set();
        };

        hub.Start();
        try
        {
            Assert.IsTrue(published.Wait(WaitTimeout), "Fast lane should publish even if the disk alert tracker throws.");
        }
        finally
        {
            await hub.DisposeAsync();
        }

        Assert.AreSame(brokenDisk, publishedDisk, "Raw disk snapshot should be published as a fallback.");
        Assert.AreEqual(1, logs.Count(l => l.Message.Contains("DiskBusyAlertTracker")), "Failure should be logged only once.");
        Assert.AreEqual(0, logs.Count(l => l.Message.Contains("fast レーン")), "Lane-level failure should not occur.");
    }

    [TestMethod]
    public void SubscriberThrows_LogsOnlyOnFailureStartAndRecovery()
    {
        var logs = new List<(string Message, Exception? Exception)>();
        var hub = MetricsHub.CreateForTest(new MetricsHubOptions { Log = (m, e) => logs.Add((m, e)) });
        int calls = 0;
        hub.SnapshotAvailable += _ =>
        {
            if (++calls <= 3)
            {
                throw new InvalidOperationException("subscriber failure");
            }
        };

        for (int i = 0; i < 6; i++)
        {
            hub.PublishSnapshotForTest(CreateEmptySnapshot());
        }

        Assert.AreEqual(6, calls);
        Assert.AreEqual(2, logs.Count, string.Join(" | ", logs.Select(l => l.Message)));
        Assert.IsInstanceOfType<InvalidOperationException>(logs[0].Exception);
        StringAssert.Contains(logs[1].Message, "連続失敗 3 回");
    }

    [TestMethod]
    public void LogSinkThrows_DoesNotBreakPublishing()
    {
        var hub = MetricsHub.CreateForTest(new MetricsHubOptions { Log = (_, _) => throw new InvalidOperationException("sink failure") });
        hub.SnapshotAvailable += _ => throw new InvalidOperationException("subscriber failure");

        MetricsSnapshot snapshot = CreateEmptySnapshot();
        hub.PublishSnapshotForTest(snapshot);

        Assert.IsNotNull(hub.Latest);
    }

    [TestMethod]
    public async Task InitializeThrows_LogsOnceWithProviderTypeName()
    {
        var logs = new ConcurrentQueue<(string Message, Exception? Exception)>();
        var cpu = new ThrowingInitializeCpuProvider();
        using var published = new ManualResetEventSlim();

        var hub = MetricsHub.CreateForTest(FastOnlyOptions(logs), cpu: cpu);
        hub.SnapshotAvailable += _ => published.Set();
        hub.Start();
        try
        {
            Assert.IsTrue(published.Wait(WaitTimeout), "Sampling should continue after an initialize failure.");
        }
        finally
        {
            await hub.DisposeAsync();
        }

        var initLogs = logs.Where(l => l.Message.Contains(nameof(ThrowingInitializeCpuProvider))).ToArray();
        Assert.AreEqual(1, initLogs.Length);
        StringAssert.Contains(initLogs[0].Message, "初期化");
        Assert.IsInstanceOfType<InvalidOperationException>(initLogs[0].Exception);
    }

    [TestMethod]
    [DataRow("Initialize")]
    [DataRow("Sample")]
    [DataRow("Dispose")]
    public async Task DisposeAsync_BlockedOperation_RespectsDeadlineAndDefersExactlyOnce(string operation)
    {
        var cpu = new BarrierCpuProvider(operation);
        var memory = new InitializationCountingMemoryProvider();
        var hub = MetricsHub.CreateForTest(new MetricsHubOptions
        {
            FastInterval = TimeSpan.FromMilliseconds(5),
            ProcessInterval = TimeSpan.FromHours(1),
            ThermalInterval = TimeSpan.FromHours(1),
            VolumeInterval = TimeSpan.FromHours(1),
            DisposeTimeout = TimeSpan.FromMilliseconds(150),
        }, cpu: cpu, memory: memory);
        hub.Start();
        try
        {
            if (operation != "Dispose") await cpu.Entered.Task.WaitAsync(WaitTimeout);
            var sw = Stopwatch.StartNew();
            Task stopping = hub.DisposeAsync().AsTask();
            if (operation == "Dispose") await cpu.Entered.Task.WaitAsync(WaitTimeout);
            await stopping.WaitAsync(TimeSpan.FromMilliseconds(400));
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromMilliseconds(400), $"Elapsed: {sw.Elapsed}");
            Assert.AreEqual(operation == "Dispose" ? 1 : 0, cpu.DisposeCount);
            Assert.IsFalse(cpu.Overlapped);
            // Repeated callers share the same owner, including after the timeout.
            await hub.DisposeAsync();
        }
        finally { cpu.Release.Set(); }
        await cpu.Disposed.Task.WaitAsync(WaitTimeout);
        Assert.AreEqual(1, cpu.DisposeCount);
        Assert.IsFalse(cpu.Overlapped, "Disposal must follow Initialize/Sample exit.");
        if (operation == "Initialize") Assert.AreEqual(0, memory.Initializations, "Cancellation skips later initializers.");
        await hub.DisposeAsync();
        Assert.AreEqual(1, cpu.DisposeCount);
    }

    private static MetricsSnapshot CreateEmptySnapshot() => new(
        Timestamp: DateTimeOffset.UtcNow,
        Cpu: CpuSnapshot.Empty,
        Memory: MemorySnapshot.Empty,
        Disk: DiskSnapshot.Empty,
        Network: NetworkSnapshot.Empty,
        Gpu: GpuSnapshot.Empty,
        Processes: ProcessSnapshot.Empty,
        Thermal: ThermalSnapshot.Empty,
        Volumes: Array.Empty<VolumeSnapshot>());

    /// <summary>最初の N 回だけ例外を投げ、以降は固定値を返す CPU プロバイダ。</summary>
    private sealed class ScriptedCpuProvider(int failingSamples, int signalAfterSamples) : IMetricProvider<CpuSnapshot>
    {
        private int _samples;

        public CpuSnapshot Value { get; } = new() { TotalUsagePercent = 42.0 };
        public ManualResetEventSlim Reached { get; } = new();
        public string Name => "ScriptedCpu";
        public bool IsAvailable => true;
        public void Initialize() { }

        public CpuSnapshot Sample(TimeSpan elapsed)
        {
            int n = Interlocked.Increment(ref _samples);
            if (n == signalAfterSamples)
            {
                Reached.Set();
            }

            if (n <= failingSamples)
            {
                throw new InvalidOperationException("sample failure");
            }

            return Value;
        }

        public void Dispose() { }
    }

    private sealed class ThrowingInitializeCpuProvider : IMetricProvider<CpuSnapshot>
    {
        public string Name => "ThrowingInitializeCpu";
        public bool IsAvailable => true;
        public void Initialize() => throw new InvalidOperationException("initialize failure");
        public CpuSnapshot Sample(TimeSpan elapsed) => CpuSnapshot.Empty;
        public void Dispose() { }
    }

    private sealed class InitializationCountingMemoryProvider : IMetricProvider<MemorySnapshot>
    {
        public int Initializations;
        public string Name => "Memory";
        public bool IsAvailable => true;
        public void Initialize() => Interlocked.Increment(ref Initializations);
        public MemorySnapshot Sample(TimeSpan elapsed) => MemorySnapshot.Empty;
        public void Dispose() { }
    }

    private sealed class BarrierCpuProvider(string operation) : IMetricProvider<CpuSnapshot>
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        private int _active;
        public int DisposeCount;
        public bool Overlapped;
        public string Name => "BarrierCpu";
        public bool IsAvailable => true;
        private void Block(string phase)
        {
            Interlocked.Increment(ref _active);
            try
            {
                if (phase == operation)
                {
                    Entered.TrySetResult();
                    Release.Wait();
                }
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Initialize() => Block("Initialize");
        public CpuSnapshot Sample(TimeSpan elapsed) { Block("Sample"); return CpuSnapshot.Empty; }
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Overlapped |= Volatile.Read(ref _active) != 0;
            if (operation == "Dispose") { Entered.TrySetResult(); Release.Wait(); }
            Disposed.TrySetResult();
        }
    }

    private sealed class FixedProvider<T>(T value) : IMetricProvider<T>
    {
        public string Name => "Fixed";
        public bool IsAvailable => true;
        public void Initialize() { }
        public T Sample(TimeSpan elapsed) => value;
        public void Dispose() { }
    }
}
