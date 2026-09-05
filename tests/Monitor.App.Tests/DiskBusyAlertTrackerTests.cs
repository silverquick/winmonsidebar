using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.ViewModels;
using Monitor.Core;
using Monitor.Core.Alerts;
using Monitor.Core.Models;

namespace Monitor.App.Tests;

[TestClass]
public sealed class DiskBusyAlertTrackerTests
{
    private static DiskSnapshot CreateDiskSnapshot(params (int driveNumber, double busy)[] disks)
    {
        var devices = new DiskDeviceSnapshot[disks.Length];
        for (int i = 0; i < disks.Length; i++)
        {
            devices[i] = new DiskDeviceSnapshot
            {
                PhysicalDriveNumber = disks[i].driveNumber,
                Model = $"Test Disk {disks[i].driveNumber}",
                BusType = "NVMe",
                IsSsd = true,
                CapacityBytes = 1000UL * 1024 * 1024 * 1024,
                BusyPercent = disks[i].busy,
                TemperatureC = 40.0,
            };
        }

        return new DiskSnapshot
        {
            Devices = devices,
            BusyPercent = disks.Length > 0 ? disks[0].busy : 0.0,
        };
    }

    [TestMethod]
    public void AlertEvaluator_DiskSustainedBusy_BoundaryValues()
    {
        // 94.9% (閾値直前) は2分継続しても None
        Assert.AreEqual(
            AlertLevel.None,
            AlertEvaluator.DiskSustainedBusy(94.9, TimeSpan.FromMinutes(2), TimeSpan.Zero, AlertLevel.None));

        // 95.0% (閾値一致) で 119秒は None
        Assert.AreEqual(
            AlertLevel.None,
            AlertEvaluator.DiskSustainedBusy(95.0, TimeSpan.FromSeconds(119), TimeSpan.Zero, AlertLevel.None));

        // 95.0% で 120秒は Caution
        Assert.AreEqual(
            AlertLevel.Caution,
            AlertEvaluator.DiskSustainedBusy(95.0, TimeSpan.FromSeconds(120), TimeSpan.Zero, AlertLevel.None));

        // 98.9% で 10分は Caution (99% 未満のため Critical にはならない)
        Assert.AreEqual(
            AlertLevel.Caution,
            AlertEvaluator.DiskSustainedBusy(98.9, TimeSpan.FromMinutes(10), TimeSpan.Zero, AlertLevel.None));

        // 99.0% で 599秒は Caution (2分以上なので Caution)
        Assert.AreEqual(
            AlertLevel.Caution,
            AlertEvaluator.DiskSustainedBusy(99.0, TimeSpan.FromSeconds(599), TimeSpan.Zero, AlertLevel.None));

        // 99.0% で 600秒は Critical
        Assert.AreEqual(
            AlertLevel.Critical,
            AlertEvaluator.DiskSustainedBusy(99.0, TimeSpan.FromSeconds(600), TimeSpan.Zero, AlertLevel.None));

        // 不正値（NaN, Infinity, 負値, 負時間）のハンドリング
        Assert.AreEqual(
            AlertLevel.None,
            AlertEvaluator.DiskSustainedBusy(double.NaN, TimeSpan.FromMinutes(5), TimeSpan.Zero, AlertLevel.None));
        Assert.AreEqual(
            AlertLevel.None,
            AlertEvaluator.DiskSustainedBusy(double.PositiveInfinity, TimeSpan.FromMinutes(5), TimeSpan.Zero, AlertLevel.None));
        Assert.AreEqual(
            AlertLevel.None,
            AlertEvaluator.DiskSustainedBusy(-1.0, TimeSpan.FromMinutes(5), TimeSpan.Zero, AlertLevel.None));
    }

