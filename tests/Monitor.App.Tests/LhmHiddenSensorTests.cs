using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Optional.Lhm;

namespace Monitor.App.Tests;

[TestClass]
public sealed class LhmHiddenSensorTests
{
    private const string SensorName = "Temperature #3";
    private const string Identifier = "/lpc/nct6798d/0/temperature/3";

    [TestMethod]
    public void 空リストは一致しない() => Assert.IsFalse(Matches(Array.Empty<string>(), SensorName));

    [TestMethod]
    public void 名前の完全一致で隠す() => Assert.IsTrue(Matches(new[] { SensorName }, SensorName));

    [TestMethod]
    public void 名前は大文字小文字を無視する() => Assert.IsTrue(Matches(new[] { "temperature #3" }, SensorName));

    [TestMethod]
    public void 識別子でも隠せる() => Assert.IsTrue(Matches(new[] { Identifier }, SensorName));

    [TestMethod]
    public void 空白だけの項目は無視する() => Assert.IsFalse(Matches(new[] { "  ", "" }, SensorName));

    [TestMethod]
    public void 前後空白を除去してから照合する() => Assert.IsTrue(Matches(new[] { "  Temperature #3 " }, SensorName));

    private static bool Matches(IEnumerable<string> entries, string name) =>
        LhmThermalProvider.IsHiddenSensor(LhmThermalProvider.NormalizeHiddenSensors(entries), name, Identifier);
}
