using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Optional.Lhm;

namespace Monitor.App.Tests;

/// <summary>
/// NVMe の閾値センサー（Warning / Critical Temperature）をディスク温度として取り込まないことの検証。
/// センサー名は実機（WD Blue SN580、LHM 0.9.6）で確認したもの。
/// </summary>
[TestClass]
public sealed class LhmStorageThresholdSensorTests
{
    [TestMethod]
    public void 非表示センサー照合は生名識別子と空白を扱う()
    {
        Assert.IsFalse(LhmThermalProvider.IsHiddenSensor(Array.Empty<string>(), "Temperature #3", "id"));
        Assert.IsTrue(LhmThermalProvider.IsHiddenSensor(new[] { "Temperature #3" }, "Temperature #3", "id"));
        Assert.IsTrue(LhmThermalProvider.IsHiddenSensor(new[] { "temperature #3" }, "Temperature #3", "id"));
        Assert.IsTrue(LhmThermalProvider.IsHiddenSensor(new[] { "id" }, "Temperature #3", "id"));
        Assert.IsFalse(LhmThermalProvider.IsHiddenSensor(new[] { "  ", "" }, "Temperature #3", "id"));
    }

    [TestMethod]
    [DataRow("Warning Temperature")]
    [DataRow("Critical Temperature")]
    [DataRow("critical temperature ")]
    public void 閾値センサーは除外対象(string name)
    {
        Assert.IsTrue(LhmThermalProvider.IsStorageThresholdSensor(name));
    }

    [TestMethod]
    [DataRow("Composite Temperature")]
    [DataRow("Temperature")]
    [DataRow("Temperature #1")]
    [DataRow("Temperature #2")]
    [DataRow("")]
    public void 測定値のセンサーは除外しない(string name)
    {
        Assert.IsFalse(LhmThermalProvider.IsStorageThresholdSensor(name));
    }
}
