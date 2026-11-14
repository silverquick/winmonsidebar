using System.Collections.Concurrent;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Optional.Lhm;

namespace Monitor.App.Tests;

[TestClass]
public sealed class LhmAvailabilityTests
{
    [TestMethod]
    public void RepresentativeChildScanFailure_IsFailed_EvenWhenParentHasNoOwnSensors()
    {
        var (child, _) = Hardware(HardwareType.SuperIO, Sensor("System", 40));
        var (parent, control) = Hardware(HardwareType.Motherboard);
        control.Children = [child];
        using var provider = new LhmThermalProvider();
        provider.SetHardwareForTest([parent]);
        Assert.AreEqual(40.0, provider.Sample(TimeSpan.FromSeconds(5)).MotherboardTemperatureC);
        control.ChildrenFail = true;
        provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsTrue(provider.LastSampleFailed);
        control.ChildrenFail = false;
        provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsFalse(provider.LastSampleFailed);
    }

    [TestMethod]
    public void KnownOptionalSensorMetadataFailure_DoesNotFailHealthyCpuPackage()
    {
        ISensor optional = Sensor("Core #1", 55);
        var (cpu, _) = Hardware(HardwareType.Cpu, Sensor("CPU Package", 65), optional);
        using var provider = new LhmThermalProvider();
        provider.SetHardwareForTest([cpu]);
        Assert.AreEqual(1, provider.Sample(TimeSpan.FromSeconds(5)).CpuCoreTemperatures.Count);
        ((SensorProxy)(object)optional).NameFails = true;
        ThermalSnapshot sample = provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsFalse(provider.LastSampleFailed);
        Assert.AreEqual(65.0, sample.CpuPackageTemperatureC);
        Assert.AreEqual(0, sample.CpuCoreTemperatures.Count);
    }

