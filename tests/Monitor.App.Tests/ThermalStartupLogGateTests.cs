using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Diagnostics;
using Monitor.Core.Models;

namespace Monitor.App.Tests;

/// <summary>
/// 起動時の温度ログが、Thermal レーンの初回サンプリング前の初期値を「取れていない」と誤記録しないことの検証。
/// </summary>
[TestClass]
public sealed class ThermalStartupLogGateTests
{
    [TestMethod]
    public void 初期値の間は記録せず_実際のサンプリング結果で1回だけ記録する()
    {
        var gate = new ThermalStartupLogGate(maxSnapshots: 30);

        for (int i = 0; i < 10; i++)
        {
            Assert.IsFalse(gate.ShouldLog(ThermalSnapshot.Empty, out _));
        }

        var sampled = new ThermalSnapshot { IsAvailable = true, IsElevated = true, Source = "LibreHardwareMonitor" };
        Assert.IsTrue(gate.ShouldLog(sampled, out bool wasSampled));
        Assert.IsTrue(wasSampled);

        Assert.IsFalse(gate.ShouldLog(sampled, out _));
    }

    [TestMethod]
    public void 利用不可でも初期値と別インスタンスならサンプリング結果として記録する()
    {
        // LhmThermalProvider は利用不可時も Empty with { IsElevated, Source } の新しいインスタンスを返す。
        var gate = new ThermalStartupLogGate();
        ThermalSnapshot unavailable = ThermalSnapshot.Empty with { IsElevated = true, Source = "none" };

        Assert.IsTrue(gate.ShouldLog(unavailable, out bool wasSampled));
        Assert.IsTrue(wasSampled);
    }

    [TestMethod]
    public void 初期値のままでも上限回数で打ち切って1回だけ記録する()
    {
        var gate = new ThermalStartupLogGate(maxSnapshots: 3);

        Assert.IsFalse(gate.ShouldLog(ThermalSnapshot.Empty, out _));
        Assert.IsFalse(gate.ShouldLog(ThermalSnapshot.Empty, out _));
        Assert.IsTrue(gate.ShouldLog(ThermalSnapshot.Empty, out bool wasSampled));
        Assert.IsFalse(wasSampled);

        Assert.IsFalse(gate.ShouldLog(ThermalSnapshot.Empty, out _));
    }

    [TestMethod]
    public void 複数スレッドから同時に呼ばれても記録は1回だけ()
    {
        var gate = new ThermalStartupLogGate(maxSnapshots: 5);
        var sampled = new ThermalSnapshot { IsAvailable = true };
        int logged = 0;

        Parallel.For(0, 1000, i =>
        {
            ThermalSnapshot t = i % 2 == 0 ? ThermalSnapshot.Empty : sampled;
            if (gate.ShouldLog(t, out _))
            {
                Interlocked.Increment(ref logged);
            }
        });

        Assert.AreEqual(1, logged);
    }
}
