namespace Monitor.Core.Abstractions;

/// <summary>
/// GPU ベンダー固有 API（NVAPI 等）から供給される、DXGI/PDH では取れないセンサー群。
/// Monitor.Windows は特定ベンダーの実装（Monitor.Vendors.Nvidia 等）を直接参照しない
/// （層の逆転を避けるため）。<see cref="Providers.GpuProvider"/> のような実装はこのインターフェース
/// を通じて注入されたベンダーセンサーだけを使う。
/// </summary>
public interface IGpuVendorSensors : IDisposable
{
    /// <summary>
    /// 列挙順が安定していること（LUID/名前で照合できない場合のフォールバックとして使われる）。
    /// 例外を投げてはならない。取得できない項目は null を返すこと。
    /// </summary>
    IReadOnlyList<GpuVendorReading> Read();
}

/// <summary>1つの物理 GPU についてベンダー API から読めたセンサー値。取得できなかった項目は
/// 常に null（0 で埋めない）。</summary>
public sealed record GpuVendorReading
{
    public string Name { get; init; } = "";

    /// <summary>DXGI の LUID と突き合わせ可能なら設定する。突き合わせ手段が無いベンダー実装は
    /// 常に null を返してよい（呼び出し側は名前一致・順序一致にフォールバックする）。</summary>
    public long? Luid { get; init; }

    public double? TemperatureC { get; init; }
    public double? HotspotTemperatureC { get; init; }
    public double? MemoryTemperatureC { get; init; }
    public double? FanPercent { get; init; }
    public int? FanRpm { get; init; }
    public double? PowerWatts { get; init; }
    public double? PowerLimitWatts { get; init; }
    public double? CoreClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
    public ulong? DedicatedTotalBytes { get; init; }
    public string? DriverVersion { get; init; }
}
