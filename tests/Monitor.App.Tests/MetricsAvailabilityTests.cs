using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;

namespace Monitor.App.Tests;

[TestClass]
public sealed class MetricsAvailabilityTests
{
    [TestMethod]
    [DataRow("fast", MetricSources.Fast)]
    [DataRow("thermal", MetricSources.Thermal)]
    [DataRow("process", MetricSources.Processes)]
    [DataRow("volume", MetricSources.Volumes)]
    public async Task FailedSamples_HoldTwoThenUnavailableAndRecover(string lane, MetricSources sources)
    {
        var cpu = new ScriptedProvider<CpuSnapshot>(new() { TotalUsagePercent = 42 });
        var memory = new ScriptedProvider<MemorySnapshot>(new() { UsedPercent = 57 });
        var disk = new ScriptedProvider<DiskSnapshot>(new() { TotalReadBytesPerSec = 123 });
        var network = new ScriptedProvider<NetworkSnapshot>(new([], 456, 789));
        var gpu = new ScriptedProvider<GpuSnapshot>(new() { TotalUsagePercent = 23 });
        var process = new ScriptedProvider<ProcessSnapshot>(new() { Processes = [new(1, "test", 12, 345)] });
        var thermal = new ScriptedThermalProvider(new() { IsAvailable = true, IsElevated = true, CpuPackageTemperatureC = 67 });
        var volumes = new ScriptedProvider<IReadOnlyList<VolumeSnapshot>>([new() { DriveLetter = "C:", TotalBytes = 100 }]);
        TimeSpan Interval(string name) => lane == name ? TimeSpan.FromMilliseconds(5) : TimeSpan.FromHours(1);
        var hub = new MetricsHub(new()
        {
            FastInterval = Interval("fast"), ThermalInterval = Interval("thermal"),
            ProcessInterval = Interval("process"), VolumeInterval = Interval("volume"),
        }, cpu, memory, disk, network, gpu, process, thermal, volumes);
        var published = new ConcurrentQueue<MetricsSnapshot>();
        using var reached = new ManualResetEventSlim();
        hub.SnapshotAvailable += s => { published.Enqueue(s); if (published.Count >= 6) reached.Set(); };
        hub.Start();
        try
        {
            Assert.IsTrue(reached.Wait(TimeSpan.FromSeconds(10)), "Lane must publish six samples without sleeps.");
        }
        finally
        {
            await hub.DisposeAsync();
        }
        MetricsSnapshot[] samples = published.Take(6).ToArray();
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.AreEqual(i is 3 or 4 ? sources : MetricSources.None, samples[i].UnavailableSources, $"Sample {i}");
            if (lane == "fast")
            {
                Assert.AreSame(cpu.Value, samples[i].Cpu);
                Assert.AreSame(memory.Value, samples[i].Memory);
                Assert.AreSame(disk.Value, samples[i].Disk);
                Assert.AreEqual(network.Value, samples[i].Network);
                Assert.AreSame(gpu.Value, samples[i].Gpu);
            }
            else if (lane == "thermal") Assert.AreSame(thermal.Value, samples[i].Thermal);
            else if (lane == "process") Assert.AreEqual(process.Value, samples[i].Processes);
            else Assert.AreSame(volumes.Value, samples[i].Volumes);
        }
        if (lane == "fast")
        {
            // No missing sample is replaced with zero (additional recovery ticks may have arrived).
            Assert.IsTrue(hub.History.Snapshot(MetricSeries.CpuTotal).All(v => v == 42));
            Assert.AreEqual(published.Count - 2, hub.History.Snapshot(MetricSeries.CpuTotal).Length);
        }
    }

    [TestMethod]
    public void History_UnavailableSourcesDoNotAppend_HealthySourcesStillAppend()
    {
        var history = new MetricsHistory();
        history.Append(MetricsSnapshot.Empty);
        history.Append(MetricsSnapshot.Empty with { UnavailableSources = MetricSources.Cpu | MetricSources.Network });
        Assert.AreEqual(1, history.Snapshot(MetricSeries.CpuTotal).Length);
        Assert.AreEqual(1, history.Snapshot(MetricSeries.CpuTemperature).Length);
        Assert.AreEqual(1, history.Snapshot(MetricSeries.NetReceiveBytesPerSec).Length);
        Assert.AreEqual(2, history.Snapshot(MetricSeries.GpuTotal).Length);
    }

    private class ScriptedProvider<T>(T value) : IMetricProvider<T>
    {
        private int _samples;
        public T Value { get; } = value;
        public string Name => "Scripted";
        public bool IsAvailable => true;
        public void Initialize() { }
        public T Sample(TimeSpan elapsed) => ++_samples is >= 2 and <= 5
            ? throw new InvalidOperationException("sample failure") : Value;
        public void Dispose() { }
    }

    private sealed class ScriptedThermalProvider(ThermalSnapshot value) : ScriptedProvider<ThermalSnapshot>(value), IThermalProvider
    {
        public bool RequiresElevation => false;
    }
}
