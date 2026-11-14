using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Controls;
using Monitor.App.ViewModels;
using Monitor.Core;
using Monitor.Core.Abstractions;
using Monitor.Core.Alerts;
using Monitor.Core.Models;
using Monitor.Windows.Providers;

namespace Monitor.App.Tests;

[TestClass]
public sealed class ProviderAvailabilityTests
{
    [TestMethod]
    public async Task SignaledFailure_BeforeFirstSuccess_IndependentSource_AndSecondStreak()
    {
        var cpu = new SignalingCpuProvider([true, true, true, false, true, true, true, false]);
        var memory = new CountingMemoryProvider();
        var logs = new ConcurrentQueue<(string Message, Exception? Exception)>();
        var hub = new MetricsHub(FastOptions(logs), cpu, memory,
            new FixedProvider<DiskSnapshot>(DiskSnapshot.Empty),
            new FixedProvider<NetworkSnapshot>(NetworkSnapshot.Empty),
            new FixedProvider<GpuSnapshot>(GpuSnapshot.Empty),
            new FixedProvider<ProcessSnapshot>(ProcessSnapshot.Empty), new EmptyThermalProvider(),
            new FixedProvider<IReadOnlyList<VolumeSnapshot>>([]));
        var published = new ConcurrentQueue<MetricsSnapshot>();
        using var reached = new ManualResetEventSlim();
        hub.SnapshotAvailable += s => { published.Enqueue(s); if (published.Count >= 8) reached.Set(); };
        hub.Start();
        try { Assert.IsTrue(reached.Wait(TimeSpan.FromSeconds(10))); }
        finally { await hub.DisposeAsync(); }
        MetricsSnapshot[] samples = published.Take(8).ToArray();
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.AreEqual(i is 2 or 6 ? MetricSources.Cpu : MetricSources.None, samples[i].UnavailableSources, $"Sample {i}");
            Assert.AreEqual(i < 3 ? 0.0 : i < 7 ? 40.0 : 80.0, samples[i].Cpu.TotalUsagePercent);
            Assert.AreEqual((double)i + 1, samples[i].Memory.UsedPercent, "Healthy memory continues updating.");
        }
        var cpuLogs = logs.Where(l => l.Message.Contains(nameof(SignalingCpuProvider))).ToArray();
        Assert.AreEqual(4, cpuLogs.Length, "Each streak logs failure and recovery once.");
        Assert.IsTrue(cpuLogs.All(l => l.Exception is null), "Signaled failures need no exception allocation.");
        StringAssert.Contains(cpuLogs[0].Message, "取得失敗");
        StringAssert.Contains(cpuLogs[1].Message, "連続失敗 3 回");
    }

    [TestMethod]
    public void DiskBusy_OnlyFreshSamplesAdvanceTracker_RecoveryUsesActualGap()
    {
        var raw = new DiskSnapshot { Devices = [new() { PhysicalDriveNumber = 0, BusyPercent = 99 }] };
        var disk = new ControlledDiskProvider(raw);
        var hub = MetricsHub.CreateForTest(disk: disk);
        // Four fresh seconds: one more would raise Caution.
        DiskSnapshot held = raw;
        for (int i = 0; i < 4; i++) held = hub.SampleDisk(TimeSpan.FromSeconds(1));
        Assert.AreEqual(AlertLevel.None, held.Devices[0].BusyAlertLevel);
        disk.Fail = true;
        Assert.AreSame(held, hub.SampleDisk(TimeSpan.FromSeconds(1)));
        Assert.AreSame(held, hub.SampleDisk(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(AlertLevel.None, held.Devices[0].BusyAlertLevel, "Held samples must not raise a sustained-busy alert.");
        hub.SampleDisk(TimeSpan.FromSeconds(1));
        disk.Fail = false;
        Assert.AreEqual(AlertLevel.None, hub.SampleDisk(TimeSpan.FromSeconds(1)).Devices[0].BusyAlertLevel,
            "Four-second gap resets continuity rather than resurrecting prior busy time.");
        for (int i = 0; i < 5; i++) held = hub.SampleDisk(TimeSpan.FromSeconds(1));
        Assert.AreEqual(AlertLevel.Caution, held.Devices[0].BusyAlertLevel);
        disk.Fail = true;
        Assert.AreSame(held, hub.SampleDisk(TimeSpan.FromSeconds(1)), "Already decorated alerts remain unchanged while held.");
    }

    [TestMethod]
    public void Network_RetriesAfterNativeFalseAndException_AndRecoversWithoutRealNic()
    {
        int calls = 0;
        using var provider = new NetworkProvider(rows =>
        {
            rows.Clear();
            calls++;
            if (calls == 3) throw new InvalidOperationException("transient");
            return calls >= 4;
        });
        provider.Initialize(); // Native false is transient even during initialization.
        Assert.IsTrue(provider.IsAvailable);
        provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsTrue(provider.LastSampleFailed);
        Assert.IsTrue(provider.IsAvailable);
        provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsTrue(provider.LastSampleFailed);
        Assert.IsTrue(provider.IsAvailable);
        provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsFalse(provider.LastSampleFailed);
        Assert.AreEqual(4, calls);
        Assert.IsTrue(provider.IsAvailable, "An empty successful table means no connected NIC, not a failed sample.");
    }

    [TestMethod]
    public async Task Volume_FailedRefreshRemainsFailedAcrossSamplesUntilSuccessfulRefresh()
    {
        using var provider = new VolumeProvider { ReadDriveNamesForTest = () => throw new IOException("transient") };
        await provider.RefreshForTest();
        for (int i = 0; i < 4; i++)
        {
            provider.Sample(TimeSpan.FromSeconds(5));
            Assert.IsTrue(provider.LastSampleFailed);
        }
        provider.ReadDriveNamesForTest = () => [];
        await provider.RefreshForTest();
        provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsFalse(provider.LastSampleFailed);
    }

    [STATestMethod]
    public void Gauge_UnavailableSnapsFillAway_AndRecoverySnapsToFreshValue()
    {
        var gauge = new GaugeBar { Width = 100, Height = 5, Value = 90 };
        gauge.Measure(new Size(100, 5));
        gauge.Arrange(new Rect(0, 0, 100, 5));
        Size originalSize = gauge.RenderSize;
        FieldInfo Field(string name) => typeof(GaugeBar).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var clip = (RectangleGeometry)Field("_clipGeometry").GetValue(gauge)!;
        // Simulate a fill already in flight; no visible window or app is launched.
        Field("_current").SetValue(gauge, 75.0);
        Field("_isComplete").SetValue(gauge, false);
        gauge.Value = double.NaN;
        Assert.IsTrue(clip.Rect.IsEmpty);
        Assert.AreEqual(0.0, Field("_current").GetValue(gauge));
        Assert.IsTrue((bool)Field("_isComplete").GetValue(gauge)!);
        Assert.AreEqual(originalSize, gauge.RenderSize);
        gauge.Value = 62;
        Assert.AreEqual(62.0, Field("_current").GetValue(gauge));
        Assert.AreEqual(62.0, clip.Rect.Width);
        Assert.IsTrue((bool)Field("_isComplete").GetValue(gauge)!);
        Assert.AreEqual(originalSize, gauge.RenderSize);
    }

    [TestMethod]
    public void UnavailableRows_RepeatedUpdatesDoNotFormatHeldValuesOrAllocate()
    {
        var nic = new NetworkInterfaceSnapshot("Ethernet", "Test", 1_000_000, 123, 456, true);
        var process = new ProcessInfo(1, "test.exe", 12.3, 456);
        var networkRow = new NetworkInterfaceRowViewModel(nic, unavailable: true);
        var processRow = new ProcessRowViewModel(1);
        processRow.Update(process, unavailable: true);
        // Warm the update path before measuring only the held/unavailable updates.
        networkRow.Update(nic, unavailable: true);
        processRow.Update(process, unavailable: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            networkRow.Update(nic, unavailable: true);
            processRow.Update(process, unavailable: true);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual("—", networkRow.DownText);
        Assert.AreEqual("—", processRow.CpuText);
    }

    private static MetricsHubOptions FastOptions(ConcurrentQueue<(string, Exception?)> logs) => new()
    {
        FastInterval = TimeSpan.FromMilliseconds(5), ProcessInterval = TimeSpan.FromHours(1),
        ThermalInterval = TimeSpan.FromHours(1), VolumeInterval = TimeSpan.FromHours(1),
        Log = (m, e) => logs.Enqueue((m, e)),
    };

    private class FixedProvider<T>(T value) : IMetricProvider<T>
    {
        public string Name => "Fixed";
        public bool IsAvailable => true;
        public virtual T Sample(TimeSpan elapsed) => value;
        public void Initialize() { }
        public void Dispose() { }
    }

    private sealed class EmptyThermalProvider() : FixedProvider<ThermalSnapshot>(ThermalSnapshot.Empty), IThermalProvider
    {
        public bool RequiresElevation => false;
    }

    private sealed class SignalingCpuProvider(bool[] failures) : IMetricProvider<CpuSnapshot>
    {
        private int _samples;
        public string Name => "SignalingCpu";
        public bool IsAvailable => true;
        public bool LastSampleFailed { get; private set; }
        public void Initialize() { }
        public void Dispose() { }
        public CpuSnapshot Sample(TimeSpan elapsed)
        {
            int index = _samples++;
            LastSampleFailed = index < failures.Length && failures[index];
            return LastSampleFailed ? CpuSnapshot.Empty : new() { TotalUsagePercent = _samples * 10.0 };
        }
    }

    private sealed class CountingMemoryProvider : FixedProvider<MemorySnapshot>
    {
        private int _samples;
        public CountingMemoryProvider() : base(MemorySnapshot.Empty) { }
        public override MemorySnapshot Sample(TimeSpan elapsed) => new() { UsedPercent = ++_samples };
    }

    private sealed class ControlledDiskProvider(DiskSnapshot value) : IMetricProvider<DiskSnapshot>
    {
        public bool Fail { get; set; }
        public bool LastSampleFailed => Fail;
        public string Name => "Disk";
        public bool IsAvailable => true;
        public void Initialize() { }
        public void Dispose() { }
        public DiskSnapshot Sample(TimeSpan elapsed) => Fail ? DiskSnapshot.Empty : value;
    }
}
