using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core.Models;
using Monitor.Windows.Native;
using Monitor.Windows.Providers;

namespace Monitor.App.Tests;

[TestClass]
public sealed class PdhRegressionTests
{
    [TestMethod]
    public void PdhMultiCounter_BufferReuseAndReallocationOnSizeChange()
    {
        var currentItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 25.5, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 50.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,2", 75.25, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        int getArrayCallCount = 0;

        uint FakeGetArray(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer)
        {
            getArrayCallCount++;
            (IntPtr nativeBuf, uint neededSize) = CreateNativeBuffer(currentItems);
            try
            {
                if (itemBuffer == IntPtr.Zero || lpdwBufferSize < neededSize)
                {
                    lpdwBufferSize = neededSize;
                    return Pdh.PDH_MORE_DATA;
                }

                unsafe
                {
                    Buffer.MemoryCopy((void*)nativeBuf, (void*)itemBuffer, lpdwBufferSize, neededSize);
                }

                lpdwItemCount = (uint)currentItems.Length;
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuf);
            }
        }

        using var multiCounter = new PdhMultiCounter(@"\Processor Information(*)\% Processor Utility", new IntPtr(0x1234), FakeGetArray);

        // 1. First sample: 3 items (initial size query + initial fill = 2 calls to native API)
        var itemsList1 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            itemsList1.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(3, itemsList1.Count);
        Assert.AreEqual("0,0", itemsList1[0].Name);
        Assert.AreEqual(25.5, itemsList1[0].Value, 0.001);
        Assert.AreEqual("0,1", itemsList1[1].Name);
        Assert.AreEqual(50.0, itemsList1[1].Value, 0.001);
        Assert.AreEqual("0,2", itemsList1[2].Name);
        Assert.AreEqual(75.25, itemsList1[2].Value, 0.001);
        Assert.AreEqual(2, getArrayCallCount, "First sample requires 2 calls (size query + fetch)");

        // 2. Second sample: 2 items (smaller size, existing buffer is reused directly in 1 call!)
        currentItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 12.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 34.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        var itemsList2 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            itemsList2.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(2, itemsList2.Count);
        Assert.AreEqual("0,0", itemsList2[0].Name);
        Assert.AreEqual(12.0, itemsList2[0].Value, 0.001);
        Assert.AreEqual("0,1", itemsList2[1].Name);
        Assert.AreEqual(34.0, itemsList2[1].Value, 0.001);
        Assert.AreEqual(3, getArrayCallCount, "Second sample with smaller/equal size must reuse buffer in 1 call");

        // 3. Third sample: 5 items (larger size, returns PDH_MORE_DATA, reallocates buffer and fetches)
        currentItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 10.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 20.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,2", 30.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,3", 40.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,4", 50.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        var itemsList3 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            itemsList3.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(5, itemsList3.Count);
        Assert.AreEqual("0,4", itemsList3[4].Name);
        Assert.AreEqual(50.0, itemsList3[4].Value, 0.001);
        Assert.AreEqual(5, getArrayCallCount, "Third sample with larger size must reallocate (1 failed call + 1 reallocated fetch = 2 calls)");

