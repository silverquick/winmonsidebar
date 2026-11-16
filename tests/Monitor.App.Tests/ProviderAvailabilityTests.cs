using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Controls;
using Monitor.App.Settings;
using System.Windows.Threading;
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
    public async Task Volume_ReadFailureKeepsGoodCapacity_ExpectedNotReadyRemainsHealthy()
    {
        var good = new VolumeSnapshot { DriveLetter = "Z:", Kind = VolumeKind.Network, IsReady = true, TotalBytes = 100, UsedBytes = 25 };
        var missing = good with { IsReady = false, TotalBytes = 0, UsedBytes = 0 };
        using var provider = new VolumeProvider { ReadDriveNamesForTest = () => ["Z:\\"] };
        provider.ReadVolumeForTest = _ => new(good, Failed: false);
        await provider.RefreshForTest();
        Assert.AreSame(good, provider.Sample(TimeSpan.FromSeconds(5))[0]);
        provider.ReadVolumeForTest = _ => new(missing, Failed: true);
        await provider.RefreshForTest();
        Assert.AreSame(good, provider.Sample(TimeSpan.FromSeconds(5))[0]);
        Assert.IsTrue(provider.LastSampleFailed);
        provider.ReadVolumeForTest = _ => throw new IOException("unexpected read exception");
        await provider.RefreshForTest();
        Assert.AreSame(good, provider.Sample(TimeSpan.FromSeconds(5))[0]);
        Assert.IsTrue(provider.LastSampleFailed);
        provider.ReadVolumeForTest = _ => new(missing, Failed: false);
        await provider.RefreshForTest();
        Assert.AreSame(missing, provider.Sample(TimeSpan.FromSeconds(5))[0]);
        Assert.IsFalse(provider.LastSampleFailed, "Normal disconnected network observation must remain eligible for existing alerts.");
    }

    [TestMethod]
    [DataRow(VolumeKind.Network, 53, true)]
    [DataRow(VolumeKind.Network, 2250, true)]
    [DataRow(VolumeKind.Network, 1201, true)]
    [DataRow(VolumeKind.Network, 1222, true)]
    [DataRow(VolumeKind.Network, 1232, true)]
    [DataRow(VolumeKind.Removable, 21, true)]
    [DataRow(VolumeKind.CdRom, 1112, true)]
    [DataRow(VolumeKind.Fixed, 5, false)]
    [DataRow(VolumeKind.Network, 5, true)]
    [DataRow(VolumeKind.Network, 1326, true)]
    [DataRow(VolumeKind.Network, 1231, true)]
    [DataRow(VolumeKind.Network, 67, true)]
    [DataRow(VolumeKind.Network, 21, true)]
    [DataRow(VolumeKind.Removable, 5, true)]
    [DataRow(VolumeKind.Removable, 1326, true)]
    [DataRow(VolumeKind.Fixed, 1326, false)]
    [DataRow(VolumeKind.Unknown, 21, false)]
    public void Volume_NotReadyClassification_DoesNotHideUnexpectedReadErrors(VolumeKind kind, int error, bool expected) =>
        Assert.AreEqual(expected, VolumeProvider.IsExpectedNotReady(kind, error));

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
        gauge.UpdateTarget(62, animate: true); // Exercise recovered=true while ordinary changes would animate.
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

    [TestMethod]
    public async Task Volume_DisconnectedDrive_IsRemovedFromCache_WhileTransientFailureKeepsConnectedDrive()
    {
        var driveZ = new VolumeSnapshot { DriveLetter = "Z:", Kind = VolumeKind.Network, IsReady = true, TotalBytes = 100, UsedBytes = 25 };
        var driveY = new VolumeSnapshot { DriveLetter = "Y:", Kind = VolumeKind.Removable, IsReady = true, TotalBytes = 200, UsedBytes = 50 };

        string[] currentDrives = ["Z:\\", "Y:\\"];
        using var provider = new VolumeProvider { ReadDriveNamesForTest = () => currentDrives };
        provider.ReadVolumeForTest = letter => letter.StartsWith("Z", StringComparison.OrdinalIgnoreCase)
            ? new(driveZ, Failed: false)
            : new(driveY, Failed: false);

        await provider.RefreshForTest();
        IReadOnlyList<VolumeSnapshot> initial = provider.Sample(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, initial.Count);

        // Y: が切断され Z: は一時失敗になった場合、Y: はキャッシュから消え Z: は前回値が保持される
        currentDrives = ["Z:\\"];
        provider.ReadVolumeForTest = _ => new(driveZ with { IsReady = false, TotalBytes = 0, UsedBytes = 0 }, Failed: true);

        await provider.RefreshForTest();
        IReadOnlyList<VolumeSnapshot> afterDisconnect = provider.Sample(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, afterDisconnect.Count);
        Assert.AreSame(driveZ, afterDisconnect[0]);
        Assert.IsTrue(provider.LastSampleFailed);
    }

    [TestMethod]
    public async Task Volume_SampleAfterDispose_ReturnsEmptyWithoutThrowing()
    {
        var driveZ = new VolumeSnapshot { DriveLetter = "Z:", Kind = VolumeKind.Fixed, IsReady = true, TotalBytes = 100, UsedBytes = 10 };
        var provider = new VolumeProvider { ReadDriveNamesForTest = () => ["Z:\\"] };
        provider.ReadVolumeForTest = _ => new(driveZ, Failed: false);

        provider.Initialize();
        await provider.RefreshForTest();
        Assert.AreEqual(1, provider.Sample(TimeSpan.FromSeconds(5)).Count);

        provider.Dispose();

        IReadOnlyList<VolumeSnapshot> afterDispose = provider.Sample(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, afterDispose.Count);
        Assert.IsFalse(provider.LastSampleFailed);
        Assert.IsFalse(provider.IsAvailable);
    }

    [TestMethod]
    public async Task Volume_DeniedNetworkDrive_ThreeHubSamplesKeepLocalValuesAvailable()
    {
        using var provider = new VolumeProvider { ReadDriveNamesForTest = () => ["C:\\", "Z:\\"] };
        var local = new VolumeSnapshot { DriveLetter = "C:", Kind = VolumeKind.Fixed, IsReady = true, TotalBytes = 100, UsedBytes = 25 };
        var denied = new VolumeSnapshot { DriveLetter = "Z:", Kind = VolumeKind.Network, IsReady = false };
        provider.ReadVolumeForTest = drive => drive[0] == 'C' ? new(local, false)
            : new(denied, !VolumeProvider.IsExpectedNotReady(VolumeKind.Network, 5));
        await provider.RefreshForTest();
        var hub = VolumeHub(provider);
        var samples = new ConcurrentQueue<MetricsSnapshot>();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.SnapshotAvailable += snapshot => { samples.Enqueue(snapshot); if (samples.Count >= 3) reached.TrySetResult(); };
        hub.Start();
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await hub.DisposeAsync(); }
        foreach (MetricsSnapshot snapshot in samples.Take(3))
        {
            Assert.AreEqual(MetricSources.None, snapshot.UnavailableSources & MetricSources.Volumes);
            Assert.AreEqual(2, snapshot.Volumes.Count);
            Assert.AreSame(local, snapshot.Volumes[0]);
            Assert.IsFalse(snapshot.Volumes[1].IsReady);
        }
        AssertVolumeRows(samples.Take(3).ToArray());
    }

    [TestMethod]
    public async Task Volume_HubPublishesPrunedListEvenWhenSurvivingDriveFails()
    {
        string[] drives = ["C:\\", "Z:\\"];
        bool fail = false;
        var local = new VolumeSnapshot { DriveLetter = "C:", Kind = VolumeKind.Fixed, IsReady = true, TotalBytes = 100 };
        var network = new VolumeSnapshot { DriveLetter = "Z:", Kind = VolumeKind.Network, IsReady = true };
        using var provider = new VolumeProvider { ReadDriveNamesForTest = () => drives };
        provider.ReadVolumeForTest = drive => new(drive[0] == 'C' ? local : network, fail);
        await provider.RefreshForTest();
        var hub = VolumeHub(provider);
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pruned = new TaskCompletionSource<MetricsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.SnapshotAvailable += snapshot =>
        {
            if (snapshot.Volumes.Count == 2) initial.TrySetResult();
            if (snapshot.Volumes.Count == 1) pruned.TrySetResult(snapshot);
        };
        hub.Start();
        try
        {
            await initial.Task.WaitAsync(TimeSpan.FromSeconds(10));
            MetricsSnapshot initialSnapshot = hub.Latest!;
            drives = ["C:\\"];
            fail = true;
            await provider.RefreshForTest();
            MetricsSnapshot snapshot = await pruned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreSame(local, snapshot.Volumes[0], "Retain the individual failed-drive cache while removing Z:.");
            AssertVolumeRows(initialSnapshot, snapshot);
        }
        finally { await hub.DisposeAsync(); }
    }

    private static void AssertVolumeRows(params MetricsSnapshot[] snapshots)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var uiHub = MetricsHub.CreateForTest();
                var settings = new AppSettings();
                settings.ExpandedSections["storage"] = true;
                using var vm = new SidebarViewModel(uiHub, Dispatcher.CurrentDispatcher, settings);
                foreach (MetricsSnapshot snapshot in snapshots)
                {
                    uiHub.PublishSnapshotForTest(snapshot);
                    vm.ApplyLatestSnapshot();
                    Assert.AreEqual(snapshot.Volumes.Count, vm.StorageRows.Count, "Pruned drives must leave the UI too.");
                    foreach (VolumeSnapshot volume in snapshot.Volumes)
                    {
                        StorageRowViewModel row = vm.StorageRows.Single(r => r.Key == volume.DriveLetter);
                        Assert.AreEqual(volume.IsReady, row.IsReady);
                        if (volume.IsReady && volume.TotalBytes > 0)
                            Assert.AreNotEqual("—", row.CapacityText, "Healthy local capacity must stay visible.");
                        else Assert.AreEqual("—", row.CapacityText);
                    }
                }
                uiHub.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.IsNull(failure, failure?.ToString());
    }

    private static MetricsHub VolumeHub(VolumeProvider volumes) => new(new MetricsHubOptions
    {
        FastInterval = TimeSpan.FromHours(1), ProcessInterval = TimeSpan.FromHours(1),
        ThermalInterval = TimeSpan.FromHours(1), VolumeInterval = TimeSpan.FromMilliseconds(5),
    }, new FixedProvider<CpuSnapshot>(CpuSnapshot.Empty), new FixedProvider<MemorySnapshot>(MemorySnapshot.Empty),
        new FixedProvider<DiskSnapshot>(DiskSnapshot.Empty), new FixedProvider<NetworkSnapshot>(NetworkSnapshot.Empty),
        new FixedProvider<GpuSnapshot>(GpuSnapshot.Empty), new FixedProvider<ProcessSnapshot>(ProcessSnapshot.Empty),
        new EmptyThermalProvider(), new PreRefreshedVolumes(volumes));

    // Explicit refreshes keep tests independent of the provider's five-second background schedule.
    private sealed class PreRefreshedVolumes(VolumeProvider provider) : IMetricProvider<IReadOnlyList<VolumeSnapshot>>
    {
        public string Name => "Volumes";
        public bool IsAvailable => true;
        public bool LastSampleFailed => provider.LastSampleFailed;
        public bool PreservesPartialSamplesOnFailure => true;
        public void Initialize() { }
        public IReadOnlyList<VolumeSnapshot> Sample(TimeSpan elapsed) => provider.Sample(elapsed);
        public void Dispose() { }
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
