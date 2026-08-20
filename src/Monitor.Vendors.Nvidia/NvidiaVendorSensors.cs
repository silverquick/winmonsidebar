using Monitor.Core.Abstractions;

namespace Monitor.Vendors.Nvidia;

/// <summary>
/// <see cref="NvidiaGpuSensors"/> を <see cref="IGpuVendorSensors"/> に適合させるアダプタ。
/// Monitor.Windows はこのクラスを直接参照しない（層の逆転を避けるため、App 起動時に
/// <see cref="Monitor.Windows.Providers.GpuProvider"/> のコンストラクタへ注入する）。
/// </summary>
public sealed class NvidiaVendorSensors : IGpuVendorSensors
{
    private readonly NvidiaGpuSensors _sensors;

    private NvidiaVendorSensors(NvidiaGpuSensors sensors)
    {
        _sensors = sensors;
    }

    /// <summary>nvapi64.dll が無い / NVIDIA GPU が無い / 初期化失敗のいずれでも null を返す
    /// （例外は投げない）。呼び出し側はこの結果をそのまま <c>GpuProvider</c> へ渡してよい。</summary>
    public static IGpuVendorSensors? TryCreate()
    {
        try
        {
            NvidiaGpuSensors? sensors = NvidiaGpuSensors.TryCreate();
            return sensors is null ? null : new NvidiaVendorSensors(sensors);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<GpuVendorReading> Read()
    {
        try
        {
            IReadOnlyList<NvidiaGpuReading> readings = _sensors.Read();
            if (readings.Count == 0)
            {
                return Array.Empty<GpuVendorReading>();
            }

            string? driverVersion = _sensors.DriverVersion;

            var results = new List<GpuVendorReading>(readings.Count);
            foreach (NvidiaGpuReading r in readings)
            {
                results.Add(new GpuVendorReading
                {
                    Name = r.Name,
                    Luid = r.Luid,
                    TemperatureC = r.TemperatureC,
                    HotspotTemperatureC = r.HotspotTemperatureC,
                    MemoryTemperatureC = r.MemoryTemperatureC,
                    FanPercent = r.FanPercent,
                    FanRpm = r.FanRpm,
                    PowerWatts = r.PowerWatts,
                    PowerLimitWatts = r.PowerLimitWatts,
                    CoreClockMhz = r.CoreClockMhz,
                    MemoryClockMhz = r.MemoryClockMhz,
                    DedicatedTotalBytes = r.DedicatedTotalBytes,
                    DriverVersion = driverVersion,
                });
            }

            return results;
        }
        catch
        {
            return Array.Empty<GpuVendorReading>();
        }
    }

    public void Dispose()
    {
        try
        {
            _sensors.Dispose();
        }
        catch
        {
            // Dispose 中の例外は無視する。
        }
    }
}