        // 4. GetValues backward compatibility
        IReadOnlyList<PdhCounterItem> legacyValues = multiCounter.GetValues();
        Assert.AreEqual(5, legacyValues.Count);
        Assert.AreEqual("0,0", legacyValues[0].InstanceName);
        Assert.AreEqual(10.0, legacyValues[0].Value, 0.001);
        Assert.AreEqual("0,4", legacyValues[4].InstanceName);
        Assert.AreEqual(50.0, legacyValues[4].Value, 0.001);
    }

    [TestMethod]
    public void PdhCounterEnumerator_HandlesErrorCStatusAndNullNames()
    {
        var rawItems = new (string Name, double Value, uint CStatus)[]
        {
            ("valid", 42.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("invalid", 99.0, Pdh.PDH_INVALID_DATA),
            ("nodata", 88.0, Pdh.PDH_NO_DATA),
            ("negdenom", 77.0, Pdh.PDH_CALC_NEGATIVE_DENOMINATOR),
        };

        (IntPtr buffer, _) = CreateNativeBuffer(rawItems);
        try
        {
            var enumerator = new PdhCounterEnumerator(buffer, rawItems.Length);
            var results = new List<(string Name, double Value)>();
            while (enumerator.MoveNext())
            {
                results.Add((enumerator.Current.InstanceName.ToString(), enumerator.Current.Value));
            }

            Assert.AreEqual(4, results.Count);
            Assert.AreEqual("valid", results[0].Name);
            Assert.AreEqual(42.0, results[0].Value, 0.001);

            Assert.AreEqual("invalid", results[1].Name);
            Assert.AreEqual(0.0, results[1].Value, "Error CStatus PDH_INVALID_DATA must yield 0.0");

            Assert.AreEqual("nodata", results[2].Name);
            Assert.AreEqual(0.0, results[2].Value, "Error CStatus PDH_NO_DATA must yield 0.0");

            Assert.AreEqual("negdenom", results[3].Name);
            Assert.AreEqual(0.0, results[3].Value, "Error CStatus PDH_CALC_NEGATIVE_DENOMINATOR must yield 0.0");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [TestMethod]
    public void CpuProvider_CoreInstanceOrder_MatchesExpectedNumericalSequence()
    {
        var testInstances = new[]
        {
            "_Total",
            "0,_Total",
            "1,_Total",
            "0,10",
            "0,2",
            "0,0",
            "0,1",
            "0,9",
            "0,11",
            "1,0",
            "1,1",
            "1,10",
            "invalid_name",
        };

        var parsed = new List<(string InstanceName, long SortKey)>();
        foreach (string inst in testInstances)
        {
            if (CpuProvider.TryParseCoreInstance(inst, out long sortKey))
            {
                parsed.Add((inst, sortKey));
            }
        }

        // _Total, 0,_Total, 1,_Total, invalid_name must be excluded
        Assert.AreEqual(9, parsed.Count);

        parsed.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));

        string[] expectedOrder =
        {
            "0,0",
            "0,1",
            "0,2",
            "0,9",
            "0,10",
            "0,11",
            "1,0",
            "1,1",
            "1,10",
        };

        for (int i = 0; i < expectedOrder.Length; i++)
        {
            Assert.AreEqual(expectedOrder[i], parsed[i].InstanceName, $"Instance index {i} must match numerical order");
        }

        // Specifically assert the invariant that "0,2" is before "0,10" and "0,9" is before "0,10" and "0,11" is before "1,0"
        int idx0_2 = parsed.FindIndex(x => x.InstanceName == "0,2");
        int idx0_9 = parsed.FindIndex(x => x.InstanceName == "0,9");
        int idx0_10 = parsed.FindIndex(x => x.InstanceName == "0,10");
        int idx0_11 = parsed.FindIndex(x => x.InstanceName == "0,11");
        int idx1_0 = parsed.FindIndex(x => x.InstanceName == "1,0");

        Assert.IsTrue(idx0_2 < idx0_10, "0,2 must sort before 0,10");
        Assert.IsTrue(idx0_9 < idx0_10, "0,9 must sort before 0,10");
        Assert.IsTrue(idx0_10 < idx0_11, "0,10 must sort before 0,11");
        Assert.IsTrue(idx0_11 < idx1_0, "0,11 must sort before 1,0");
    }

    [TestMethod]
    public void GpuProvider_EngineInstanceParsing_And_Aggregation()
    {
        // 1. Instance parsing tests
        Assert.IsTrue(GpuProvider.TryParseEngineInstance(
            "pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D",
            out long luid1, out ReadOnlySpan<char> engType1));
        Assert.AreEqual(0xABCDL, luid1);
        Assert.IsTrue(engType1.SequenceEqual("3D"));

        Assert.IsTrue(GpuProvider.TryParseEngineInstance(
            "pid_5678_luid_0x00000001_0x00000020_phys_0_eng_1_engtype_VideoDecode",
            out long luid2, out ReadOnlySpan<char> engType2));
        Assert.AreEqual((1L << 32) | 0x20L, luid2);
        Assert.IsTrue(engType2.SequenceEqual("VideoDecode"));

        Assert.IsTrue(GpuProvider.TryParseEngineInstance(
            "pid_9999_luid_0x00000000_0x0000ABCD_phys_0_eng_2_engtype_Security",
            out long luid3, out ReadOnlySpan<char> engType3));
        Assert.AreEqual(0xABCDL, luid3);
        Assert.IsTrue(engType3.SequenceEqual("Security"));

        Assert.IsFalse(GpuProvider.TryParseEngineInstance("invalid_string_without_luid", out _, out _));

        // 2. AdapterAccumulator aggregation tests
        var acc = new GpuProvider.AdapterAccumulator();
        acc.AddEngine("3D", 35.0);
        acc.AddEngine("3D", 25.0);
        acc.AddEngine("Copy", 15.0);
        acc.AddEngine("VideoDecode", 20.0);
        acc.AddEngine("VideoDecode", 15.0);
        acc.AddEngine("VideoEncode", 10.0);
        acc.AddEngine("VideoProcessing", 5.0);
        acc.AddEngine("Compute", 40.0);

        Assert.AreEqual(60.0, acc.Engine3D, 0.001);
        Assert.AreEqual(15.0, acc.EngineCopy, 0.001);
        Assert.AreEqual(35.0, acc.VideoDecode, 0.001);
        Assert.AreEqual(10.0, acc.VideoEncode, 0.001);
        Assert.AreEqual(5.0, acc.VideoProcessing, 0.001);
        Assert.AreEqual(40.0, acc.EngineCompute, 0.001);

        // MaxVideoTotal = max(VideoDecode, VideoEncode, VideoProcessing) = max(35, 10, 5) = 35.0
        Assert.AreEqual(35.0, acc.MaxVideoTotal(), 0.001);

        // MaxCategoryTotal = max(3D=60, Copy=15, VideoDecode=35, VideoEncode=10, VideoProcessing=5, Compute=40) = 60.0
        Assert.AreEqual(60.0, acc.MaxCategoryTotal(), 0.001);

        // Add unknown category "Security" = 80.0 -> MaxCategoryTotal becomes 80.0
        acc.AddEngine("Security", 80.0);
        Assert.AreEqual(80.0, acc.MaxCategoryTotal(), 0.001);
    }

    [TestMethod]
    public void DiskProvider_TryParseDriveNumber_ParsesCorrectly()
    {
        Assert.IsTrue(DiskProvider.TryParseDriveNumber("0 C:", out int drive0));
        Assert.AreEqual(0, drive0);

        Assert.IsTrue(DiskProvider.TryParseDriveNumber("1 D: E:", out int drive1));
        Assert.AreEqual(1, drive1);

        Assert.IsTrue(DiskProvider.TryParseDriveNumber("  12   X:  ", out int drive12));
        Assert.AreEqual(12, drive12);

        Assert.IsFalse(DiskProvider.TryParseDriveNumber("_Total", out _));
        Assert.IsFalse(DiskProvider.TryParseDriveNumber("", out _));
        Assert.IsFalse(DiskProvider.TryParseDriveNumber("HarddiskVolume1", out _));
    }

    [TestMethod]
    public void Providers_WhenPdhUnavailable_ReturnEmptyAndDoNotThrow()
    {
        // 1. PdhMultiCounter with null/invalid handle
        using var invalidMultiCounter = new PdhMultiCounter(@"\Invalid\Counter", IntPtr.Zero);
        int count = 0;
        foreach (PdhItemSpan _ in invalidMultiCounter.Enumerate())
        {
            count++;
        }
        Assert.AreEqual(0, count);
        Assert.AreEqual(0, invalidMultiCounter.GetValues().Count);

        // 2. PdhCounter with null/invalid handle
        var invalidCounter = new PdhCounter(@"\Invalid\Counter", IntPtr.Zero);
        Assert.AreEqual(0.0, invalidCounter.GetDouble());
        Assert.IsFalse(invalidCounter.HasValue);

        // 3. GpuProvider without Initialize (unavailable PDH)
        using var gpuProvider = new GpuProvider();
        GpuSnapshot gpuSnap = gpuProvider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(gpuSnap);
        Assert.AreEqual(0, gpuSnap.Adapters.Count);
        Assert.AreEqual(0.0, gpuSnap.TotalUsagePercent);

        // 4. DiskProvider without Initialize (unavailable PDH)
        using var diskProvider = new DiskProvider();
        DiskSnapshot diskSnap = diskProvider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(diskSnap);
        Assert.AreEqual(0, diskSnap.Devices.Count);
        Assert.AreEqual(0.0, diskSnap.TotalReadBytesPerSec);

        // 5. CpuProvider without Initialize (unavailable PDH)
        using var cpuProvider = new CpuProvider();
        CpuSnapshot cpuSnap = cpuProvider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(cpuSnap);
        Assert.AreEqual(0, cpuSnap.PerCoreUsagePercent.Count);
        Assert.AreEqual(0, cpuSnap.PerCoreClockMhz.Count);
    }

    private static (IntPtr Buffer, uint ByteSize) CreateNativeBuffer((string Name, double Value, uint CStatus)[] items)
    {
        int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
        int totalSize = items.Length * itemSize;
        foreach (var item in items)
        {
            totalSize += (item.Name.Length + 1) * sizeof(char);
        }

        IntPtr buffer = Marshal.AllocHGlobal(totalSize);
        int stringOffset = items.Length * itemSize;

        for (int i = 0; i < items.Length; i++)
        {
            IntPtr itemPtr = IntPtr.Add(buffer, i * itemSize);
            IntPtr namePtr = IntPtr.Add(buffer, stringOffset);

            char[] chars = (items[i].Name + "\0").ToCharArray();
            Marshal.Copy(chars, 0, namePtr, chars.Length);
            stringOffset += chars.Length * sizeof(char);

            var nativeItem = new PDH_FMT_COUNTERVALUE_ITEM_W
            {
                szName = namePtr,
                FmtValue = new PDH_FMT_COUNTERVALUE
                {
                    CStatus = items[i].CStatus,
                    doubleValue = items[i].Value,
                },
            };

            Marshal.StructureToPtr(nativeItem, itemPtr, false);
        }

        return (buffer, (uint)totalSize);
    }
}
