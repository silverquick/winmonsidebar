using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Settings;
using Monitor.App.ViewModels;
using Monitor.Core;
using Monitor.Core.Alerts;
using Monitor.Core.Models;

namespace Monitor.App.Tests;

[TestClass]
public sealed class SidebarViewModelDetailGatingTests
{
    private static MetricsSnapshot CreateSampleSnapshot(
        double cpuUsage = 50.0,
        double cpuTemp = 60.0,
        double gpuUsage = 40.0,
        double gpuTemp = 55.0,
        double gpuHotspot = 65.0,
        ulong memUsed = 8UL * 1024 * 1024 * 1024,
        ulong memTotal = 16UL * 1024 * 1024 * 1024,
        ulong committed = 10UL * 1024 * 1024 * 1024,
        ulong commitLimit = 20UL * 1024 * 1024 * 1024,
        double diskRead = 1024 * 1024,
        double diskWrite = 2048 * 1024,
        double diskTemp = 42.0,
        double netRecv = 500_000,
        double netSend = 100_000,
        double thermalCpu = 60.0,
        double thermalMb = 35.0,
        int fanRpm = 1200,
        IReadOnlyList<MemoryModuleInfo>? customModules = null,
        IReadOnlyList<PageFileInfo>? customPageFiles = null,
        IReadOnlyList<ProcessInfo>? customProcesses = null,
        ThermalSnapshot? customThermal = null)
    {
        var cpu = new CpuSnapshot
        {
            ModelName = "Test CPU 8-Core",
            TotalUsagePercent = cpuUsage,
            BaseClockMhz = 3600,
            CurrentClockMhz = 4200,
            PhysicalCoreCount = 8,
            LogicalCoreCount = 16,
            PerCoreUsagePercent = new double[] { cpuUsage, cpuUsage, cpuUsage, cpuUsage },
            PackageTemperatureC = cpuTemp,
            PackagePowerWatts = 65.0,
        };

        var gpu = new GpuSnapshot
        {
            TotalUsagePercent = gpuUsage,
            Adapters = new GpuAdapterSnapshot[]
            {
                new()
                {
                    Name = "Test GPU RTX",
                    CoreClockMhz = 1800,
                    MemoryClockMhz = 7000,
                    DedicatedUsedBytes = 4UL * 1024 * 1024 * 1024,
                    DedicatedTotalBytes = 8UL * 1024 * 1024 * 1024,
                    TemperatureC = gpuTemp,
                    HotspotTemperatureC = gpuHotspot,
                    PowerWatts = 150.0,
                    PowerLimitWatts = 220.0,
                    FanPercent = 50.0,
                    FanRpm = 1500,
                    Engine3DPercent = gpuUsage,
                },
            },
        };

        var memory = new MemorySnapshot
        {
            TotalBytes = memTotal,
            UsedBytes = memUsed,
            UsedPercent = (double)memUsed / memTotal * 100.0,
            AvailableBytes = memTotal - memUsed,
            CommittedBytes = committed,
            CommitLimitBytes = commitLimit,
            CommitPeakBytes = committed,
            CachedBytes = 2UL * 1024 * 1024 * 1024,
            StandbyBytes = 1UL * 1024 * 1024 * 1024,
            ModifiedBytes = 512UL * 1024 * 1024,
            FreeBytes = memTotal - memUsed,
            CompressedBytes = 256UL * 1024 * 1024,
            PoolPagedBytes = 300UL * 1024 * 1024,
            PoolNonPagedBytes = 200UL * 1024 * 1024,
            SystemCacheBytes = 100UL * 1024 * 1024,
            HandleCount = 40000,
            ProcessCount = 150,
            ThreadCount = 1200,
            Modules = customModules ?? new MemoryModuleInfo[]
            {
                new()
                {
                    Slot = "DIMM 1",
                    CapacityBytes = 8UL * 1024 * 1024 * 1024,
                    SpeedMhz = 3200,
                    MemoryType = "DDR4",
                },
            },
            PageFiles = customPageFiles ?? new PageFileInfo[]
            {
                new()
                {
                    Path = "C:\\pagefile.sys",
                    TotalBytes = 4UL * 1024 * 1024 * 1024,
                    UsedBytes = 1UL * 1024 * 1024 * 1024,
                    PeakBytes = 2UL * 1024 * 1024 * 1024,
                    UsagePercent = 25.0,
                },
            },
            SlotsTotal = 2,
            SlotsUsed = 1,
            SpeedMhz = 3200,
        };

        var disk = new DiskSnapshot
        {
            TotalReadBytesPerSec = diskRead,
            TotalWriteBytesPerSec = diskWrite,
            BusyPercent = 10.0,
            Devices = new DiskDeviceSnapshot[]
            {
                new()
                {
                    PhysicalDriveNumber = 0,
                    Model = "NVMe SSD Test",
                    BusType = "NVMe",
                    IsSsd = true,
                    CapacityBytes = 1000UL * 1024 * 1024 * 1024,
                    ReadBytesPerSec = diskRead,
                    WriteBytesPerSec = diskWrite,
                    BusyPercent = 10.0,
                    TemperatureC = diskTemp,
                    WarningTemperatureC = 70.0,
                    CriticalTemperatureC = 80.0,
                },
            },
        };

        var volumes = new VolumeSnapshot[]
        {
            new()
            {
                DriveLetter = "C:",
                Label = "System",
                FileSystem = "NTFS",
                Kind = VolumeKind.Fixed,
                PhysicalDriveNumber = 0,
                TotalBytes = 500UL * 1024 * 1024 * 1024,
                UsedBytes = 250UL * 1024 * 1024 * 1024,
                FreeBytes = 250UL * 1024 * 1024 * 1024,
                UsedPercent = 50.0,
                IsReady = true,
            },
        };

        var network = new NetworkSnapshot(
            Interfaces: new NetworkInterfaceSnapshot[]
            {
                new("Ethernet 1", "Primary NIC", 1_000_000_000, netRecv, netSend, true),
                new("vEthernet", "Virtual NIC", 10_000_000_000, 1000, 1000, true),
                new("Tailscale", "VPN NIC", 100_000_000, 500, 500, true),
            },
            TotalReceiveBytesPerSec: netRecv,
            TotalSendBytesPerSec: netSend);

        var thermal = customThermal ?? new ThermalSnapshot
        {
            IsAvailable = true,
            IsElevated = true,
            CpuPackageTemperatureC = thermalCpu,
            MotherboardTemperatureC = thermalMb,
            CpuCoreTemperatures = new SensorReading[] { new("Core 0", thermalCpu) },
            OtherTemperatures = new SensorReading[] { new("Chipset", thermalMb) },
            Fans = new SensorReading[] { new("CPU Fan", fanRpm) },
        };

        var processes = new ProcessSnapshot(customProcesses ?? new ProcessInfo[]
        {
            new(1001, "proc_a.exe", 25.0, 500UL * 1024 * 1024),
            new(1002, "proc_b.exe", 15.0, 300UL * 1024 * 1024),
        });

        return new MetricsSnapshot(
            Timestamp: DateTimeOffset.UtcNow,
            Cpu: cpu,
            Memory: memory,
            Disk: disk,
            Network: network,
            Gpu: gpu,
            Processes: processes,
            Thermal: thermal,
            Volumes: volumes);
    }

