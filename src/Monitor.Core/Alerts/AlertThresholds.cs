namespace Monitor.Core.Alerts;

/// <summary>
/// <see cref="AlertEvaluator"/> が使う閾値の定数。1箇所に集約することで、将来 settings.json から
/// 上書きできるようにする余地を残す（今回は設定ファイル対応そのものは未実装）。
/// 各値の根拠はグループごとのコメントを参照。
/// </summary>
public static class AlertThresholds
{
    // ===== 枯渇: ドライブ空き容量 =====
    // 使用率(%)と絶対空き容量(GB)の AND 条件。片方だけでは実機で誤検知する:
    //   率だけだと 16TB HDD が 1.6TB 残した状態でも警告になってしまう（率は高いが実際は余裕がある）。
    //   絶対値だけだと 120GB SSD が 26% 空き（≒31GB）でも警告にならない（絶対値はまだ多いが実際は逼迫）。
    // 「率が高い」かつ「残りが実際に少ない」の両方が揃って初めて警告する。
    public const double DriveUsedPercentCaution = 90.0;
    public const double DriveFreeGbCaution = 32.0;
    public const double DriveUsedPercentCritical = 95.0;
    public const double DriveFreeGbCritical = 10.0;

    // ===== 枯渇: メモリコミット（使用/上限） =====
    public const double CommitPercentCaution = 85.0;
    public const double CommitPercentCritical = 95.0;

    // ===== 温度: CPU (Tctl/Tdie) =====
    public const double CpuTempCautionC = 85.0;
    public const double CpuTempCriticalC = 95.0;

    // ===== 温度: GPU コア =====
    public const double GpuCoreTempCautionC = 80.0;
    public const double GpuCoreTempCriticalC = 87.0;

    // ===== 温度: GPU ホットスポット =====
    public const double GpuHotspotTempCautionC = 95.0;
    public const double GpuHotspotTempCriticalC = 105.0;

    // ===== 温度: ディスク（ドライブ申告値が無い場合の既定値） =====
    // 実機確認では、臨界温度(CriticalTemperatureC)を申告するドライブは 7台中1台(NVMe)のみ、
    // 警告温度(WarningTemperatureC)は取得できた4台すべてで申告されていた。機種ごとに 55/60/70/84°C と
    // ばらつくため一律定数は使わず、AlertEvaluator.DiskTemperature で次の優先順位でフォールバックする:
    //   1. 申告された WarningTemperatureC / CriticalTemperatureC があればそれを使う。
    //   2. CriticalTemperatureC が無く WarningTemperatureC がある場合、危険側は
    //      「警告温度 + DiskTempCriticalFallbackMarginC」とする。
    //   3. どちらも申告が無い場合、IsSsd を見てこの既定値を使う。
    public const double DiskTempDefaultSsdCautionC = 70.0;
    public const double DiskTempDefaultSsdCriticalC = 80.0;
    public const double DiskTempDefaultHddCautionC = 55.0;
    public const double DiskTempDefaultHddCriticalC = 65.0;
    public const double DiskTempCriticalFallbackMarginC = 10.0;

    // ===== 障害: 冷却異常 =====
    // 表には「CPU またはマザーボード温度が注意域を超えている」とだけ指定があり、マザーボード側の
    // 「注意域」は表に無かったため、ここでの判断として CPU の注意閾値と同じ値を暫定採用する
    // （マザーボード温度専用の閾値表が無い以上、既存の CPU 閾値を流用するのが最も保守的）。
    public const double MotherboardTempCautionC = CpuTempCautionC;
}