    [TestMethod]
    public void AlertEvaluator_DiskSustainedBusy_HysteresisAndRecovery()
    {
        // Caution 発報中: 90% (85〜95%のヒステリシス帯) では Caution を維持
        Assert.AreEqual(
            AlertLevel.Caution,
            AlertEvaluator.DiskSustainedBusy(90.0, TimeSpan.Zero, TimeSpan.Zero, AlertLevel.Caution));

        // Caution 発報中: 80% (<85%) で 14秒継続は Caution を維持（短い低下では非解除）
        Assert.AreEqual(
            AlertLevel.Caution,
            AlertEvaluator.DiskSustainedBusy(80.0, TimeSpan.Zero, TimeSpan.FromSeconds(14), AlertLevel.Caution));

        // Caution 発報中: 80% (<85%) で 15秒継続すると None へ解除
        Assert.AreEqual(
            AlertLevel.None,
            AlertEvaluator.DiskSustainedBusy(80.0, TimeSpan.Zero, TimeSpan.FromSeconds(15), AlertLevel.Caution));

        // Critical 発報中: 90% (85〜95%のヒステリシス帯) では Critical を維持
        Assert.AreEqual(
            AlertLevel.Critical,
            AlertEvaluator.DiskSustainedBusy(90.0, TimeSpan.Zero, TimeSpan.Zero, AlertLevel.Critical));

        // Critical 発報中: 80% で 14秒継続は Critical を維持
        Assert.AreEqual(
            AlertLevel.Critical,
            AlertEvaluator.DiskSustainedBusy(80.0, TimeSpan.Zero, TimeSpan.FromSeconds(14), AlertLevel.Critical));

        // Critical 発報中: 80% で 15秒継続すると None へ解除
        Assert.AreEqual(
            AlertLevel.None,
            AlertEvaluator.DiskSustainedBusy(80.0, TimeSpan.Zero, TimeSpan.FromSeconds(15), AlertLevel.Critical));
    }

