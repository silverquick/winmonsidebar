using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core.Models;
using Monitor.Windows.Native;
using Monitor.Windows.Providers;

namespace Monitor.Windows.Tests;

[TestClass]
public sealed class DiskProviderTests
{
    [TestMethod]
    public void BuildStaticProjections_WithSingleDiskAndVolume_MapsCorrectly()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(0, "Samsung SSD 980 1TB", "NVMe", true, 1_000_000_000_000UL),
        };

        var volumes = new[]
        {
            new VolumeToDiskMapping("C:", "Windows", 500_000_000_000UL, 200_000_000_000UL, 0),
        };

        var projections = DiskProvider.BuildStaticProjections(disks, volumes);

        Assert.AreEqual(1, projections.Count);
        Assert.IsTrue(projections.TryGetValue(0, out DiskProvider.DiskStaticProjection projection));
        Assert.AreEqual("C:", projection.DisplayName);
        Assert.AreEqual(1, projection.Volumes.Count);

        LogicalVolumeSnapshot volume = projection.Volumes[0];
        Assert.AreEqual("C:", volume.DriveLetter);
        Assert.AreEqual("Windows", volume.Label);
        Assert.AreEqual(500_000_000_000UL, volume.TotalBytes);
        Assert.AreEqual(200_000_000_000UL, volume.FreeBytes);
        Assert.AreEqual(60.0, volume.UsedPercent, 0.001);
    }

    [TestMethod]
    public void BuildStaticProjections_WithMultipleVolumesOnSameDisk_PreservesOrderAndBuildsDisplayName()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(0, "Samsung SSD 980 1TB", "NVMe", true, 1_000_000_000_000UL),
        };

        var volumes = new[]
        {
            new VolumeToDiskMapping("C:", "System", 400_000_000_000UL, 100_000_000_000UL, 0),
            new VolumeToDiskMapping("D:", "Data", 600_000_000_000UL, 300_000_000_000UL, 0),
        };

        var projections = DiskProvider.BuildStaticProjections(disks, volumes);

        Assert.AreEqual(1, projections.Count);
        Assert.IsTrue(projections.TryGetValue(0, out DiskProvider.DiskStaticProjection projection));
        Assert.AreEqual("C: D:", projection.DisplayName);
        Assert.AreEqual(2, projection.Volumes.Count);
        Assert.AreEqual("C:", projection.Volumes[0].DriveLetter);
        Assert.AreEqual("D:", projection.Volumes[1].DriveLetter);
    }

    [TestMethod]
    public void BuildStaticProjections_WithMultipleDisksAndNoVolumeDisk_MapsCorrectly()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(0, "NVMe Drive 0", "NVMe", true, 1_000_000_000_000UL),
            new PhysicalDiskInfo(1, "SATA SSD Drive 1", "SATA", true, 500_000_000_000UL),
            new PhysicalDiskInfo(2, "Raw HDD Drive 2", "SATA", false, 2_000_000_000_000UL),
        };

        var volumes = new[]
        {
            new VolumeToDiskMapping("C:", "OS", 500_000_000_000UL, 250_000_000_000UL, 0),
            new VolumeToDiskMapping("D:", "Games", 500_000_000_000UL, 100_000_000_000UL, 0),
            new VolumeToDiskMapping("E:", "Backup", 500_000_000_000UL, 400_000_000_000UL, 1),
        };

        var projections = DiskProvider.BuildStaticProjections(disks, volumes);

        Assert.AreEqual(3, projections.Count);

        // Disk 0: C: D:
        Assert.AreEqual("C: D:", projections[0].DisplayName);
        Assert.AreEqual(2, projections[0].Volumes.Count);

        // Disk 1: E:
        Assert.AreEqual("E:", projections[1].DisplayName);
        Assert.AreEqual(1, projections[1].Volumes.Count);

        // Disk 2: No volumes -> DisplayName is "Disk 2"
        Assert.AreEqual("Disk 2", projections[2].DisplayName);
        Assert.AreEqual(0, projections[2].Volumes.Count);
    }

    [TestMethod]
    public void BuildStaticProjections_UsedPercent_ComputesAccuratelyAndClamps()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(0, "Test Disk", "NVMe", true, 1_000_000_000_000UL),
        };

        var volumes = new[]
        {
            // Normal 75%
            new VolumeToDiskMapping("C:", "Normal", 1000UL, 250UL, 0),
            // Total is 0 -> 0%
            new VolumeToDiskMapping("D:", "ZeroTotal", 0UL, 0UL, 0),
            // Free > Total (overflow/anomaly) -> clamped to 0%
            new VolumeToDiskMapping("E:", "FreeExceedsTotal", 1000UL, 1500UL, 0),
        };

        var projections = DiskProvider.BuildStaticProjections(disks, volumes);

        Assert.AreEqual(3, projections[0].Volumes.Count);
        Assert.AreEqual(75.0, projections[0].Volumes[0].UsedPercent, 0.001);
        Assert.AreEqual(0.0, projections[0].Volumes[1].UsedPercent, 0.001);
        Assert.AreEqual(0.0, projections[0].Volumes[2].UsedPercent, 0.001);
    }

    [TestMethod]
    public void BuildStaticProjections_WithEmptyDisksOrVolumes_HandlesGracefully()
    {
        var emptyDisks = Array.Empty<PhysicalDiskInfo>();
        var emptyVolumes = Array.Empty<VolumeToDiskMapping>();

        var emptyProjections = DiskProvider.BuildStaticProjections(emptyDisks, emptyVolumes);
        Assert.AreEqual(0, emptyProjections.Count);

        var singleDisk = new[] { new PhysicalDiskInfo(3, "Disk Model", "USB", false, 500_000_000UL) };
        var projectionsNoVolumes = DiskProvider.BuildStaticProjections(singleDisk, emptyVolumes);
        Assert.AreEqual(1, projectionsNoVolumes.Count);
        Assert.AreEqual("Disk 3", projectionsNoVolumes[3].DisplayName);
        Assert.AreEqual(0, projectionsNoVolumes[3].Volumes.Count);
    }

    [TestMethod]
    public void DiskProvider_Sample_ReusesStaticProjectionReferences_AcrossConsecutiveSamples()
    {
        var disks = new[]
        {
            new PhysicalDiskInfo(0, "Drive 0", "NVMe", true, 1_000_000_000_000UL),
            new PhysicalDiskInfo(1, "Drive 1", "SATA", false, 2_000_000_000_000UL),
        };

        var volumes = new[]
        {
            new VolumeToDiskMapping("C:", "OS", 500_000_000_000UL, 250_000_000_000UL, 0),
            new VolumeToDiskMapping("D:", "Data", 500_000_000_000UL, 100_000_000_000UL, 0),
        };

        var provider = new DiskProvider(() => disks, () => volumes);
        provider.Initialize();

        var sample1 = provider.Sample(TimeSpan.FromSeconds(1));
        var sample2 = provider.Sample(TimeSpan.FromSeconds(1));
        var sample3 = provider.Sample(TimeSpan.FromSeconds(1));

        Assert.AreEqual(2, sample1.Devices.Count);
        Assert.AreEqual(2, sample2.Devices.Count);
        Assert.AreEqual(2, sample3.Devices.Count);

        // Drive 0 has volumes C: and D:
        Assert.AreEqual("C: D:", sample1.Devices[0].DisplayName);
        Assert.AreEqual(2, sample1.Devices[0].Volumes.Count);

        // Invariant: Across consecutive samples within the refresh interval,
        // Volumes and DisplayName are the EXACT same object references (no per-sample heap allocations).
        Assert.AreSame(sample1.Devices[0].Volumes, sample2.Devices[0].Volumes, "Volumes reference must be reused across samples.");
        Assert.AreSame(sample2.Devices[0].Volumes, sample3.Devices[0].Volumes, "Volumes reference must be reused across samples.");

        Assert.AreSame(sample1.Devices[0].DisplayName, sample2.Devices[0].DisplayName, "DisplayName reference must be reused across samples.");
        Assert.AreSame(sample2.Devices[0].DisplayName, sample3.Devices[0].DisplayName, "DisplayName reference must be reused across samples.");

        // Drive 1 has no volumes -> DisplayName "Disk 1"
        Assert.AreEqual("Disk 1", sample1.Devices[1].DisplayName);
        Assert.AreSame(sample1.Devices[1].DisplayName, sample2.Devices[1].DisplayName);
        Assert.AreSame(sample1.Devices[1].Volumes, sample2.Devices[1].Volumes);
    }

    [TestMethod]
    public void DiskProvider_RefreshStaticInfo_UpdatesProjections_AndSubsequentSamplesReuseThem()
    {
        var disks = new List<PhysicalDiskInfo>
        {
            new(0, "Drive 0", "NVMe", true, 1_000_000_000_000UL),
        };

        var volumes = new List<VolumeToDiskMapping>
        {
            new("C:", "OS", 500_000_000_000UL, 250_000_000_000UL, 0),
        };

        var provider = new DiskProvider(() => disks, () => volumes);
        provider.Initialize();

        var initialSample = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, initialSample.Devices.Count);
        Assert.AreEqual("C:", initialSample.Devices[0].DisplayName);

        // Simulate hot-plug of a secondary drive and volume configuration update
        disks.Add(new PhysicalDiskInfo(1, "Drive 1", "SATA", true, 2_000_000_000_000UL));
        volumes.Add(new VolumeToDiskMapping("D:", "Data", 1_000_000_000_000UL, 800_000_000_000UL, 1));

        provider.RefreshStaticInfo();

        var sampleAfterRefresh1 = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, sampleAfterRefresh1.Devices.Count);
        Assert.AreEqual("C:", sampleAfterRefresh1.Devices[0].DisplayName);
        Assert.AreEqual("D:", sampleAfterRefresh1.Devices[1].DisplayName);

        var sampleAfterRefresh2 = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.AreSame(sampleAfterRefresh1.Devices[0].Volumes, sampleAfterRefresh2.Devices[0].Volumes);
        Assert.AreSame(sampleAfterRefresh1.Devices[1].Volumes, sampleAfterRefresh2.Devices[1].Volumes);
        Assert.AreSame(sampleAfterRefresh1.Devices[0].DisplayName, sampleAfterRefresh2.Devices[0].DisplayName);
        Assert.AreSame(sampleAfterRefresh1.Devices[1].DisplayName, sampleAfterRefresh2.Devices[1].DisplayName);
    }

    [TestMethod]
    public void DiskProvider_EnumerationException_FallsBackSafelyWithoutThrowing()
    {
        var provider = new DiskProvider(
            () => throw new InvalidOperationException("Disk enum failure"),
            () => throw new InvalidOperationException("Volume enum failure"));

        // Initialize and Sample must not throw even if native storage enumeration fails
        provider.Initialize();
        var snapshot = provider.Sample(TimeSpan.FromSeconds(1));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(0, snapshot.Devices.Count);
    }
}