    [TestMethod]
    public async Task RepresentativeUpdateFailure_HoldsTwo_UnavailableOnThird_Recovers()
    {
        int updates = 0;
        var (cpu, control) = Hardware(HardwareType.Cpu, Sensor("CPU Package", 65));
        control.Update = () => { if (++updates is >= 2 and <= 4) throw new InvalidOperationException("update failed"); };
        var thermal = new LhmThermalProvider();
        thermal.SetHardwareForTest([cpu]);
        // Keep production Initialize from opening native hardware in this unit test.
        var hub = new MetricsHub(new()
        {
            FastInterval = TimeSpan.FromHours(1), ProcessInterval = TimeSpan.FromHours(1),
            ThermalInterval = TimeSpan.FromMilliseconds(5), VolumeInterval = TimeSpan.FromHours(1),
        }, new Fixed<CpuSnapshot>(CpuSnapshot.Empty), new Fixed<MemorySnapshot>(MemorySnapshot.Empty),
            new Fixed<DiskSnapshot>(DiskSnapshot.Empty), new Fixed<NetworkSnapshot>(NetworkSnapshot.Empty),
            new Fixed<GpuSnapshot>(GpuSnapshot.Empty), new Fixed<ProcessSnapshot>(ProcessSnapshot.Empty),
            new InitializedThermal(thermal), new Fixed<IReadOnlyList<VolumeSnapshot>>([]));
        var published = new ConcurrentQueue<MetricsSnapshot>();
        using var reached = new ManualResetEventSlim();
        hub.SnapshotAvailable += s => { published.Enqueue(s); if (published.Count >= 5) reached.Set(); };
        hub.Start();
        try { Assert.IsTrue(reached.Wait(TimeSpan.FromSeconds(10))); }
        finally { await hub.DisposeAsync(); }
        MetricsSnapshot[] samples = published.Take(5).ToArray();
        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual(i == 3 ? MetricSources.Thermal : MetricSources.None, samples[i].UnavailableSources);
            Assert.AreEqual(65.0, samples[i].Thermal.CpuPackageTemperatureC);
        }
        Assert.AreSame(samples[0].Thermal, samples[1].Thermal);
        Assert.AreSame(samples[0].Thermal, samples[2].Thermal);
        Assert.AreNotSame(samples[0].Thermal, samples[4].Thermal);
    }

    [TestMethod]
    public void OptionalUpdateOrScanFailure_DropsReadingsWithoutFailingRepresentativeSource()
    {
        var (cpu, _) = Hardware(HardwareType.Cpu, Sensor("CPU Package", 65));
        var (secondary, control) = Hardware(HardwareType.SuperIO, Sensor("Aux", 44));
        using var provider = new LhmThermalProvider();
        provider.SetHardwareForTest([cpu, secondary]);
        Assert.AreEqual(1, provider.Sample(TimeSpan.FromSeconds(5)).OtherTemperatures.Count);
        control.Update = () => throw new InvalidOperationException("optional update");
        ThermalSnapshot sample = provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsFalse(provider.LastSampleFailed);
        Assert.AreEqual(65.0, sample.CpuPackageTemperatureC);
        Assert.AreEqual(0, sample.OtherTemperatures.Count);
        control.Update = () => { };
        control.ScanFails = true;
        sample = provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsFalse(provider.LastSampleFailed);
        Assert.AreEqual(0, sample.OtherTemperatures.Count);
    }

    [TestMethod]
    public void RepresentativeSensorReadOrScanFailure_IsFailed_ButNullAndHiddenAreNormal()
    {
        ISensor sensor = Sensor("CPU Package", 65);
        var sensorControl = (SensorProxy)(object)sensor;
        var (cpu, control) = Hardware(HardwareType.Cpu, sensor);
        using var provider = new LhmThermalProvider();
        provider.SetHardwareForTest([cpu]);
        provider.Sample(TimeSpan.FromSeconds(5));
        sensorControl.ReadingFails = true;
        provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsTrue(provider.LastSampleFailed);
        sensorControl.ReadingFails = false;
        sensorControl.Temperature = null;
        provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsFalse(provider.LastSampleFailed, "A legitimately absent temperature is normal.");
        control.ScanFails = true;
        provider.Sample(TimeSpan.FromSeconds(5));
        Assert.IsTrue(provider.LastSampleFailed);
        control.ScanFails = false;
        using var hidden = new LhmThermalProvider(hiddenSensors: ["CPU Package"]);
        hidden.SetHardwareForTest([cpu]);
        control.Update = () => throw new InvalidOperationException("hidden hardware");
        hidden.Sample(TimeSpan.FromSeconds(5));
        Assert.IsFalse(hidden.LastSampleFailed, "A hidden temperature must not become a representative failure.");
    }

    private static (IHardware, HardwareProxy) Hardware(HardwareType type, params ISensor[] sensors)
    {
        IHardware proxy = DispatchProxy.Create<IHardware, HardwareProxy>();
        var control = (HardwareProxy)(object)proxy;
        control.Type = type;
        control.Sensors = sensors;
        return (proxy, control);
    }

    private static ISensor Sensor(string name, float value)
    {
        ISensor proxy = DispatchProxy.Create<ISensor, SensorProxy>();
        var control = (SensorProxy)(object)proxy;
        control.Name = name;
        control.Temperature = value;
        return proxy;
    }

    public class HardwareProxy : DispatchProxy
    {
        public HardwareType Type;
        public ISensor[] Sensors = [];
        public bool ScanFails;
        public IHardware[] Children = [];
        public bool ChildrenFail;
        public Action Update = () => { };
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "Update" => RunUpdate(),
            "get_HardwareType" => Type,
            "get_Sensors" => ScanFails ? throw new InvalidOperationException("scan") : Sensors,
            "get_SubHardware" => ChildrenFail ? throw new InvalidOperationException("child scan") : Children,
            "get_Name" => "Test hardware",
            _ => null,
        };
        private object? RunUpdate() { Update(); return null; }
    }

    public class SensorProxy : DispatchProxy
    {
        public string Name = "";
        public float? Temperature;
        public bool ReadingFails;
        public bool NameFails;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_Name" => NameFails ? throw new InvalidOperationException("sensor name") : Name,
            "get_SensorType" => SensorType.Temperature,
            "get_Value" => ReadingFails ? throw new InvalidOperationException("sensor") : Temperature,
            "get_Identifier" => new Identifier("test", "temperature", Name),
            _ => null,
        };
    }

    private sealed class InitializedThermal(LhmThermalProvider provider) : IThermalProvider
    {
        public string Name => provider.Name;
        public bool IsAvailable => provider.IsAvailable;
        public bool LastSampleFailed => provider.LastSampleFailed;
        public bool RequiresElevation => true;
        public void Initialize() { }
        public ThermalSnapshot Sample(TimeSpan elapsed) => provider.Sample(elapsed);
        public void Dispose() => provider.Dispose();
    }

    private sealed class Fixed<T>(T value) : IMetricProvider<T>
    {
        public string Name => "Fixed";
        public bool IsAvailable => true;
        public void Initialize() { }
        public T Sample(TimeSpan elapsed) => value;
        public void Dispose() { }
    }
}