    [TestMethod]
    public void Tracker_ThresholdBoundaries_TriggersAtExactThreshold()
    {
        var tracker = new DiskBusyAlertTracker();
        TimeSpan tick = TimeSpan.FromSeconds(1);

        // 94.9% を 120 秒連続投入 -> 常に None
        for (int i = 0; i < 120; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 94.9)), tick);
            Assert.AreEqual(AlertLevel.None, snap.Devices[0].BusyAlertLevel);
        }

        tracker.Reset();

        // 95.0% を 119 秒投入 -> None
        for (int i = 0; i < 119; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
            Assert.AreEqual(AlertLevel.None, snap.Devices[0].BusyAlertLevel, $"Tick {i + 1} should be None");
        }

        // 120 秒目で Caution 発報
        var cautionSnap = tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
        Assert.AreEqual(AlertLevel.Caution, cautionSnap.Devices[0].BusyAlertLevel, "Tick 120 should trigger Caution");

        tracker.Reset();

        // 99.0% を 599 秒投入 -> 120秒〜599秒は Caution
        for (int i = 0; i < 119; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 99.0)), tick);
            Assert.AreEqual(AlertLevel.None, snap.Devices[0].BusyAlertLevel);
        }
        for (int i = 119; i < 599; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 99.0)), tick);
            Assert.AreEqual(AlertLevel.Caution, snap.Devices[0].BusyAlertLevel, $"Tick {i + 1} should be Caution");
        }

        // 600 秒目で Critical 発報
        var critSnap = tracker.Update(CreateDiskSnapshot((0, 99.0)), tick);
        Assert.AreEqual(AlertLevel.Critical, critSnap.Devices[0].BusyAlertLevel, "Tick 600 should trigger Critical");
    }

    [TestMethod]
    public void Tracker_CautionToCritical_Transition()
    {
        var tracker = new DiskBusyAlertTracker();
        TimeSpan tick = TimeSpan.FromSeconds(1);

        // 95.0% で 120 秒 -> Caution
        for (int i = 0; i < 120; i++)
        {
            tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
        }
        Assert.AreEqual(AlertLevel.Caution, tracker.GetAlertLevel(0));

        // その後 99.0% でさらに 600 秒 -> Critical へ昇格
        for (int i = 0; i < 599; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 99.0)), tick);
            Assert.AreEqual(AlertLevel.Caution, snap.Devices[0].BusyAlertLevel);
        }

        var finalSnap = tracker.Update(CreateDiskSnapshot((0, 99.0)), tick);
        Assert.AreEqual(AlertLevel.Critical, finalSnap.Devices[0].BusyAlertLevel);
    }

    [TestMethod]
    public void Tracker_ShortDips_DoNotClearAlert()
    {
        var tracker = new DiskBusyAlertTracker();
        TimeSpan tick = TimeSpan.FromSeconds(1);

        // 120 秒 95% -> Caution 発報
        for (int i = 0; i < 120; i++)
        {
            tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
        }
        Assert.AreEqual(AlertLevel.Caution, tracker.GetAlertLevel(0));

        // 14 秒間 80% (<85%) に低下 -> Caution を維持
        for (int i = 0; i < 14; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 80.0)), tick);
            Assert.AreEqual(AlertLevel.Caution, snap.Devices[0].BusyAlertLevel, $"Recovery tick {i + 1} should maintain Caution");
        }

        // 95% に復帰 -> Caution を維持
        var recoverSnap = tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
        Assert.AreEqual(AlertLevel.Caution, recoverSnap.Devices[0].BusyAlertLevel);

        // 85〜95% のヒステリシス帯 (90%) に 60 秒滞在 -> Caution を維持
        for (int i = 0; i < 60; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 90.0)), tick);
            Assert.AreEqual(AlertLevel.Caution, snap.Devices[0].BusyAlertLevel);
        }
    }

    [TestMethod]
    public void Tracker_FifteenSecondRecovery_ClearsAlertToNone()
    {
        var tracker = new DiskBusyAlertTracker();
        TimeSpan tick = TimeSpan.FromSeconds(1);

        // Caution 発報
        for (int i = 0; i < 120; i++)
        {
            tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
        }
        Assert.AreEqual(AlertLevel.Caution, tracker.GetAlertLevel(0));

        // 14 秒間 50% -> Caution 維持
        for (int i = 0; i < 14; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 50.0)), tick);
            Assert.AreEqual(AlertLevel.Caution, snap.Devices[0].BusyAlertLevel);
        }

        // 15 秒目で None へ解除
        var clearedSnap = tracker.Update(CreateDiskSnapshot((0, 50.0)), tick);
        Assert.AreEqual(AlertLevel.None, clearedSnap.Devices[0].BusyAlertLevel);

        // その後も None を維持
        var nextSnap = tracker.Update(CreateDiskSnapshot((0, 50.0)), tick);
        Assert.AreEqual(AlertLevel.None, nextSnap.Devices[0].BusyAlertLevel);
    }

    [TestMethod]
    public void Tracker_DisappearingDisk_RemovesState()
    {
        var tracker = new DiskBusyAlertTracker();
        TimeSpan tick = TimeSpan.FromSeconds(1);

        // Disk 0 を Caution にする
        for (int i = 0; i < 120; i++)
        {
            tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
        }
        Assert.AreEqual(AlertLevel.Caution, tracker.GetAlertLevel(0));

        // スナップショットから Disk 0 が消え Disk 1 だけになる
        var snapWithoutDisk0 = tracker.Update(CreateDiskSnapshot((1, 50.0)), tick);
        Assert.AreEqual(AlertLevel.None, tracker.GetAlertLevel(0));

        // Disk 0 が再び現れた時は初期状態（None）から始まる
        var snapWithDisk0Again = tracker.Update(CreateDiskSnapshot((0, 95.0), (1, 50.0)), tick);
        Assert.AreEqual(AlertLevel.None, snapWithDisk0Again.Devices[0].BusyAlertLevel);
    }

    [TestMethod]
    public void Tracker_MissingDataAndInvalidValues_ResetState()
    {
        var tracker = new DiskBusyAlertTracker();
        TimeSpan tick = TimeSpan.FromSeconds(1);

        // Disk 0 を Caution にする
        for (int i = 0; i < 120; i++)
        {
            tracker.Update(CreateDiskSnapshot((0, 95.0)), tick);
        }
        Assert.AreEqual(AlertLevel.Caution, tracker.GetAlertLevel(0));

        // 不正値（NaN）が入った場合は None にリセット
        var nanSnap = tracker.Update(CreateDiskSnapshot((0, double.NaN)), tick);
        Assert.AreEqual(AlertLevel.None, nanSnap.Devices[0].BusyAlertLevel);
        Assert.AreEqual(AlertLevel.None, tracker.GetAlertLevel(0));

        // 空スナップショットが来た場合は全状態リセット
        tracker.Update(new DiskSnapshot(), tick);
        Assert.AreEqual(AlertLevel.None, tracker.GetAlertLevel(0));
    }

    [TestMethod]
    public void Tracker_LongPause_ResetsState()
    {
        var tracker = new DiskBusyAlertTracker(TimeSpan.FromSeconds(3));

        // 100秒間 95% を蓄積
        for (int i = 0; i < 100; i++)
        {
            tracker.Update(CreateDiskSnapshot((0, 95.0)), TimeSpan.FromSeconds(1));
        }
        Assert.AreEqual(AlertLevel.None, tracker.GetAlertLevel(0));

        // サスペンド等の長時間停止（10秒経過 > maxGap 3秒）が発生
        var gapSnap = tracker.Update(CreateDiskSnapshot((0, 95.0)), TimeSpan.FromSeconds(10));
        Assert.AreEqual(AlertLevel.None, gapSnap.Devices[0].BusyAlertLevel);

        // 継続時間がリセットされているため、さらに 20 秒 (計120秒相当) では発報せず 120 秒フルに必要
        for (int i = 0; i < 20; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 95.0)), TimeSpan.FromSeconds(1));
            Assert.AreEqual(AlertLevel.None, snap.Devices[0].BusyAlertLevel);
        }
    }

    [TestMethod]
    public void Tracker_LargeGapWithHighBusyOnResume_DoesNotImmediatelyTriggerAlert()
    {
        var tracker = new DiskBusyAlertTracker(TimeSpan.FromSeconds(3));

        // 通常運転中（99%を50秒蓄積、まだCaution閾値未満）
        for (int i = 0; i < 50; i++)
        {
            tracker.Update(CreateDiskSnapshot((0, 99.0)), TimeSpan.FromSeconds(1));
        }
        Assert.AreEqual(AlertLevel.None, tracker.GetAlertLevel(0));

        // サスペンド復帰: 数時間分の elapsed が一度に来て、かつ復帰直後のサンプルが Busy 99%
        // (Windows Update やウイルススキャンが復帰直後に走るケースを想定)。
        // 巨大な elapsed をそのまま継続時間へ加算すると、この1サンプルだけで
        // Critical 閾値(10分)を満たしてしまう回帰を検出する。
        var resumeSnap = tracker.Update(CreateDiskSnapshot((0, 99.0)), TimeSpan.FromHours(5));
        Assert.AreEqual(
            AlertLevel.None,
            resumeSnap.Devices[0].BusyAlertLevel,
            "サスペンド復帰直後の1サンプルだけで Critical へ誤って昇格してはならない");

        // 復帰後は継続時間がゼロから積み上げ直しになるため、120秒までは None、599秒までは Caution
        for (int i = 0; i < 119; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 99.0)), TimeSpan.FromSeconds(1));
            Assert.AreEqual(AlertLevel.None, snap.Devices[0].BusyAlertLevel, $"Resume+{i + 1}s should still be None");
        }
        for (int i = 119; i < 599; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 99.0)), TimeSpan.FromSeconds(1));
            Assert.AreEqual(AlertLevel.Caution, snap.Devices[0].BusyAlertLevel, $"Resume+{i + 1}s should be Caution, not Critical yet");
        }

        // 600秒目でようやく Critical
        var criticalSnap = tracker.Update(CreateDiskSnapshot((0, 99.0)), TimeSpan.FromSeconds(1));
        Assert.AreEqual(AlertLevel.Critical, criticalSnap.Devices[0].BusyAlertLevel);
    }

    [TestMethod]
    public void Tracker_MultipleDisks_IndependentState()
    {
        var tracker = new DiskBusyAlertTracker();
        TimeSpan tick = TimeSpan.FromSeconds(1);

        // Disk 0 は 99%、Disk 1 は 50%、Disk 2 は 95%
        for (int i = 0; i < 120; i++)
        {
            var snap = tracker.Update(CreateDiskSnapshot((0, 99.0), (1, 50.0), (2, 95.0)), tick);
            if (i == 119)
            {
                Assert.AreEqual(AlertLevel.Caution, snap.Devices[0].BusyAlertLevel, "Disk 0 should be Caution");
                Assert.AreEqual(AlertLevel.None, snap.Devices[1].BusyAlertLevel, "Disk 1 should be None");
                Assert.AreEqual(AlertLevel.Caution, snap.Devices[2].BusyAlertLevel, "Disk 2 should be Caution");
            }
        }
    }

    [TestMethod]
    public void MetricsHub_OtherLanes_DoNotAdvanceDiskBusyDuration()
    {
        var hub = MetricsHub.CreateForTest();

        // 1. 他レーンのスナップショットを直接発行しても、ディスクの継続時間は進まない
        var diskSnapshot = CreateDiskSnapshot((0, 95.0));
        var initialSnapshot = new MetricsSnapshot(
            Timestamp: DateTimeOffset.UtcNow,
            Cpu: CpuSnapshot.Empty,
            Memory: MemorySnapshot.Empty,
            Disk: diskSnapshot,
            Network: NetworkSnapshot.Empty,
            Gpu: GpuSnapshot.Empty,
            Processes: ProcessSnapshot.Empty,
            Thermal: ThermalSnapshot.Empty,
            Volumes: Array.Empty<VolumeSnapshot>());

        hub.PublishSnapshotForTest(initialSnapshot);

        // プロセス・温度・ボリューム等の低頻度レーンを何回発行しても BusyAlertLevel は None のまま
        for (int i = 0; i < 150; i++)
        {
            hub.PublishSnapshotForTest(initialSnapshot with
            {
                Timestamp = DateTimeOffset.UtcNow,
                Processes = new ProcessSnapshot(new ProcessInfo[] { new(100, "test.exe", 10.0, 1000) }),
            });
        }

        Assert.AreEqual(AlertLevel.None, hub.Latest?.Disk.Devices[0].BusyAlertLevel);
    }

    [TestMethod]
    public void XamlBinding_TemperatureTextBindsToTemperatureAlertLevel()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "SidebarWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        // StorageDiskRowTemplate 内の TemperatureText TextBlock を探す
        XElement? tempTextBlock = document
            .Descendants(presentation + "TextBlock")
            .FirstOrDefault(element => (string?)element.Attribute("Text") == "{Binding TemperatureText}");

        Assert.IsNotNull(tempTextBlock, "TemperatureText TextBlock must exist in SidebarWindow.xaml");

        string? foreground = (string?)tempTextBlock.Attribute("Foreground");
        Assert.IsNotNull(foreground, "TemperatureText must have a Foreground binding");
        StringAssert.Contains(foreground, "TemperatureAlertLevel", "TemperatureText Foreground must bind to TemperatureAlertLevel");
        Assert.IsFalse(foreground.Contains("{Binding AlertLevel,"), "TemperatureText Foreground must not bind to row AlertLevel");
    }

    [STATestMethod]
    [DataRow(AlertLevel.None, false, (byte)0, (byte)0, (byte)0, 0.0, 0.0)]
    [DataRow(AlertLevel.Caution, true, (byte)0xF0, (byte)0xC2, (byte)0x3C, 4.0, 0.60)]
    [DataRow(AlertLevel.Critical, true, (byte)0xF0, (byte)0x4A, (byte)0x4A, 5.0, 0.80)]
    public void XamlTheme_StorageDiskHeaderStyle_AppliesDropShadowGlow(
        AlertLevel alertLevel,
        bool hasEffect,
        byte red,
        byte green,
        byte blue,
        double blurRadius,
        double opacity)
    {
        var resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/Monitor.App;component/Themes/Dark.xaml", UriKind.Relative));

        var border = new Border
        {
            DataContext = new AlertLevelSource(alertLevel),
            Style = (Style)resources["StorageDiskHeaderStyle"],
        };

        border.ApplyTemplate();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

        if (!hasEffect)
        {
            Assert.IsNull(border.Effect, "Normal level (None) must not have an Effect applied.");
        }
        else
        {
            Assert.IsTrue(border.Effect is DropShadowEffect, "Caution/Critical must apply a DropShadowEffect.");
            var dropShadow = (DropShadowEffect)border.Effect;
            Assert.AreEqual(Color.FromRgb(red, green, blue), dropShadow.Color);
            Assert.AreEqual(0.0, dropShadow.ShadowDepth, "ShadowDepth must be 0 for uniform glow.");
            Assert.AreEqual(blurRadius, dropShadow.BlurRadius, 0.01);
            Assert.AreEqual(opacity, dropShadow.Opacity, 0.01);
        }
    }

    private sealed class AlertLevelSource(AlertLevel alertLevel)
    {
        public AlertLevel AlertLevel { get; } = alertLevel;
    }
}
