using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Windows.Native;
using Monitor.Windows.Providers;
using Monitor.Core.Models;
using Monitor.Core;
using Monitor.Core.Abstractions;
using System.Collections.Concurrent;

namespace Monitor.App.Tests;

[TestClass]
public sealed class B21SampleFailureTests
{
    [TestMethod]
    [DataRow(0x800007D1u)] // instance disappears
    [DataRow(0xC0000BBAu)] // one item invalid
    [DataRow(0x800007D6u)]
    [DataRow(0x800007D7u)]
    [DataRow(0x800007D8u)]
    public void MixedTransientItemsKeepWildcardHealthy_AllInvalidStillFails(uint bad)
    {
        uint healthy = 1; // NEW_DATA is valid item data
        uint Read(IntPtr h, uint f, ref uint size, ref uint count, IntPtr buffer)
        {
            uint stride = (uint)Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            count = 2;
            if (buffer == IntPtr.Zero) { size = stride * 2; return Pdh.PDH_MORE_DATA; }
            Marshal.StructureToPtr(new PDH_FMT_COUNTERVALUE_ITEM_W { FmtValue = new() { CStatus = bad } }, buffer, false);
            Marshal.StructureToPtr(new PDH_FMT_COUNTERVALUE_ITEM_W { FmtValue = new() { CStatus = healthy, doubleValue = 12 } }, IntPtr.Add(buffer, (int)stride), false);
            return 0;
        }
        using var counter = new PdhMultiCounter("dynamic", new IntPtr(1), Read) { TolerateInstanceChurn = true };
        foreach (var item in counter.Enumerate()) { }
        foreach (var item in counter.Enumerate()) { }
        Assert.IsFalse(counter.LastReadFailed);
        healthy = bad;
        foreach (var item in counter.Enumerate()) { }
        Assert.IsTrue(counter.LastReadFailed);
        healthy = 0;
        bad = 0xC0000BB9; // actual counter missing, not normal churn
        foreach (var item in counter.Enumerate()) { }
        Assert.IsTrue(counter.LastReadFailed);
    }

