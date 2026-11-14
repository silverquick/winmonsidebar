using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Windows.Native;
using Monitor.Windows.Providers;

namespace Monitor.App.Tests;

[TestClass]
public sealed class PdhReadHealthTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Disk_InvalidTotalUsesHealthyDeviceAlternate_ButInvalidDeviceFails(bool deviceFailed)
    {
        IntPtr totalName = Marshal.StringToHGlobalUni("_Total");
        IntPtr deviceName = Marshal.StringToHGlobalUni("0 C:");
        try
        {
            using var read = DiskCounter(10, deviceFailed, totalName, deviceName);
            using var write = DiskCounter(20, deviceFailed, totalName, deviceName);
            using var busy = DiskCounter(30, deviceFailed, totalName, deviceName);
            using var provider = new DiskProvider();
            void Set(string name, object value) => typeof(DiskProvider).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, value);
            Set("_readCounter", read);
            Set("_writeCounter", write);
            Set("_busyCounter", busy);
            provider.ReadPdhRates(collect: false); // Initial no-data items are warmup.
            var result = provider.ReadPdhRates(collect: false);
            Assert.AreEqual(deviceFailed, provider.LastSampleFailed);
            Assert.IsNull(result.Total, "An invalid _Total must not be published as zero.");
            if (!deviceFailed)
            {
                Assert.AreEqual(10.0, result.ByDrive[0].Read);
                Assert.AreEqual(20.0, result.ByDrive[0].Write);
                Assert.AreEqual(30.0, result.ByDrive[0].Busy);
            }
        }
        finally { Marshal.FreeHGlobal(totalName); Marshal.FreeHGlobal(deviceName); }
    }

    [TestMethod]
    public void Cpu_ClockErrorUsesValidBaseClockAlternate_AbsentOptionalCounterIsNormal()
    {
        uint Read(IntPtr handle, uint format, out uint type, out PDH_FMT_COUNTERVALUE value)
        {
            type = 0;
            value = default;
            return Pdh.PDH_INVALID_DATA;
        }
        var counter = new PdhCounter("clock", new IntPtr(1), Read);
        counter.GetDouble(); // Warmup is over before exercising a persistent error.
        using var provider = new CpuProvider();
        void Set(string name, object? value) => typeof(CpuProvider).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, value);
        var sampleClock = typeof(CpuProvider).GetMethod("SampleCurrentClockMhz", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Set("_performancePercentCounter", counter);
        Set("_baseClockMhz", 3600.0);
        Assert.AreEqual(3600.0, sampleClock.Invoke(provider, null));
        Assert.IsFalse(provider.LastSampleFailed);
        Set("_baseClockMhz", 0.0);
        sampleClock.Invoke(provider, null);
        Assert.IsTrue(provider.LastSampleFailed, "A required read without a valid alternate is failed.");
        using var absent = new CpuProvider();
        sampleClock.Invoke(absent, null);
        Assert.IsFalse(absent.LastSampleFailed);
    }

    [TestMethod]
    public void Scalar_InitialNoDataIsWarmup_RepeatedErrorFails_AndSuccessRecovers()
    {
        uint status = Pdh.PDH_INVALID_DATA;
        uint itemStatus = 0;
        uint Read(IntPtr handle, uint format, out uint type, out PDH_FMT_COUNTERVALUE value)
        {
            type = 0;
            value = new() { CStatus = itemStatus, doubleValue = 42 };
            return status;
        }
        var counter = new PdhCounter("test", new IntPtr(1), Read);
        Assert.AreEqual(0.0, counter.GetDouble());
        Assert.AreEqual(PdhReadStatus.WarmingUp, counter.LastReadStatus);
        Assert.IsFalse(counter.LastReadFailed);
        counter.GetDouble();
        Assert.IsTrue(counter.LastReadFailed);
        status = 0;
        itemStatus = 1;
        Assert.AreEqual(42.0, counter.GetDouble());
        Assert.IsTrue(counter.HasValue);
        Assert.IsFalse(counter.LastReadFailed);
        itemStatus = Pdh.PDH_CSTATUS_INVALID_DATA; // PDH_CSTATUS_INVALID_DATA (different from the API's PDH_INVALID_DATA).
        counter.GetDouble();
        Assert.IsTrue(counter.LastReadFailed);
        itemStatus = 0;
        counter.GetDouble();
        Assert.IsFalse(counter.LastReadFailed);
        status = 1; // API error code 1 is not the per-value CSTATUS_NEW_DATA success code.
        counter.GetDouble();
        Assert.IsTrue(counter.LastReadFailed);
    }

    [TestMethod]
    public void Scalar_FirstReadAfterMultipleCollections_IsNotMistakenForWarmup()
    {
        var query = (PdhQuery)Activator.CreateInstance(typeof(PdhQuery), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [new SafePdhQueryHandle(IntPtr.Zero)], culture: null)!;
        using (query)
        {
            typeof(PdhQuery).GetProperty("CollectionCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(query, 2);
            uint Read(IntPtr handle, uint format, out uint type, out PDH_FMT_COUNTERVALUE value)
            {
                type = 0;
                value = default;
                return Pdh.PDH_NO_DATA;
            }
            var counter = new PdhCounter("test", new IntPtr(1), Read) { Query = query };
            counter.GetDouble();
            Assert.IsTrue(counter.LastReadFailed);
        }
    }

    [TestMethod]
    public void Wildcard_NativeAndPerItemErrorsAreVisible_WithoutFabricatingHealthyZeros()
    {
        uint apiStatus = Pdh.PDH_NO_DATA;
        uint itemStatus = Pdh.PDH_INVALID_DATA;
        uint Read(IntPtr handle, uint format, ref uint size, ref uint count, IntPtr buffer)
        {
            if (apiStatus != 0) return apiStatus;
            count = 1;
            uint required = (uint)Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            if (buffer == IntPtr.Zero || size < required) { size = required; return Pdh.PDH_MORE_DATA; }
            Marshal.StructureToPtr(new PDH_FMT_COUNTERVALUE_ITEM_W
            {
                szName = IntPtr.Zero, FmtValue = new() { CStatus = itemStatus, doubleValue = 99 },
            }, buffer, false);
            return 0;
        }
        using var counter = new PdhMultiCounter("test", new IntPtr(1), Read);
        foreach (var item in counter.Enumerate()) { }
        Assert.AreEqual(PdhReadStatus.WarmingUp, counter.LastReadStatus);
        foreach (var item in counter.Enumerate()) { }
        Assert.IsTrue(counter.LastReadFailed);
        apiStatus = 0;
        foreach (var item in counter.Enumerate()) Assert.AreEqual(0.0, item.Value);
        Assert.IsTrue(counter.LastReadFailed, "Error item CStatus must reach the provider even when API status is success.");
        itemStatus = 0;
        foreach (var item in counter.Enumerate()) Assert.AreEqual(99.0, item.Value);
        Assert.IsFalse(counter.LastReadFailed);
        apiStatus = 5;
        foreach (var item in counter.Enumerate()) { }
        Assert.IsTrue(counter.LastReadFailed);
    }

    [TestMethod]
    [DataRow(true, PdhReadStatus.Failed, false, true)]
    [DataRow(false, PdhReadStatus.Failed, false, false)]
    [DataRow(true, PdhReadStatus.Failed, true, false)]
    [DataRow(true, PdhReadStatus.WarmingUp, false, false)]
    [DataRow(true, PdhReadStatus.Success, false, false)]
    public void ProviderDecision_RequiredFailureVersusAbsentOptionalOrValidAlternate(bool present, PdhReadStatus status, bool alternate, bool expected) =>
        Assert.AreEqual(expected, PdhReadHealth.RequiredFailed(present, status, alternate));

    private static PdhMultiCounter DiskCounter(double value, bool deviceFailed, IntPtr totalName, IntPtr deviceName)
    {
        uint Read(IntPtr handle, uint format, ref uint size, ref uint count, IntPtr buffer)
        {
            uint itemSize = (uint)Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            count = 2;
            if (buffer == IntPtr.Zero || size < itemSize * 2) { size = itemSize * 2; return Pdh.PDH_MORE_DATA; }
            // Native names are test-owned and allocated once outside the read loop.
            WriteItem(buffer, totalName, Pdh.PDH_INVALID_DATA);
            WriteItem(IntPtr.Add(buffer, (int)itemSize), deviceName, deviceFailed ? Pdh.PDH_INVALID_DATA : 0);
            return 0;
        }
        void WriteItem(IntPtr buffer, IntPtr namePointer, uint status)
        {
            Marshal.StructureToPtr(new PDH_FMT_COUNTERVALUE_ITEM_W
            {
                szName = namePointer, FmtValue = new() { CStatus = status, doubleValue = value },
            }, buffer, false);
        }
        return new PdhMultiCounter("disk", new IntPtr(1), Read);
    }

}
