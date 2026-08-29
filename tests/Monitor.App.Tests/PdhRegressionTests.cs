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
    public void TestA_PdhMultiCounter_Reallocation_NeverTrustsReturnedBufferSizeOnInsufficientBuffer()
    {
        var currentItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 25.5, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 50.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        var callLog = new List<(IntPtr Buffer, uint InBufferSize, uint OutBufferSize, uint ReturnStatus)>();

        uint FakeGetArray(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer)
        {
            uint inSize = lpdwBufferSize;
            uint neededSize = CalculateNativeBufferSize(currentItems);

            // 1. NULL/0 probe: reliable neededSize query
            if (itemBuffer == IntPtr.Zero && inSize == 0)
            {
                lpdwBufferSize = neededSize;
                callLog.Add((itemBuffer, inSize, lpdwBufferSize, Pdh.PDH_MORE_DATA));
                return Pdh.PDH_MORE_DATA;
            }

            // 2. Insufficient non-zero buffer: return PDH_MORE_DATA with UNTRUSTED/POISON size
            if (itemBuffer != IntPtr.Zero && inSize < neededSize)
            {
                lpdwBufferSize = 1; // Poison value! Must NOT be used for allocation
                callLog.Add((itemBuffer, inSize, lpdwBufferSize, Pdh.PDH_MORE_DATA));
                return Pdh.PDH_MORE_DATA;
            }

            // 3. Sufficient buffer
            if (itemBuffer != IntPtr.Zero && inSize >= neededSize)
            {
                WriteNativeBuffer(itemBuffer, currentItems);
                lpdwItemCount = (uint)currentItems.Length;
                callLog.Add((itemBuffer, inSize, lpdwBufferSize, 0));
                return 0;
            }

            callLog.Add((itemBuffer, inSize, lpdwBufferSize, Pdh.PDH_INVALID_ARGUMENT));
            return Pdh.PDH_INVALID_ARGUMENT;
        }

        using var multiCounter = new PdhMultiCounter(@"\Processor Information(*)\% Processor Utility", new IntPtr(0x1234), FakeGetArray);

        // 1. Initial sample: 2 items (2 calls: NULL/0 probe -> allocated fetch)
        var list1 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            list1.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(2, list1.Count);
        Assert.AreEqual("0,0", list1[0].Name);
        Assert.AreEqual(25.5, list1[0].Value, 0.001);
        Assert.AreEqual("0,1", list1[1].Name);
        Assert.AreEqual(50.0, list1[1].Value, 0.001);
        Assert.AreEqual(2, callLog.Count);
        Assert.AreEqual(IntPtr.Zero, callLog[0].Buffer);
        Assert.AreEqual(0u, callLog[0].InBufferSize);
        Assert.AreNotEqual(IntPtr.Zero, callLog[1].Buffer);

        // 2. Growth sample: 5 items (3 calls: reused fetch [fails with untrusted size] -> NULL/0 probe [reliable] -> reallocated fetch [success])
        currentItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 10.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 20.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,2", 30.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,3", 40.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,4", 50.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        var list2 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            list2.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(5, list2.Count);
        Assert.AreEqual("0,4", list2[4].Name);
        Assert.AreEqual(50.0, list2[4].Value, 0.001);

        // Assert native call sequence for growth:
        Assert.AreEqual(5, callLog.Count, "Growth sample must take exactly 3 native calls (reused fetch + NULL/0 probe + fetch)");
        Assert.AreNotEqual(IntPtr.Zero, callLog[2].Buffer, "Call 3 is reused buffer fetch");
        Assert.AreEqual(1u, callLog[2].OutBufferSize, "Call 3 fake returned poison size 1");
        Assert.AreEqual(IntPtr.Zero, callLog[3].Buffer, "Call 4 must strictly probe with NULL/0");
        Assert.AreEqual(0u, callLog[3].InBufferSize, "Call 4 must pass inBufferSize == 0");
        Assert.AreNotEqual(IntPtr.Zero, callLog[4].Buffer, "Call 5 is reallocated fetch");

        // 3. Steady state sample: 5 items (1 call fast path)
        var list3 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            list3.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(5, list3.Count);
        Assert.AreEqual(6, callLog.Count, "Subsequent steady state must take 1 native call");

        // 4. GetValues backward compatibility
        IReadOnlyList<PdhCounterItem> legacyValues = multiCounter.GetValues();
        Assert.AreEqual(5, legacyValues.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual(currentItems[i].Name, legacyValues[i].InstanceName);
            Assert.AreEqual(currentItems[i].Value, legacyValues[i].Value, 0.001);
            Assert.AreEqual(list3[i].Name, legacyValues[i].InstanceName);
            Assert.AreEqual(list3[i].Value, legacyValues[i].Value, 0.001);
        }
    }

    [TestMethod]
    public void TestB_PdhMultiCounter_Reallocation_HandlesPdhInvalidArgumentOnInsufficientBuffer()
    {
        var currentItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 25.5, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 50.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        var callLog = new List<(IntPtr Buffer, uint InBufferSize, uint ReturnStatus)>();

        uint FakeGetArray(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer)
        {
            uint inSize = lpdwBufferSize;
            uint neededSize = CalculateNativeBufferSize(currentItems);

            if (itemBuffer == IntPtr.Zero && inSize == 0)
            {
                lpdwBufferSize = neededSize;
                callLog.Add((itemBuffer, inSize, Pdh.PDH_MORE_DATA));
                return Pdh.PDH_MORE_DATA;
            }

            if (itemBuffer != IntPtr.Zero && inSize < neededSize)
            {
                // Return PDH_INVALID_ARGUMENT on insufficient buffer (Windows compatibility behavior)
                callLog.Add((itemBuffer, inSize, Pdh.PDH_INVALID_ARGUMENT));
                return Pdh.PDH_INVALID_ARGUMENT;
            }

            if (itemBuffer != IntPtr.Zero && inSize >= neededSize)
            {
                WriteNativeBuffer(itemBuffer, currentItems);
                lpdwItemCount = (uint)currentItems.Length;
                callLog.Add((itemBuffer, inSize, 0));
                return 0;
            }

            callLog.Add((itemBuffer, inSize, Pdh.PDH_INVALID_ARGUMENT));
            return Pdh.PDH_INVALID_ARGUMENT;
        }

        using var multiCounter = new PdhMultiCounter(@"\Processor Information(*)\% Processor Utility", new IntPtr(0x1234), FakeGetArray);

        // 1. Initial 2 items
        var list1 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            list1.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(2, list1.Count);
        Assert.AreEqual(2, callLog.Count);

        // 2. Growth to 4 items -> triggers PDH_INVALID_ARGUMENT on reused buffer -> probes NULL/0 -> reallocates
        currentItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 11.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 22.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,2", 33.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,3", 44.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        var list2 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            list2.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(4, list2.Count);
        Assert.AreEqual("0,3", list2[3].Name);
        Assert.AreEqual(44.0, list2[3].Value, 0.001);

        Assert.AreEqual(5, callLog.Count);
        Assert.AreEqual(Pdh.PDH_INVALID_ARGUMENT, callLog[2].ReturnStatus);
        Assert.AreEqual(IntPtr.Zero, callLog[3].Buffer);
        Assert.AreEqual(0u, callLog[3].InBufferSize);
        Assert.AreEqual(0u, callLog[4].ReturnStatus);

        // 3. Subsequent sample uses new capacity in 1 call
        var list3 = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in multiCounter.Enumerate())
        {
            list3.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(4, list3.Count);
        Assert.AreEqual(6, callLog.Count);
    }

    [TestMethod]
    public void TestC_PdhMultiCounter_Reallocation_HandlesGrowthRaceBetweenProbeAndFetch()
    {
        var smallItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 1.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 2.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };
        var mediumItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 10.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 20.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,2", 30.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };
        var largeItems = new (string Name, double Value, uint CStatus)[]
        {
            ("0,0", 100.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,1", 200.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,2", 300.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,3", 400.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("0,4", 500.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        var currentItems = smallItems;
        var callLog = new List<(IntPtr Buffer, uint InBufferSize, uint ReturnStatus)>();
        bool raceTriggered = false;

        uint RaceGetArray(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer)
        {
            uint inSize = lpdwBufferSize;
            uint neededSize = CalculateNativeBufferSize(currentItems);

            if (itemBuffer == IntPtr.Zero && inSize == 0)
            {
                // After first probe returns medium size, simulate race: instances increase to large before fetch!
                if (!raceTriggered)
                {
                    raceTriggered = true;
                    lpdwBufferSize = CalculateNativeBufferSize(mediumItems);
                    currentItems = largeItems; // Race: grown to large before fetch!
                    callLog.Add((itemBuffer, inSize, Pdh.PDH_MORE_DATA));
                    return Pdh.PDH_MORE_DATA;
                }

                lpdwBufferSize = neededSize;
                callLog.Add((itemBuffer, inSize, Pdh.PDH_MORE_DATA));
                return Pdh.PDH_MORE_DATA;
            }

            if (itemBuffer != IntPtr.Zero && inSize < neededSize)
            {
                lpdwBufferSize = 1; // poison
                callLog.Add((itemBuffer, inSize, Pdh.PDH_MORE_DATA));
                return Pdh.PDH_MORE_DATA;
            }

            if (itemBuffer != IntPtr.Zero && inSize >= neededSize)
            {
                WriteNativeBuffer(itemBuffer, currentItems);
                lpdwItemCount = (uint)currentItems.Length;
                callLog.Add((itemBuffer, inSize, 0));
                return 0;
            }

            callLog.Add((itemBuffer, inSize, Pdh.PDH_INVALID_ARGUMENT));
            return Pdh.PDH_INVALID_ARGUMENT;
        }

        using var raceCounter = new PdhMultiCounter(@"\Processor Information(*)\% Processor Utility", new IntPtr(0x5678), RaceGetArray);

        var listRace = new List<(string Name, double Value)>();
        foreach (PdhItemSpan item in raceCounter.Enumerate())
        {
            listRace.Add((item.InstanceName.ToString(), item.Value));
        }

        Assert.AreEqual(5, listRace.Count);
        Assert.AreEqual("0,4", listRace[4].Name);
        Assert.AreEqual(500.0, listRace[4].Value, 0.001);
    }

    [TestMethod]
    public void TestD_PdhMultiCounter_RetryLimit_ZeroSize_And_DisposalSafety()
    {
        // 1. Retry limit: always failing probe/fetch
        int callCount = 0;
        uint FailingGetArray(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer)
        {
            callCount++;
            if (itemBuffer == IntPtr.Zero)
            {
                lpdwBufferSize = 100;
                return Pdh.PDH_MORE_DATA;
            }
            return Pdh.PDH_MORE_DATA; // always fail fetch
        }

        using (var failingCounter = new PdhMultiCounter(@"\test", new IntPtr(0x111), FailingGetArray))
        {
            int count = 0;
            foreach (PdhItemSpan _ in failingCounter.Enumerate()) count++;
            Assert.AreEqual(0, count);
            Assert.IsTrue(callCount <= 7, "Must terminate within bounded retry attempts");
        }

        // 2. Zero size probe
        uint ZeroSizeGetArray(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer)
        {
            lpdwBufferSize = 0;
            return Pdh.PDH_MORE_DATA;
        }

        using (var zeroCounter = new PdhMultiCounter(@"\test", new IntPtr(0x222), ZeroSizeGetArray))
        {
            int count = 0;
            foreach (PdhItemSpan _ in zeroCounter.Enumerate()) count++;
            Assert.AreEqual(0, count);
        }

        // 3. Double dispose safety
        var disposableCounter = new PdhMultiCounter(@"\test", new IntPtr(0x333), FailingGetArray);
        disposableCounter.Dispose();
        disposableCounter.Dispose(); // must not throw

        // 4. PdhQuery dispose tracking
        using (var query = PdhQuery.TryCreate())
        {
            // Query disposal must not throw
        }
    }

    [TestMethod]
    public void TestE_PdhNativeLayout_AbiInvariants_And_NullNameHandling()
    {
        // 1. ABI sizes in x64
        Assert.AreEqual(16, Marshal.SizeOf<PDH_FMT_COUNTERVALUE>());
        Assert.AreEqual(24, Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>());

        // 2. Exact offset calculation check
        var rawItems = new (string Name, double Value, uint CStatus)[]
        {
            ("first_inst", 10.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("second_inst", 20.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("third_inst", 30.0, Pdh.PDH_CSTATUS_VALID_DATA),
        };

        uint bufferSize = CalculateNativeBufferSize(rawItems);
        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            WriteNativeBuffer(buffer, rawItems);

            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            int expectedOffset0 = rawItems.Length * itemSize; // 3 * 24 = 72
            int expectedOffset1 = expectedOffset0 + ("first_inst\0".Length * sizeof(char));
            int expectedOffset2 = expectedOffset1 + ("second_inst\0".Length * sizeof(char));

            PDH_FMT_COUNTERVALUE_ITEM_W item0 = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buffer);
            PDH_FMT_COUNTERVALUE_ITEM_W item1 = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(IntPtr.Add(buffer, itemSize));
            PDH_FMT_COUNTERVALUE_ITEM_W item2 = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(IntPtr.Add(buffer, itemSize * 2));

            Assert.AreEqual(IntPtr.Add(buffer, expectedOffset0), item0.szName, "First szName points to end of item array");
            Assert.AreEqual(IntPtr.Add(buffer, expectedOffset1), item1.szName, "Second szName points after first NUL-terminated string");
            Assert.AreEqual(IntPtr.Add(buffer, expectedOffset2), item2.szName, "Third szName points after second NUL-terminated string");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // 3. Null szName handling (item with szName == IntPtr.Zero)
        int singleItemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
        IntPtr nullNameBuffer = Marshal.AllocHGlobal(singleItemSize * 2);
        try
        {
            var itemValid = new PDH_FMT_COUNTERVALUE_ITEM_W
            {
                szName = IntPtr.Zero, // Explicitly NULL!
                FmtValue = new PDH_FMT_COUNTERVALUE
                {
                    CStatus = Pdh.PDH_CSTATUS_VALID_DATA,
                    doubleValue = 99.5,
                },
            };
            Marshal.StructureToPtr(itemValid, nullNameBuffer, false);

            var enumerator = new PdhCounterEnumerator(nullNameBuffer, 1);
            Assert.IsTrue(enumerator.MoveNext());
            Assert.IsTrue(enumerator.Current.InstanceName.IsEmpty, "NULL szName must yield empty ReadOnlySpan<char>");
            Assert.AreEqual(99.5, enumerator.Current.Value, 0.001);
            Assert.IsFalse(enumerator.MoveNext());
        }
        finally
        {
            Marshal.FreeHGlobal(nullNameBuffer);
        }
    }

    [TestMethod]
    public void PdhCounterEnumerator_HandlesErrorCStatus()
    {
        var rawItems = new (string Name, double Value, uint CStatus)[]
        {
            ("valid", 42.0, Pdh.PDH_CSTATUS_VALID_DATA),
            ("invalid", 99.0, Pdh.PDH_INVALID_DATA),
            ("nodata", 88.0, Pdh.PDH_NO_DATA),
            ("negdenom", 77.0, Pdh.PDH_CALC_NEGATIVE_DENOMINATOR),
        };

        uint neededSize = CalculateNativeBufferSize(rawItems);
        IntPtr buffer = Marshal.AllocHGlobal((int)neededSize);
        try
        {
            WriteNativeBuffer(buffer, rawItems);
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

    private static uint CalculateNativeBufferSize((string Name, double Value, uint CStatus)[] items)
    {
        int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
        int totalSize = items.Length * itemSize;
        foreach (var item in items)
        {
            totalSize += (item.Name.Length + 1) * sizeof(char);
        }

        return (uint)totalSize;
    }

    private static void WriteNativeBuffer(IntPtr destination, (string Name, double Value, uint CStatus)[] items)
    {
        int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
        int stringOffset = items.Length * itemSize;

        for (int i = 0; i < items.Length; i++)
        {
            IntPtr itemPtr = IntPtr.Add(destination, i * itemSize);
            IntPtr namePtr = IntPtr.Add(destination, stringOffset);

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
    }
}
