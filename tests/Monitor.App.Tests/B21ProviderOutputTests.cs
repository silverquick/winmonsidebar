using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Windows.Native;
using Monitor.Windows.Providers;
using Monitor.Core;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;

namespace Monitor.App.Tests;

[TestClass]
public sealed class B21ProviderOutputTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object provider, string field, object value) => provider.GetType().GetField(field, Fields)!.SetValue(provider, value);
    private static PdhQuery Query()
    {
        var query = (PdhQuery)Activator.CreateInstance(typeof(PdhQuery), Fields, null, [new SafePdhQueryHandle(IntPtr.Zero)], null)!;
        query.CollectForTest = () => true;
        query.Collect();
        query.Collect();
        return query;
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void CpuInvalidRealCoresFailWithoutReturningZeroDetails(bool clocks, bool allRealInvalid)
    {
        using var query = Query();
        using var usage = new NamedCounter(query, "0,0", "0,1", "_Total");
        using var clock = new NamedCounter(query, "0,0", "0,1", "_Total");
        using var cpu = new CpuProvider();
        Set(cpu, "_pdhQuery", query);
        Set(cpu, "_perCoreCounter", usage.Counter);
        Set(cpu, "_perCoreClockCounter", clock.Counter);
        Set(cpu, "_baseClockMhz", 3600.0);
        Set(cpu, "<IsAvailable>k__BackingField", true);
        CpuSnapshot good = cpu.Sample(TimeSpan.FromSeconds(1));
        Assert.IsFalse(cpu.LastSampleFailed);
        CollectionAssert.AreEqual(new[] { 30.0, 30.0 }, good.PerCoreUsagePercent.ToArray());
        var bad = clocks ? clock : usage;
        bad.Status[1] = Pdh.PDH_CSTATUS_INVALID_DATA;
        if (allRealInvalid) bad.Status[0] = Pdh.PDH_CSTATUS_INVALID_DATA;
        for (int i = 0; i < 3; i++)
        {
            CpuSnapshot failed = cpu.Sample(TimeSpan.FromSeconds(1));
            Assert.IsTrue(cpu.LastSampleFailed, "A valid _Total or another core must not mask an invalid core.");
            Assert.AreSame(CpuSnapshot.Empty, failed, "Failed details must never be returned as fresh zero measurements.");
        }
        bad.Status[0] = bad.Status[1] = 0;
        var recovered = cpu.Sample(TimeSpan.FromSeconds(1));
        Assert.IsFalse(cpu.LastSampleFailed);
        Assert.AreEqual(30.0, recovered.PerCoreUsagePercent[1]);
    }

    private const string AEngine = "pid_1_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D";

    [TestMethod]
    public async Task CpuInvalidCoreSamplesAreHeldByHubAndThirdFailureIsUnavailable()
    {
        using var query = Query();
        using var usage = new NamedCounter(query, "0,0", "0,1", "_Total");
        using var cpu = new CpuProvider();
        Set(cpu, "_pdhQuery", query);
        Set(cpu, "_perCoreCounter", usage.Counter);
        Set(cpu, "<IsAvailable>k__BackingField", true);
        var prepared = new PreparedCpu(cpu, () => usage.Status[1] = Pdh.PDH_CSTATUS_INVALID_DATA);
        var results = new ConcurrentQueue<MetricsSnapshot>();
        using var reached = new ManualResetEventSlim();
        var hub = MetricsHub.CreateForTest(new MetricsHubOptions {
            FastInterval = TimeSpan.FromMilliseconds(5), ProcessInterval = TimeSpan.FromHours(1),
            ThermalInterval = TimeSpan.FromHours(1), VolumeInterval = TimeSpan.FromHours(1)
        }, cpu: prepared);
        hub.SnapshotAvailable += s => { results.Enqueue(s); if (results.Count >= 4) reached.Set(); };
        hub.Start();
        try { Assert.IsTrue(reached.Wait(TimeSpan.FromSeconds(10))); }
        finally { await hub.DisposeAsync(); }
        var snapshots = results.ToArray();
        Assert.AreEqual(30.0, snapshots[0].Cpu.PerCoreUsagePercent[1]);
        for (int i = 1; i <= 3; i++) Assert.AreSame(snapshots[0].Cpu, snapshots[i].Cpu);
        Assert.AreEqual(MetricSources.None, snapshots[2].UnavailableSources & MetricSources.Cpu);
        Assert.AreEqual(MetricSources.Cpu, snapshots[3].UnavailableSources & MetricSources.Cpu);
    }

    private sealed class PreparedCpu(CpuProvider provider, Action fail) : IMetricProvider<CpuSnapshot>
    {
        private int _samples;
        public string Name => "prepared CPU";
        public bool IsAvailable => true;
        public bool LastSampleFailed => provider.LastSampleFailed;
        public string? LastFailureReason => provider.LastFailureReason;
        public void Initialize() { }
        public CpuSnapshot Sample(TimeSpan elapsed) { if (_samples++ > 0) fail(); return provider.Sample(elapsed); }
        public void Dispose() { }
    }
    private const string BEngine = "pid_2_luid_0x00000000_0x0000000B_phys_0_eng_0_engtype_3D";
    private const string AMemory = "luid_0x00000000_0x0000000A_phys_0";
    private const string BMemory = "luid_0x00000000_0x0000000B_phys_0";

    [TestMethod]
    [DataRow(0)] // all A engines invalid, A memory healthy
    [DataRow(1)] // A engines healthy, all A memory invalid
    [DataRow(2)] // all A observations invalid
    [DataRow(3)] // A memory disappears after a good sample
    public void GpuHealthyBDoesNotMaskARequiredReadFailureAcrossConsecutiveSamples(int failure)
    {
        using var query = Query();
        using var engines = new NamedCounter(query, AEngine, BEngine);
        using var memory = new NamedCounter(query, AMemory, BMemory);
        using var gpu = Gpu(query, engines, memory);
        GpuSnapshot good = gpu.Sample(TimeSpan.FromSeconds(1));
        Assert.IsFalse(gpu.LastSampleFailed);
        Assert.AreEqual(2, good.Adapters.Count);
        if (failure is 0 or 2) engines.Status[0] = Pdh.PDH_CSTATUS_INVALID_DATA;
        if (failure is 1 or 2) memory.Status[0] = Pdh.PDH_CSTATUS_INVALID_DATA;
        if (failure == 3) memory.SkipFirst = true;
        for (int i = 0; i < 3; i++)
        {
            GpuSnapshot failed = gpu.Sample(TimeSpan.FromSeconds(1));
            Assert.IsTrue(gpu.LastSampleFailed);
            Assert.AreSame(good, failed);
            var adapterA = failed.Adapters.Single(a => a.Luid == 10);
            Assert.AreEqual(30.0, adapterA.UsagePercent);
            Assert.AreEqual(30UL, adapterA.DedicatedUsedBytes);
            StringAssert.Contains(gpu.LastFailureReason!, "LUID=0x000000000000000A");
        }
        var accumulators = (System.Collections.IDictionary)typeof(GpuProvider).GetField("_perAdapterBuffer", Fields)!.GetValue(gpu)!;
        Assert.IsTrue(accumulators.Contains(10L), "Invalid observations must not remove the adapter accumulator.");
        engines.Status[0] = memory.Status[0] = 0;
        memory.SkipFirst = false;
        Assert.AreEqual(2, gpu.Sample(TimeSpan.FromSeconds(1)).Adapters.Count);
        Assert.IsFalse(gpu.LastSampleFailed);
    }

    [TestMethod]
    public void GpuPartialChurnWithinOneAdapterUsesOnlyValidEngineValues()
    {
        using var query = Query();
        using var engines = new NamedCounter(query, AEngine, AEngine.Replace("pid_1", "pid_3"));
        using var memory = new NamedCounter(query, AMemory);
        using var gpu = Gpu(query, engines, memory);
        engines.Status[0] = Pdh.PDH_CSTATUS_INVALID_DATA;
        engines.Values[0] = 80;
        engines.Values[1] = 42;
        for (int i = 0; i < 3; i++)
        {
            var result = gpu.Sample(TimeSpan.FromSeconds(1));
            Assert.IsFalse(gpu.LastSampleFailed);
            Assert.AreEqual(1, result.Adapters.Count);
            Assert.AreEqual(42.0, result.Adapters[0].UsagePercent);
            Assert.AreEqual(42.0, result.Adapters[0].Engine3DPercent);
        }
    }

    private static GpuProvider Gpu(PdhQuery query, NamedCounter engines, NamedCounter memory)
    {
        var gpu = new GpuProvider();
        Set(gpu, "_query", query);
        Set(gpu, "_engineCounter", engines.Counter);
        Set(gpu, "_dedicatedMemoryCounter", memory.Counter);
        Set(gpu, "<IsAvailable>k__BackingField", true);
        return gpu;
    }

    [TestMethod]
    public async Task RealVolumeLaneLogsDriveAndCodeOnlyOnceForConsecutiveFailedSamples()
    {
        var logs = new ConcurrentQueue<string>();
        using var volumes = new VolumeProvider { ReadDriveNamesForTest = () => ["Z:\\"] };
        volumes.ReadVolumeForTest = _ => new(new VolumeSnapshot { DriveLetter = "Z:", Kind = VolumeKind.Network }, true, 1234, typeof(IOException));
        await volumes.RefreshForTest();
        using var reached = new ManualResetEventSlim();
        int samples = 0;
        var hub = MetricsHub.CreateForTest(new MetricsHubOptions {
            FastInterval = TimeSpan.FromHours(1), ProcessInterval = TimeSpan.FromHours(1), ThermalInterval = TimeSpan.FromHours(1),
            VolumeInterval = TimeSpan.FromMilliseconds(5), Log = (s, _) => logs.Enqueue(s)
        }, volumes: new PreparedVolumes(volumes));
        hub.SnapshotAvailable += s => { if ((s.UnavailableSources & MetricSources.Volumes) != 0 && Interlocked.Increment(ref samples) >= 5) reached.Set(); };
        hub.Start();
        try { Assert.IsTrue(reached.Wait(TimeSpan.FromSeconds(10))); }
        finally { await hub.DisposeAsync(); }
        Assert.AreEqual(1, logs.Count(s => s.Contains("Z: (Network): Win32=1234, exception=IOException")));
    }

    private sealed class PreparedVolumes(VolumeProvider provider) : IMetricProvider<IReadOnlyList<VolumeSnapshot>>
    {
        public string Name => "prepared volumes";
        public bool IsAvailable => true;
        public bool LastSampleFailed => provider.LastSampleFailed;
        public string? LastFailureReason => provider.LastFailureReason;
        public bool PreservesPartialSamplesOnFailure => true;
        public void Initialize() { }
        public IReadOnlyList<VolumeSnapshot> Sample(TimeSpan elapsed) => provider.Sample(elapsed);
        public void Dispose() { }
    }

    private sealed class NamedCounter : IDisposable
    {
        public readonly uint[] Status;
        public readonly double[] Values;
        private readonly IntPtr[] _names;
        public bool SkipFirst;
        public PdhMultiCounter Counter { get; }
        public NamedCounter(PdhQuery query, params string[] names)
        {
            Status = new uint[names.Length];
            Values = Enumerable.Repeat(30.0, names.Length).ToArray();
            _names = names.Select(Marshal.StringToHGlobalUni).ToArray();
            Counter = new PdhMultiCounter("named counter", new IntPtr(1), Read) { Query = query };
        }
        private uint Read(IntPtr h, uint f, ref uint size, ref uint count, IntPtr buffer)
        {
            int offset = SkipFirst ? 1 : 0;
            count = (uint)(_names.Length - offset);
            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            uint needed = count * (uint)stride;
            if (buffer == IntPtr.Zero || size < needed) { size = needed; return Pdh.PDH_MORE_DATA; }
            for (int i = offset; i < _names.Length; i++) Marshal.StructureToPtr(new PDH_FMT_COUNTERVALUE_ITEM_W {
                szName = _names[i], FmtValue = new() { CStatus = Status[i], doubleValue = Values[i] }
            }, IntPtr.Add(buffer, (i-offset)*stride), false);
            return 0;
        }
        public void Dispose() { Counter.Dispose(); foreach (var p in _names) Marshal.FreeHGlobal(p); }
    }
}