    [STATestMethod]
    public void Collapsed_DetailPropertiesDoNotUpdate_AndNoPropertyChangedFired()
    {
        var hub = MetricsHub.CreateForTest();
        var initial = CreateSampleSnapshot(cpuUsage: 10.0, gpuUsage: 10.0, memUsed: 4UL * 1024 * 1024 * 1024);
        hub.PublishSnapshotForTest(initial);

        var settings = new AppSettings();
        // 全セクションを折りたたみに設定
        settings.ExpandedSections["cpu"] = false;
        settings.ExpandedSections["gpu"] = false;
        settings.ExpandedSections["memory"] = false;
        settings.ExpandedSections["memory-modules"] = false;
        settings.ExpandedSections["storage"] = false;
        settings.ExpandedSections["network"] = false;
        settings.ExpandedSections["network-all"] = false;
        settings.ExpandedSections["thermal"] = false;
        settings.ExpandedSections["process"] = false;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        // 折りたたみ状態を確認
        Assert.IsFalse(vm.IsCpuExpanded);
        Assert.IsFalse(vm.IsGpuExpanded);
        Assert.IsFalse(vm.IsMemoryExpanded);
        Assert.IsFalse(vm.IsMemoryModulesExpanded);
        Assert.IsFalse(vm.IsStorageExpanded);
        Assert.IsFalse(vm.IsNetworkExpanded);
        Assert.IsFalse(vm.IsThermalExpanded);
        Assert.IsFalse(vm.IsProcessExpanded);

        // 初期状態で詳細プロパティが更新されていないことを確認
        Assert.AreEqual(0, vm.Processes.Count);
        Assert.AreEqual(0, vm.StorageRows.Count);
        Assert.AreEqual(0, vm.CpuSparkline.Length);
        Assert.AreEqual(0, vm.GpuSparkline.Length);
        Assert.AreEqual(0, vm.MemorySparkline.Length);
        Assert.AreEqual(0, vm.DiskReadSparkline.Length);
        Assert.AreEqual(0, vm.NetworkDownSparkline.Length);
        Assert.AreEqual(0, vm.ThermalCoreTemperatures.Count);
        Assert.AreEqual(0, vm.MemoryModules.Count);
        Assert.AreEqual(0, vm.PageFileRows.Count);

        var changedProps = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                changedProps.Add(e.PropertyName);
            }
        };

        // 新しいスナップショットを発行
        var updated = CreateSampleSnapshot(
            cpuUsage: 80.0,
            cpuTemp: 75.0,
            gpuUsage: 90.0,
            gpuTemp: 80.0,
            memUsed: 12UL * 1024 * 1024 * 1024,
            diskRead: 50 * 1024 * 1024,
            diskWrite: 20 * 1024 * 1024,
            netRecv: 10_000_000,
            netSend: 5_000_000,
            thermalCpu: 75.0,
            thermalMb: 45.0);

        hub.PublishSnapshotForTest(updated);
        vm.ApplyLatestSnapshot();

        // 詳細プロパティの PropertyChanged が飛んでいないことを検証
        string[] forbiddenDetailProps =
        [
            nameof(SidebarViewModel.CpuUsageText),
            nameof(SidebarViewModel.CpuModelText),
            nameof(SidebarViewModel.CpuCurrentClockText),
            nameof(SidebarViewModel.CpuGaugeValue),
            nameof(SidebarViewModel.CpuSparkline),
            nameof(SidebarViewModel.CpuCoreValues),
            nameof(SidebarViewModel.CpuCoreCountText),
            nameof(SidebarViewModel.CpuTemperatureText),
            nameof(SidebarViewModel.CpuPowerText),
            nameof(SidebarViewModel.GpuAdapterNameText),
            nameof(SidebarViewModel.GpuUsageText),
            nameof(SidebarViewModel.GpuCoreClockText),
            nameof(SidebarViewModel.GpuGaugeValue),
            nameof(SidebarViewModel.GpuSparkline),
            nameof(SidebarViewModel.Gpu3DValue),
            nameof(SidebarViewModel.GpuCopyValue),
            nameof(SidebarViewModel.GpuVideoValue),
            nameof(SidebarViewModel.GpuComputeValue),
            nameof(SidebarViewModel.GpuTemperatureHotspotText),
            nameof(SidebarViewModel.GpuFanText),
            nameof(SidebarViewModel.GpuPowerText),
            nameof(SidebarViewModel.GpuMemoryClockText),
            nameof(SidebarViewModel.GpuVramText),
            nameof(SidebarViewModel.GpuVramGaugeValue),
            nameof(SidebarViewModel.GpuTemperatureSparkline),
            nameof(SidebarViewModel.MemoryUsageText),
            nameof(SidebarViewModel.MemoryUsagePercentText),
            nameof(SidebarViewModel.MemoryGaugeValue),
            nameof(SidebarViewModel.MemorySparkline),
            nameof(SidebarViewModel.MemoryStackedValues),
            nameof(SidebarViewModel.MemoryCachedText),
            nameof(SidebarViewModel.MemoryStandbyText),
            nameof(SidebarViewModel.MemoryModifiedText),
            nameof(SidebarViewModel.MemoryFreeText),
            nameof(SidebarViewModel.MemoryCompressedText),
            nameof(SidebarViewModel.MemoryPoolText),
            nameof(SidebarViewModel.MemorySystemCacheText),
            nameof(SidebarViewModel.MemoryCommitText),
            nameof(SidebarViewModel.MemoryHandlesLine),
            nameof(SidebarViewModel.MemoryHardwareReservedText),
            nameof(SidebarViewModel.MemorySlotSummaryText),
            nameof(SidebarViewModel.MemoryModules),
            nameof(SidebarViewModel.PageFileRows),
            nameof(SidebarViewModel.DiskReadLineText),
            nameof(SidebarViewModel.DiskWriteLineText),
            nameof(SidebarViewModel.DiskReadSparkline),
            nameof(SidebarViewModel.DiskWriteSparkline),
            nameof(SidebarViewModel.NetworkNicName),
            nameof(SidebarViewModel.NetworkDownText),
            nameof(SidebarViewModel.NetworkUpText),
            nameof(SidebarViewModel.NetworkDownSparkline),
            nameof(SidebarViewModel.NetworkUpSparkline),
            nameof(SidebarViewModel.NetworkAllRows),
            nameof(SidebarViewModel.ThermalCpuPackageText),
            nameof(SidebarViewModel.ThermalCpuPowerText),
            nameof(SidebarViewModel.ThermalMotherboardText),
            nameof(SidebarViewModel.ThermalVrmText),
            nameof(SidebarViewModel.ThermalCoreTemperatures),
            nameof(SidebarViewModel.ThermalOtherTemperatures),
            nameof(SidebarViewModel.ThermalFans),
        ];

        foreach (string forbidden in forbiddenDetailProps)
        {
            Assert.IsFalse(
                changedProps.Contains(forbidden),
                $"Detail property '{forbidden}' should NOT fire PropertyChanged while section is collapsed.");
        }

        // コレクションや配列が生成されていないことを検証
        Assert.AreEqual(0, vm.Processes.Count);
        Assert.AreEqual(0, vm.StorageRows.Count);
        Assert.AreEqual(0, vm.CpuSparkline.Length);
        Assert.AreEqual(0, vm.GpuSparkline.Length);
        Assert.AreEqual(0, vm.MemorySparkline.Length);
        Assert.AreEqual(0, vm.DiskReadSparkline.Length);
        Assert.AreEqual(0, vm.NetworkDownSparkline.Length);
        Assert.AreEqual(0, vm.ThermalCoreTemperatures.Count);
        Assert.AreEqual(0, vm.MemoryModules.Count);
        Assert.AreEqual(0, vm.PageFileRows.Count);
    }

    [STATestMethod]
    public void Collapsed_SummaryAndAlertLevelsUpdateEverySample()
    {
        var hub = MetricsHub.CreateForTest();
        var initial = CreateSampleSnapshot(
            cpuUsage: 10.0,
            cpuTemp: 50.0,
            gpuUsage: 10.0,
            gpuTemp: 45.0,
            gpuHotspot: 50.0,
            memUsed: 4UL * 1024 * 1024 * 1024,
            committed: 4UL * 1024 * 1024 * 1024,
            commitLimit: 16UL * 1024 * 1024 * 1024,
            diskTemp: 40.0,
            netRecv: 1000,
            netSend: 500,
            thermalCpu: 50.0);

        hub.PublishSnapshotForTest(initial);

        var settings = new AppSettings();
        settings.ExpandedSections["cpu"] = false;
        settings.ExpandedSections["gpu"] = false;
        settings.ExpandedSections["memory"] = false;
        settings.ExpandedSections["storage"] = false;
        settings.ExpandedSections["network"] = false;
        settings.ExpandedSections["thermal"] = false;
        settings.ExpandedSections["process"] = false;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        // 初期サマリーと警告レベル（None）を確認
        Assert.AreEqual(AlertLevel.None, vm.CpuAlertLevel);
        Assert.AreEqual(AlertLevel.None, vm.GpuAlertLevel);
        Assert.AreEqual(AlertLevel.None, vm.MemoryAlertLevel);
        Assert.AreEqual(AlertLevel.None, vm.StorageAlertLevel);
        Assert.AreEqual(AlertLevel.None, vm.ThermalAlertLevel);

        // 警告値（危険温度・コミット逼迫）を含むスナップショットを発行
        var criticalSnapshot = CreateSampleSnapshot(
            cpuUsage: 95.0,
            cpuTemp: 95.0,       // CPU critical: >= 90
            gpuUsage: 99.0,
            gpuTemp: 85.0,
            gpuHotspot: 105.0,   // GPU hotspot critical: >= 100
            memUsed: 15UL * 1024 * 1024 * 1024,
            committed: 19UL * 1024 * 1024 * 1024,
            commitLimit: 20UL * 1024 * 1024 * 1024, // commit critical: >= 95%
            diskTemp: 85.0,      // Disk critical: >= critical temp (80.0)
            netRecv: 125_000_000, // 1 Gbps
            netSend: 62_500_000,  // 500 Mbps
            thermalCpu: 95.0,
            fanRpm: 0);          // Cooling fault: Caution (CPU高温 + ファン0RPM、このカテゴリにCriticalは無し)

        hub.PublishSnapshotForTest(criticalSnapshot);
        vm.ApplyLatestSnapshot();

        // 各セクション折りたたみ中にもかかわらず、要約と AlertLevel が最新値へ即座に更新されることを検証
        Assert.AreEqual(AlertLevel.Critical, vm.CpuAlertLevel, "CPU temperature alert must update while collapsed.");
        StringAssert.Contains(vm.CpuSummary, "95%");
        StringAssert.Contains(vm.CpuSummary, "95°C");

        Assert.AreEqual(AlertLevel.Critical, vm.GpuAlertLevel, "GPU hotspot alert must update while collapsed.");
        StringAssert.Contains(vm.GpuSummary, "99%");
        StringAssert.Contains(vm.GpuSummary, "85°C");

        Assert.AreEqual(AlertLevel.Critical, vm.MemoryAlertLevel, "Memory commit alert must update while collapsed.");
        StringAssert.Contains(vm.MemorySummary, "15 GB / 16 GB");

        Assert.AreEqual(AlertLevel.Critical, vm.StorageAlertLevel, "Storage alert must update while collapsed.");
        StringAssert.Contains(vm.StorageSummary, "1 ドライブ");

        StringAssert.Contains(vm.NetworkSummary, "1.0 Gbps");
        StringAssert.Contains(vm.NetworkSummary, "500 Mbps");

        Assert.AreEqual(AlertLevel.Caution, vm.ThermalAlertLevel, "Thermal alert must update while collapsed.");
        StringAssert.Contains(vm.ThermalSummary, "95°C");
    }

    [STATestMethod]
    public void Collapsed_AlertLevelTransitionsBackToNone_WhenValuesNormalize()
    {
        var hub = MetricsHub.CreateForTest();
        var initialCritical = CreateSampleSnapshot(
            cpuUsage: 95.0,
            cpuTemp: 95.0,
            gpuTemp: 85.0,
            gpuHotspot: 105.0,
            committed: 19UL * 1024 * 1024 * 1024,
            commitLimit: 20UL * 1024 * 1024 * 1024,
            diskTemp: 85.0,
            thermalCpu: 95.0,
            fanRpm: 0);

        hub.PublishSnapshotForTest(initialCritical);

        var settings = new AppSettings();
        settings.ExpandedSections["cpu"] = false;
        settings.ExpandedSections["gpu"] = false;
        settings.ExpandedSections["memory"] = false;
        settings.ExpandedSections["storage"] = false;
        settings.ExpandedSections["thermal"] = false;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        // 初期状態で Critical / Caution になっていることを確認
        Assert.AreEqual(AlertLevel.Critical, vm.CpuAlertLevel);
        Assert.AreEqual(AlertLevel.Critical, vm.GpuAlertLevel);
        Assert.AreEqual(AlertLevel.Critical, vm.MemoryAlertLevel);
        Assert.AreEqual(AlertLevel.Critical, vm.StorageAlertLevel);
        Assert.AreEqual(AlertLevel.Caution, vm.ThermalAlertLevel);

        // 正常値に戻ったスナップショットを発行
        var normalSnapshot = CreateSampleSnapshot(
            cpuUsage: 25.0,
            cpuTemp: 45.0,
            gpuTemp: 40.0,
            gpuHotspot: 50.0,
            committed: 6UL * 1024 * 1024 * 1024,
            commitLimit: 20UL * 1024 * 1024 * 1024,
            diskTemp: 35.0,
            thermalCpu: 45.0,
            fanRpm: 1200);

        hub.PublishSnapshotForTest(normalSnapshot);
        vm.ApplyLatestSnapshot();

        // 折りたたみ中であっても警告解除（None）が即座に反映されることを検証
        Assert.AreEqual(AlertLevel.None, vm.CpuAlertLevel, "CPU alert should clear to None while collapsed.");
        Assert.AreEqual(AlertLevel.None, vm.GpuAlertLevel, "GPU alert should clear to None while collapsed.");
        Assert.AreEqual(AlertLevel.None, vm.MemoryAlertLevel, "Memory alert should clear to None while collapsed.");
        Assert.AreEqual(AlertLevel.None, vm.StorageAlertLevel, "Storage alert should clear to None while collapsed.");
        Assert.AreEqual(AlertLevel.None, vm.ThermalAlertLevel, "Thermal alert should clear to None while collapsed.");
    }

    [STATestMethod]
    public void CollapsedToExpanded_ImmediatelyAppliesLatestSnapshotToDetails()
    {
        var hub = MetricsHub.CreateForTest();
        var initial = CreateSampleSnapshot(cpuUsage: 20.0, gpuUsage: 25.0);
        hub.PublishSnapshotForTest(initial);

        var settings = new AppSettings();
        settings.ExpandedSections["cpu"] = false;
        settings.ExpandedSections["gpu"] = false;
        settings.ExpandedSections["memory"] = false;
        settings.ExpandedSections["memory-modules"] = false;
        settings.ExpandedSections["storage"] = false;
        settings.ExpandedSections["network"] = false;
        settings.ExpandedSections["network-all"] = false;
        settings.ExpandedSections["thermal"] = false;
        settings.ExpandedSections["process"] = false;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        // 新しいスナップショットを発行
        var latestSnapshot = CreateSampleSnapshot(
            cpuUsage: 77.0,
            cpuTemp: 68.0,
            gpuUsage: 88.0,
            gpuTemp: 62.0,
            gpuHotspot: 72.0,
            memUsed: 10UL * 1024 * 1024 * 1024,
            diskRead: 12 * 1024 * 1024,
            diskWrite: 34 * 1024 * 1024,
            netRecv: 25_000_000,
            netSend: 12_500_000,
            thermalCpu: 68.0);

        hub.PublishSnapshotForTest(latestSnapshot);
        // この時点では折りたたみ中のため detail はまだ未反映
        vm.ApplyLatestSnapshot();
        Assert.AreEqual("0%", vm.CpuUsageText);
        Assert.AreEqual(0, vm.StorageRows.Count);
        Assert.AreEqual(0, vm.Processes.Count);

        // 1. CPU を展開 -> 即座に latestSnapshot の値が反映される
        vm.IsCpuExpanded = true;
        Assert.AreEqual("77%", vm.CpuUsageText);
        Assert.AreEqual("68°C", vm.CpuTemperatureText);
        Assert.IsTrue(vm.CpuSparkline.Length > 0);
        Assert.AreEqual(4, vm.CpuCoreValues.Count);

        // 2. GPU を展開 -> 即座に反映
        vm.IsGpuExpanded = true;
        Assert.AreEqual("88%", vm.GpuUsageText);
        Assert.AreEqual("62 / 72 °C", vm.GpuTemperatureHotspotText);
        Assert.IsTrue(vm.GpuSparkline.Length > 0);

        // 3. Memory を展開 -> 即座に反映
        vm.IsMemoryExpanded = true;
        StringAssert.Contains(vm.MemoryUsageText, "10 GB / 16 GB");
        Assert.IsTrue(vm.MemoryStackedValues.Count > 0);
        Assert.IsTrue(vm.MemorySparkline.Length > 0);
        Assert.AreEqual(1, vm.PageFileRows.Count);

        // 4. MemoryModules を展開 -> 即座に反映
        Assert.AreEqual(0, vm.MemoryModules.Count);
        vm.IsMemoryModulesExpanded = true;
        Assert.AreEqual(1, vm.MemoryModules.Count);
        Assert.AreEqual("DIMM 1", vm.MemoryModules[0].SlotText);
        StringAssert.Contains(vm.MemorySlotSummaryText, "8GB DDR4-3200");

        // 5. Storage を展開 -> 即座に反映
        vm.IsStorageExpanded = true;
        Assert.IsTrue(vm.StorageRows.Count > 0, "StorageRows should be immediately populated on expand.");
        StringAssert.Contains(vm.DiskReadLineText, "12 MB/s");
        StringAssert.Contains(vm.DiskWriteLineText, "34 MB/s");
        Assert.IsTrue(vm.DiskReadSparkline.Length > 0);

        // 6. Network を展開 -> 即座に反映
        vm.IsNetworkExpanded = true;
        Assert.AreEqual("Ethernet 1", vm.NetworkNicName);
        StringAssert.Contains(vm.NetworkDownText, "200 Mbps");
        StringAssert.Contains(vm.NetworkUpText, "100 Mbps");
        Assert.IsTrue(vm.NetworkDownSparkline.Length > 0);

        // 7. NetworkAll を展開 -> 即座に secondary rows が作成される
        Assert.AreEqual(0, vm.NetworkAllRows.Count);
        vm.IsNetworkAllExpanded = true;
        Assert.AreEqual(2, vm.NetworkAllRows.Count, "Secondary network adapters should be created when expanded.");

        // 8. Thermal を展開 -> 即座に反映
        vm.IsThermalExpanded = true;
        Assert.AreEqual("68°C", vm.ThermalCpuPackageText);
        Assert.AreEqual(1, vm.ThermalCoreTemperatures.Count);
        Assert.AreEqual(1, vm.ThermalFans.Count);

        // 9. Process を展開 -> 即座に反映
        vm.IsProcessExpanded = true;
        Assert.AreEqual(2, vm.Processes.Count, "Processes should be populated immediately on expand.");
        Assert.AreEqual("proc_a.exe", vm.Processes[0].Name);
    }

    [STATestMethod]
    public void RoundTrip_CollapsedExpandedCollapsed_RetainsCorrectValuesAndStopsUpdating()
    {
        var hub = MetricsHub.CreateForTest();
        var snapshot1 = CreateSampleSnapshot(cpuUsage: 30.0, cpuTemp: 55.0);
        hub.PublishSnapshotForTest(snapshot1);

        var settings = new AppSettings();
        settings.ExpandedSections["cpu"] = false;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        // 1. 折りたたみ中: snapshot1 の詳細は反映されていない
        Assert.AreEqual("0%", vm.CpuUsageText);

        // 2. 展開: snapshot1 の詳細が即反映
        vm.IsCpuExpanded = true;
        Assert.AreEqual("30%", vm.CpuUsageText);
        Assert.AreEqual("55°C", vm.CpuTemperatureText);

        // 3. 展開中に snapshot2 到着: 更新される
        var snapshot2 = CreateSampleSnapshot(cpuUsage: 60.0, cpuTemp: 65.0);
        hub.PublishSnapshotForTest(snapshot2);
        vm.ApplyLatestSnapshot();
        Assert.AreEqual("60%", vm.CpuUsageText);
        Assert.AreEqual("65°C", vm.CpuTemperatureText);

        // 4. 再び折りたたむ: 値が壊れず残る
        vm.IsCpuExpanded = false;
        Assert.AreEqual("60%", vm.CpuUsageText);

        // 5. 折りたたみ中に snapshot3 到着: Summary は更新されるが Detail は更新されない
        var snapshot3 = CreateSampleSnapshot(cpuUsage: 85.0, cpuTemp: 78.0);
        hub.PublishSnapshotForTest(snapshot3);
        vm.ApplyLatestSnapshot();

        StringAssert.Contains(vm.CpuSummary, "85%");
        StringAssert.Contains(vm.CpuSummary, "78°C");
        Assert.AreEqual("60%", vm.CpuUsageText, "Detail CpuUsageText must not update while re-collapsed.");
        Assert.AreEqual("65°C", vm.CpuTemperatureText, "Detail CpuTemperatureText must not update while re-collapsed.");

        // 6. 再度展開: 最新 snapshot3 が即反映される
        vm.IsCpuExpanded = true;
        Assert.AreEqual("85%", vm.CpuUsageText);
        Assert.AreEqual("78°C", vm.CpuTemperatureText);
    }

    [STATestMethod]
    public void MemoryModules_SubSection_Gating()
    {
        var hub = MetricsHub.CreateForTest();
        var initial = CreateSampleSnapshot(
            customModules: new MemoryModuleInfo[]
            {
                new()
                {
                    Slot = "DIMM 1",
                    CapacityBytes = 16UL * 1024 * 1024 * 1024,
                    SpeedMhz = 3600,
                    MemoryType = "DDR4",
                },
                new()
                {
                    Slot = "DIMM 2",
                    CapacityBytes = 16UL * 1024 * 1024 * 1024,
                    SpeedMhz = 3600,
                    MemoryType = "DDR4",
                },
            });
        hub.PublishSnapshotForTest(initial);

        var settings = new AppSettings();
        settings.ExpandedSections["memory"] = true;
        settings.ExpandedSections["memory-modules"] = false;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        // 親(memory)は展開中だが子(memory-modules)は折りたたみ中
        Assert.IsTrue(vm.IsMemoryExpanded);
        Assert.IsFalse(vm.IsMemoryModulesExpanded);

        // 子セクションの要約（Summary）は更新される
        Assert.AreEqual("2 本", vm.MemoryModulesSummary);
        // 子セクションの詳細（Detail: 行VMリストおよびスロット要約）は未生成
        Assert.AreEqual(0, vm.MemoryModules.Count);
        Assert.AreEqual("—", vm.MemorySlotSummaryText);

        // 子を展開 -> 即座に最新スナップショットから反映される
        vm.IsMemoryModulesExpanded = true;
        Assert.AreEqual(2, vm.MemoryModules.Count);
        Assert.AreEqual("DIMM 1", vm.MemoryModules[0].SlotText);
        Assert.AreEqual("DIMM 2", vm.MemoryModules[1].SlotText);
        StringAssert.Contains(vm.MemorySlotSummaryText, "16GB DDR4-3200 × 2");

        // 子を折りたたむ -> 行VMリストは残る
        vm.IsMemoryModulesExpanded = false;
        Assert.AreEqual(2, vm.MemoryModules.Count);

        // 折りたたみ中に新しいモジュール構成が届く
        var newModules = new MemoryModuleInfo[]
        {
            new()
            {
                Slot = "DIMM 1",
                CapacityBytes = 32UL * 1024 * 1024 * 1024,
                SpeedMhz = 3200,
                MemoryType = "DDR4",
            },
        };
        var updated = CreateSampleSnapshot(customModules: newModules);
        hub.PublishSnapshotForTest(updated);
        vm.ApplyLatestSnapshot();

        // 折りたたみ中は Summary だけが "1 本" に更新され、MemoryModules は更新されない
        Assert.AreEqual("1 本", vm.MemoryModulesSummary);
        Assert.AreEqual(2, vm.MemoryModules.Count, "MemoryModules must not be rebuilt while collapsed.");

        // 再展開時に即座に新しい構成へ反映される
        vm.IsMemoryModulesExpanded = true;
        Assert.AreEqual(1, vm.MemoryModules.Count);
        Assert.AreEqual("32GB", vm.MemoryModules[0].CapacityText);
        StringAssert.Contains(vm.MemorySlotSummaryText, "32GB DDR4-3200 × 1");
    }

    [STATestMethod]
    public void ReferenceEqualityGuards_DoNotRecreateViewModels_WhenReferencesAreUnchanged()
    {
        var hub = MetricsHub.CreateForTest();

        var modules = new MemoryModuleInfo[]
        {
            new() { Slot = "DIMM 1", CapacityBytes = 8UL * 1024 * 1024 * 1024, SpeedMhz = 3200, MemoryType = "DDR4" },
        };
        var pageFiles = new PageFileInfo[]
        {
            new() { Path = "C:\\pagefile.sys", TotalBytes = 4UL * 1024 * 1024 * 1024, UsedBytes = 1UL * 1024 * 1024 * 1024, PeakBytes = 2UL * 1024 * 1024 * 1024, UsagePercent = 25.0 },
        };
        var processes = new ProcessInfo[]
        {
            new(1001, "proc_a.exe", 25.0, 500UL * 1024 * 1024),
        };
        var thermal = new ThermalSnapshot
        {
            IsAvailable = true,
            IsElevated = true,
            CpuPackageTemperatureC = 50.0,
            CpuCoreTemperatures = new SensorReading[] { new("Core 0", 50.0) },
            Fans = new SensorReading[] { new("Fan 1", 1200) },
        };

        var initial = CreateSampleSnapshot(
            customModules: modules,
            customPageFiles: pageFiles,
            customProcesses: processes,
            customThermal: thermal);

        hub.PublishSnapshotForTest(initial);

        var settings = new AppSettings();
        settings.ExpandedSections["memory"] = true;
        settings.ExpandedSections["memory-modules"] = true;
        settings.ExpandedSections["thermal"] = true;
        settings.ExpandedSections["process"] = true;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        IReadOnlyList<MemoryModuleRowViewModel> initialModules = vm.MemoryModules;
        IReadOnlyList<PageFileRowViewModel> initialPageFiles = vm.PageFileRows;
        IReadOnlyList<SensorRowViewModel> initialThermalCores = vm.ThermalCoreTemperatures;
        int initialProcessCount = vm.Processes.Count;

        // 同じ参照のリスト/インスタンスを載せた新しい snapshot を発行
        var sameReferenceSnapshot = CreateSampleSnapshot(
            cpuUsage: 99.0, // fast tick の値だけ変更
            customModules: modules,
            customPageFiles: pageFiles,
            customProcesses: processes,
            customThermal: thermal);

        hub.PublishSnapshotForTest(sameReferenceSnapshot);
        vm.ApplyLatestSnapshot();

        // 参照等価により再生成されていないことを検証
        Assert.AreSame(initialModules, vm.MemoryModules, "MemoryModules should not be recreated when list reference is unchanged.");
        Assert.AreSame(initialPageFiles, vm.PageFileRows, "PageFileRows should not be recreated when list reference is unchanged.");
        Assert.AreSame(initialThermalCores, vm.ThermalCoreTemperatures, "ThermalCoreTemperatures should not be recreated when thermal reference is unchanged.");
        Assert.AreEqual(initialProcessCount, vm.Processes.Count);

        // 異なる参照のリストを載せた snapshot を発行
        var newModules = new MemoryModuleInfo[]
        {
            new() { Slot = "DIMM 1", CapacityBytes = 16UL * 1024 * 1024 * 1024, SpeedMhz = 3200, MemoryType = "DDR4" },
        };
        var newPageFiles = new PageFileInfo[]
        {
            new() { Path = "D:\\pagefile.sys", TotalBytes = 8UL * 1024 * 1024 * 1024, UsedBytes = 2UL * 1024 * 1024 * 1024, PeakBytes = 3UL * 1024 * 1024 * 1024, UsagePercent = 25.0 },
        };
        var newThermal = new ThermalSnapshot
        {
            IsAvailable = true,
            IsElevated = true,
            CpuPackageTemperatureC = 60.0,
            CpuCoreTemperatures = new SensorReading[] { new("Core 0", 60.0), new("Core 1", 58.0) },
            Fans = new SensorReading[] { new("Fan 1", 1400) },
        };
        var differentReferenceSnapshot = CreateSampleSnapshot(
            customModules: newModules,
            customPageFiles: newPageFiles,
            customThermal: newThermal);

        hub.PublishSnapshotForTest(differentReferenceSnapshot);
        vm.ApplyLatestSnapshot();

        // 参照が変わったため再生成されていることを検証
        Assert.AreNotSame(initialModules, vm.MemoryModules, "MemoryModules should be recreated when list reference changes.");
        Assert.AreNotSame(initialPageFiles, vm.PageFileRows, "PageFileRows should be recreated when list reference changes.");
        Assert.AreNotSame(initialThermalCores, vm.ThermalCoreTemperatures, "ThermalCoreTemperatures should be recreated when thermal reference changes.");
    }

    [STATestMethod]
    public void Expanded_AllDetailsUpdateEverySample()
    {
        var hub = MetricsHub.CreateForTest();
        var initial = CreateSampleSnapshot(cpuUsage: 10.0, gpuUsage: 15.0);
        hub.PublishSnapshotForTest(initial);

        var settings = new AppSettings();
        // 全セクションを展開状態にする
        settings.ExpandedSections["cpu"] = true;
        settings.ExpandedSections["gpu"] = true;
        settings.ExpandedSections["memory"] = true;
        settings.ExpandedSections["memory-modules"] = true;
        settings.ExpandedSections["storage"] = true;
        settings.ExpandedSections["network"] = true;
        settings.ExpandedSections["network-all"] = true;
        settings.ExpandedSections["thermal"] = true;
        settings.ExpandedSections["process"] = true;

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings);

        // 初期状態で展開されていることを確認
        Assert.AreEqual("10%", vm.CpuUsageText);
        Assert.AreEqual("15%", vm.GpuUsageText);
        Assert.IsTrue(vm.StorageRows.Count > 0);
        Assert.AreEqual(2, vm.Processes.Count);
        Assert.AreEqual(2, vm.NetworkAllRows.Count);
        Assert.AreEqual(1, vm.MemoryModules.Count);

        // 新しいスナップショットを発行
        var updated = CreateSampleSnapshot(
            cpuUsage: 65.0,
            cpuTemp: 58.0,
            gpuUsage: 75.0,
            gpuTemp: 60.0,
            gpuHotspot: 68.0,
            memUsed: 12UL * 1024 * 1024 * 1024,
            diskRead: 8 * 1024 * 1024,
            diskWrite: 16 * 1024 * 1024,
            netRecv: 50_000_000,
            netSend: 20_000_000,
            thermalCpu: 58.0);

        hub.PublishSnapshotForTest(updated);
        vm.ApplyLatestSnapshot();

        // 展開中は全 detail が毎サンプル更新されることを検証
        Assert.AreEqual("65%", vm.CpuUsageText);
        Assert.AreEqual("58°C", vm.CpuTemperatureText);
        Assert.AreEqual("75%", vm.GpuUsageText);
        Assert.AreEqual("60 / 68 °C", vm.GpuTemperatureHotspotText);
        StringAssert.Contains(vm.MemoryUsageText, "12 GB / 16 GB");
        StringAssert.Contains(vm.DiskReadLineText, "8.0 MB/s");
        StringAssert.Contains(vm.DiskWriteLineText, "16 MB/s");
        StringAssert.Contains(vm.NetworkDownText, "400 Mbps");
        StringAssert.Contains(vm.NetworkUpText, "160 Mbps");
        Assert.AreEqual("58°C", vm.ThermalCpuPackageText);
        Assert.AreEqual(2, vm.Processes.Count);
        Assert.AreEqual(2, vm.NetworkAllRows.Count);
    }
}
