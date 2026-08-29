using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Windows.Providers;

namespace Monitor.Windows.Tests;

[TestClass]
public sealed class CpuCoreInstanceTests
{
    [TestMethod]
    public void TryParseCoreInstance_ValidGroupAndProcessor_ReturnsExpectedSortKeyAndOrder()
    {
        var instances = new[] { "0,0", "0,1", "0,2", "0,10", "0,15", "1,0", "1,3" };
        var keys = new List<long>();

        foreach (var inst in instances)
        {
            bool success = CpuProvider.TryParseCoreInstance(inst, out long key);
            Assert.IsTrue(success, $"Failed to parse valid instance '{inst}'");
            keys.Add(key);
        }

        // SortKeys must be strictly increasing for the sorted input list
        for (int i = 0; i < keys.Count - 1; i++)
        {
            Assert.IsTrue(keys[i] < keys[i + 1], $"SortKey for '{instances[i]}' ({keys[i]}) should be < '{instances[i + 1]}' ({keys[i + 1]})");
        }

        // Specific relative ordering checks
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0,10", out long key0_10));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0,2", out long key0_2));
        Assert.IsTrue(key0_10 > key0_2, "SortKey for '0,10' must be greater than '0,2'");

        Assert.IsTrue(CpuProvider.TryParseCoreInstance("1,0", out long key1_0));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0,15", out long key0_15));
        Assert.IsTrue(key1_0 > key0_15, "SortKey for '1,0' must be greater than '0,15'");

        // Unshuffled vs sorted ordering
        var unordered = new[] { "1,3", "0,10", "0,1", "1,0", "0,0", "0,2", "0,15" };
        var sortedParsed = unordered
            .Select(name =>
            {
                bool ok = CpuProvider.TryParseCoreInstance(name, out long k);
                return (Name: name, Key: k, Success: ok);
            })
            .OrderBy(x => x.Key)
            .Select(x => x.Name)
            .ToArray();

        CollectionAssert.AreEqual(instances, sortedParsed);
    }

    [TestMethod]
    public void TryParseCoreInstance_SingleNumberWithoutComma_ParsesCorrectly()
    {
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0", out long key0));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("2", out long key2));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("10", out long key10));

        Assert.AreEqual(0L, key0);
        Assert.AreEqual(2L, key2);
        Assert.AreEqual(10L, key10);
        Assert.IsTrue(key10 > key2);
        Assert.IsTrue(key2 > key0);
    }

    [TestMethod]
    public void TryParseCoreInstance_TotalInstances_ReturnsFalse()
    {
        var totalInstances = new[]
        {
            "_Total",
            "_total",
            "_TOTAL",
            "0,_Total",
            "1,_total",
            "0,0,_Total",
        };

        foreach (var inst in totalInstances)
        {
            bool success = CpuProvider.TryParseCoreInstance(inst, out long sortKey);
            Assert.IsFalse(success, $"Instance '{inst}' should be excluded (return false)");
            Assert.AreEqual(0L, sortKey);

            Assert.IsTrue(CpuProvider.IsTotalInstance(inst), $"IsTotalInstance should return true for '{inst}'");
        }
    }

    [TestMethod]
    public void TryParseCoreInstance_InvalidOrUnexpectedFormats_ReturnsFalseWithoutThrowing()
    {
        var invalidInstances = new[]
        {
            "",
            "   ",
            "abc",
            "0,abc",
            "abc,0",
            "0,0,0",
            "-1",
            "-1,0",
            "0,-1",
            ",",
            "0,",
            ",0",
            "invalid_core",
        };

        foreach (var inst in invalidInstances)
        {
            // Must not throw exception and must return false
            bool success = CpuProvider.TryParseCoreInstance(inst, out long sortKey);
            Assert.IsFalse(success, $"Instance '{inst}' should return false");
            Assert.AreEqual(0L, sortKey);
        }
    }

    [TestMethod]
    public void TryParseCoreInstance_WhitespaceHandling_ParsesValidly()
    {
        // NumberStyles.Integer and instanceName.Trim() allow leading/trailing whitespaces gracefully
        var whitespaceInstances = new[] { " 0,1 ", "0, 1", " 0 , 1 " };
        foreach (var inst in whitespaceInstances)
        {
            bool success = CpuProvider.TryParseCoreInstance(inst, out long sortKey);
            Assert.IsTrue(success, $"Instance '{inst}' should be parsed successfully");
            Assert.AreEqual(1L, sortKey);
        }
    }

    [TestMethod]
    public void CpuCoreInstance_UsageAndClockAlignment_ProducesIdenticalOrder()
    {
        // Simulate PDH items coming in different enumeration orders for usage vs clock counters,
        // including _Total instances and out-of-order logical cores across multiple NUMA/processor groups.
        var usageRaw = new (string InstanceName, double Value)[]
        {
            ("1,3", 45.0),
            ("0,10", 80.0),
            ("_Total", 50.0),
            ("0,1", 20.0),
            ("1,0", 15.0),
            ("0,0", 10.0),
            ("0,_Total", 50.0),
            ("0,2", 30.0),
        };

        var clockRaw = new (string InstanceName, double Value)[]
        {
            ("0,1", 3200.0),
            ("0,2", 3300.0),
            ("0,0", 3000.0),
            ("1,0", 3600.0),
            ("1,3", 3700.0),
            ("0,10", 3500.0),
            ("_Total", 3400.0),
        };

        var parsedUsage = ParseAndSort(usageRaw);
        var parsedClock = ParseAndSort(clockRaw);

        Assert.AreEqual(6, parsedUsage.Count);
        Assert.AreEqual(6, parsedClock.Count);

        // Core keys and alignments must match exactly across usage and clock
        for (int i = 0; i < parsedUsage.Count; i++)
        {
            Assert.AreEqual(parsedUsage[i].Key, parsedClock[i].Key, $"Core key mismatch at index {i}");
        }

        // Verify the expected order: 0,0 -> 0,1 -> 0,2 -> 0,10 -> 1,0 -> 1,3
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0,0", out long k0_0));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0,1", out long k0_1));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0,2", out long k0_2));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("0,10", out long k0_10));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("1,0", out long k1_0));
        Assert.IsTrue(CpuProvider.TryParseCoreInstance("1,3", out long k1_3));

        Assert.AreEqual(k0_0, parsedUsage[0].Key);
        Assert.AreEqual(10.0, parsedUsage[0].Value);
        Assert.AreEqual(3000.0, parsedClock[0].Value);

        Assert.AreEqual(k0_1, parsedUsage[1].Key);
        Assert.AreEqual(20.0, parsedUsage[1].Value);
        Assert.AreEqual(3200.0, parsedClock[1].Value);

        Assert.AreEqual(k0_2, parsedUsage[2].Key);
        Assert.AreEqual(30.0, parsedUsage[2].Value);
        Assert.AreEqual(3300.0, parsedClock[2].Value);

        Assert.AreEqual(k0_10, parsedUsage[3].Key);
        Assert.AreEqual(80.0, parsedUsage[3].Value);
        Assert.AreEqual(3500.0, parsedClock[3].Value);

        Assert.AreEqual(k1_0, parsedUsage[4].Key);
        Assert.AreEqual(15.0, parsedUsage[4].Value);
        Assert.AreEqual(3600.0, parsedClock[4].Value);

        Assert.AreEqual(k1_3, parsedUsage[5].Key);
        Assert.AreEqual(45.0, parsedUsage[5].Value);
        Assert.AreEqual(3700.0, parsedClock[5].Value);
    }

    private static List<(long Key, double Value)> ParseAndSort(IEnumerable<(string InstanceName, double Value)> items)
    {
        var buffer = new List<(long Key, double Value)>();
        foreach (var (name, value) in items)
        {
            if (CpuProvider.TryParseCoreInstance(name, out long key))
            {
                buffer.Add((key, value));
            }
        }

        buffer.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        return buffer;
    }
}
