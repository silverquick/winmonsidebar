using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Providers;

namespace Monitor.App.Tests;

[TestClass]
public sealed class GpuProviderTests
{
    [TestMethod]
    public void Constructor_WithFactory_DoesNotInvokeFactoryEagerly()
    {
        var factoryCalled = false;
        var provider = new GpuProvider(() =>
        {
            factoryCalled = true;
            return null;
        });

        Assert.IsFalse(factoryCalled, "GpuProvider のコンストラクタ内でファクトリを即時実行してはならない（起動遅延の防止）。");

        provider.Dispose();
    }

    [TestMethod]
    public void Initialize_WithFactory_InvokesFactoryAndDisposesCreatedSensors()
    {
        var factoryCallCount = 0;
        var fakeSensors = new FakeGpuVendorSensors();

        var provider = new GpuProvider(() =>
        {
            factoryCallCount++;
            return fakeSensors;
        });

        Assert.AreEqual(0, factoryCallCount);
        Assert.IsFalse(fakeSensors.IsDisposed);

        provider.Initialize();
        Assert.AreEqual(1, factoryCallCount, "Initialize() 実行時にファクトリが一度だけ呼び出されること。");

        provider.Dispose();
        Assert.IsTrue(fakeSensors.IsDisposed, "GpuProvider.Dispose() 時に生成したベンダーセンサーが破棄されること。");
    }

    [TestMethod]
    public void Initialize_WhenFactoryThrows_DoesNotThrowAndDisposesCleanly()
    {
        var provider = new GpuProvider(() => throw new InvalidOperationException("NVAPI 初期化失敗シミュレーション"));

        try
        {
            provider.Initialize();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Initialize はファクトリの例外を外に漏らしてはならない: {ex.Message}");
        }

        try
        {
            provider.Dispose();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Dispose は例外を外に漏らしてはならない: {ex.Message}");
        }
    }

    [TestMethod]
    public void Initialize_WhenFactoryReturnsNull_OperatesGracefully()
    {
        var provider = new GpuProvider(() => null);

        provider.Initialize();
        GpuSnapshot snapshot = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(snapshot);

        provider.Dispose();
    }

    [TestMethod]
    public void Constructor_WithNull_ResolvesUnambiguouslyAndOperatesGracefully()
    {
        var provider = new GpuProvider(null);

        provider.Initialize();
        GpuSnapshot snapshot = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(snapshot);

        provider.Dispose();
    }

    [TestMethod]
    public void Constructor_Parameterless_ResolvesUnambiguouslyAndOperatesGracefully()
    {
        var provider = new GpuProvider();

        provider.Initialize();
        GpuSnapshot snapshot = provider.Sample(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(snapshot);

        provider.Dispose();
    }

    private sealed class FakeGpuVendorSensors : IGpuVendorSensors
    {
        public bool IsDisposed { get; private set; }

        public IReadOnlyList<GpuVendorReading> Read() => Array.Empty<GpuVendorReading>();

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