    [TestMethod]
    public void MoreDataRetryRecoversButPersistentApiFailureFails()
    {
        int fetches = 0;
        uint api = 0;
        uint Read(IntPtr h, uint f, ref uint size, ref uint count, IntPtr buffer)
        {
            if (api != 0) return api;
            count = 1;
            if (buffer == IntPtr.Zero) { size = 64; return Pdh.PDH_MORE_DATA; }
            if (++fetches == 1) return Pdh.PDH_MORE_DATA;
            Marshal.StructureToPtr(new PDH_FMT_COUNTERVALUE_ITEM_W { FmtValue = new() { CStatus = 0 } }, buffer, false);
            return 0;
        }
        using var counter = new PdhMultiCounter("dynamic", new IntPtr(1), Read) { TolerateInstanceChurn = true };
        foreach (var item in counter.Enumerate()) { }
        Assert.IsFalse(counter.LastReadFailed);
        Assert.AreEqual(2, fetches);
        api = Pdh.PDH_NO_DATA;
        foreach (var item in counter.Enumerate()) { }
        Assert.IsTrue(counter.LastApiReadFailed);
        api = 5;
        foreach (var item in counter.Enumerate()) { }
        Assert.IsTrue(counter.LastReadFailed);
        Assert.AreEqual(5u, counter.LastNativeStatus);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmptyWildcardAfterWarmupIsARequiredReadFailure(bool withBuffer)
    {
        uint Read(IntPtr h, uint f, ref uint size, ref uint count, IntPtr buffer)
        {
            count = 0;
            if (withBuffer && buffer == IntPtr.Zero) { size = 64; return Pdh.PDH_MORE_DATA; }
            return 0;
        }
        using var counter = new PdhMultiCounter("empty", new IntPtr(1), Read) { TolerateInstanceChurn = true };
        foreach (var item in counter.Enumerate()) { }
        Assert.IsFalse(counter.LastReadFailed);
        foreach (var item in counter.Enumerate()) { }
        Assert.IsTrue(counter.LastReadFailed);
        Assert.AreEqual(Pdh.PDH_NO_DATA, counter.LastNativeStatus);
    }

    [TestMethod]
    public async Task VolumeEnumerationExceptionRetainsTypeUntilSuccessfulRefresh()
    {
        using var volumes = new VolumeProvider { ReadDriveNamesForTest = () => throw new IOException() };
        await volumes.RefreshForTest();
        volumes.Sample(TimeSpan.Zero);
        StringAssert.Contains(volumes.LastFailureReason!, "drive enumeration/refresh (Unknown)");
        StringAssert.Contains(volumes.LastFailureReason!, "IOException");
        volumes.ReadDriveNamesForTest = () => ["C:\\"];
        volumes.ReadVolumeForTest = _ => new(new VolumeSnapshot { DriveLetter = "C:", Kind = VolumeKind.Fixed }, true, 5);
        await volumes.RefreshForTest();
        volumes.Sample(TimeSpan.Zero);
        StringAssert.Contains(volumes.LastFailureReason!, "IOException"); // first cause of this streak
    }

    [TestMethod]
    public void CollectFailureRemainsFailureAfterWarmup()
    {
        var query = (PdhQuery)Activator.CreateInstance(typeof(PdhQuery), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [new SafePdhQueryHandle(IntPtr.Zero)], culture: null)!;
        using (query) Assert.IsFalse(query.Collect());
        Assert.AreEqual(PdhReadStatus.WarmingUp, PdhReadHealth.ClassifyApi(Pdh.PDH_NO_DATA, true));
        Assert.AreEqual(PdhReadStatus.Failed, PdhReadHealth.ClassifyApi(Pdh.PDH_NO_DATA, false));
    }

    [TestMethod]
    [DataRow(Pdh.PDH_CALC_NEGATIVE_DENOMINATOR, false)]
    [DataRow(0x800007D7u, true)]
    [DataRow(0x800007D8u, true)]
    [DataRow(Pdh.PDH_NO_DATA, true)]
    [DataRow(0xC0000BB9u, true)]
    public void CpuClockCalculationFailureHasBaseClockAlternateButMissingCounterFails(uint status, bool failed)
    {
        uint Read(IntPtr h, uint f, ref uint size, ref uint count, IntPtr buffer) => status;
        using var counter = new PdhMultiCounter("clock", new IntPtr(1), Read);
        foreach (var item in counter.Enumerate()) { } // end warmup
        using var provider = new CpuProvider();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var query = (PdhQuery)Activator.CreateInstance(typeof(PdhQuery), flags, null, [new SafePdhQueryHandle(IntPtr.Zero)], null)!;
        typeof(CpuProvider).GetField("_pdhQuery", flags)!.SetValue(provider, query);
        typeof(CpuProvider).GetField("_perCoreClockCounter", flags)!.SetValue(provider, counter);
        typeof(CpuProvider).GetField("_baseClockMhz", flags)!.SetValue(provider, 3600.0);
        typeof(CpuProvider).GetMethod("SamplePerCoreClockMhz", flags)!.Invoke(provider, null);
        Assert.AreEqual(failed, provider.LastSampleFailed);
    }

    [TestMethod]
    public async Task VolumeDiagnosticsAreLoggedOncePerStreakAndReadOnlyAtStart()
    {
        using var volumes = new VolumeProvider { ReadDriveNamesForTest = () => ["Z:\\"] };
        volumes.ReadVolumeForTest = _ => new(new VolumeSnapshot { DriveLetter = "Z:", Kind = VolumeKind.Network }, true, 1234, typeof(IOException));
        await volumes.RefreshForTest();
        volumes.Sample(TimeSpan.Zero);
        StringAssert.Contains(volumes.LastFailureReason!, "Z: (Network): Win32=1234, exception=IOException");
        var provider = new DiagnosticCpu(volumes.LastFailureReason!);
        var logs = new ConcurrentQueue<string>();
        var hub = MetricsHub.CreateForTest(new MetricsHubOptions { FastInterval = TimeSpan.FromMilliseconds(5),
            ProcessInterval = TimeSpan.FromHours(1), ThermalInterval = TimeSpan.FromHours(1), VolumeInterval = TimeSpan.FromHours(1),
            Log = (s, _) => logs.Enqueue(s) }, cpu: provider);
        hub.Start();
        try { Assert.IsTrue(provider.Reached.Wait(TimeSpan.FromSeconds(10))); }
        finally { await hub.DisposeAsync(); }
        Assert.AreEqual(1, provider.ReasonReads);
        Assert.AreEqual(1, logs.Count(s => s.Contains("Z: (Network): Win32=1234")));
        volumes.ReadVolumeForTest = _ => new(new VolumeSnapshot { DriveLetter = "Z:" }, false);
        await volumes.RefreshForTest();
        volumes.Sample(TimeSpan.Zero);
        Assert.IsNull(volumes.LastFailureReason);
    }

    private sealed class DiagnosticCpu(string reason) : IMetricProvider<CpuSnapshot>
    {
        public string Name => "diagnostic";
        public bool IsAvailable => true;
        public bool LastSampleFailed => true;
        public int ReasonReads;
        private int _samples;
        public ManualResetEventSlim Reached { get; } = new();
        public string LastFailureReason { get { Interlocked.Increment(ref ReasonReads); return reason; } }
        public void Initialize() { }
        public CpuSnapshot Sample(TimeSpan elapsed) { if (++_samples >= 5) Reached.Set(); return CpuSnapshot.Empty; }
        public void Dispose() => Reached.Dispose();
    }
}
