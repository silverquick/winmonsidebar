using Monitor.Core.Models;

namespace Monitor.Core.Alerts;

/// <summary>
/// 「枯渇」「温度」「障害」「高負荷継続」の警告判定を集約する。閾値は <see cref="AlertThresholds"/> 参照。
/// 各メソッドはスナップショット型に依存せずプリミティブ値を受け取る（<see cref="Formatting.ByteFormatter"/> と
/// 同じ流儀）。呼び出し側（各セクションの ViewModel またはトラッカー）でスナップショットから値を取り出して渡す。
/// </summary>
public static class AlertEvaluator
{
    /// <summary>観測警告: ディスク高 Busy 継続。
    /// 瞬間的な高 Busy は正常な大容量コピーでも発生するため、一定時間以上継続した場合のみ警告する。
    /// 発報後は 85% 未満が 15 秒続くまで維持する（ヒステリシス）。</summary>
    /// <param name="busyPercent">現在のディスク使用率（%）。</param>
    /// <param name="highDuration">高 Busy 状態の連続継続時間（Caution/Critical 共通）。</param>
    /// <param name="recoveryDuration">解除閾値未満（85%未満）の連続継続時間。</param>
    /// <param name="previousLevel">前回の警告レベル（ヒステリシス判定用）。</param>
    public static AlertLevel DiskSustainedBusy(
        double busyPercent,
        TimeSpan highDuration,
        TimeSpan recoveryDuration,
        AlertLevel previousLevel) =>
        DiskSustainedBusy(busyPercent, highDuration, highDuration, recoveryDuration, previousLevel);

    /// <summary>観測警告: ディスク高 Busy 継続（Caution/Critical 継続時間を個別指定）。</summary>
    /// <param name="busyPercent">現在のディスク使用率（%）。</param>
    /// <param name="cautionDuration">Caution 域（>=95%）の連続継続時間。</param>
    /// <param name="criticalDuration">Critical 域（>=99%）の連続継続時間。</param>
    /// <param name="recoveryDuration">解除閾値未満（85%未満）の連続継続時間。</param>
    /// <param name="previousLevel">前回の警告レベル（ヒステリシス判定用）。</param>
    public static AlertLevel DiskSustainedBusy(
        double busyPercent,
        TimeSpan cautionDuration,
        TimeSpan criticalDuration,
        TimeSpan recoveryDuration,
        AlertLevel previousLevel)
    {
        if (double.IsNaN(busyPercent) || double.IsInfinity(busyPercent) || busyPercent < 0)
        {
            return AlertLevel.None;
        }

        if (cautionDuration < TimeSpan.Zero)
        {
            cautionDuration = TimeSpan.Zero;
        }

        if (criticalDuration < TimeSpan.Zero)
        {
            criticalDuration = TimeSpan.Zero;
        }

        if (recoveryDuration < TimeSpan.Zero)
        {
            recoveryDuration = TimeSpan.Zero;
        }

        if (previousLevel != AlertLevel.None &&
            busyPercent < AlertThresholds.DiskBusyRecoveryPercent &&
            recoveryDuration >= AlertThresholds.DiskBusyRecoveryDuration)
        {
            return AlertLevel.None;
        }

        if (busyPercent >= AlertThresholds.DiskBusyCriticalPercent &&
            criticalDuration >= AlertThresholds.DiskBusyCriticalDuration)
        {
            return AlertLevel.Critical;
        }

        if (busyPercent >= AlertThresholds.DiskBusyCautionPercent &&
            cautionDuration >= AlertThresholds.DiskBusyCautionDuration)
        {
            return previousLevel == AlertLevel.Critical ? AlertLevel.Critical : AlertLevel.Caution;
        }

        if (previousLevel != AlertLevel.None)
        {
            return previousLevel;
        }

        return AlertLevel.None;
    }
    /// <summary>枯渇: ドライブ空き容量。使用率(%)と絶対空き容量の AND 条件で判定する
    /// （AND である理由は <see cref="AlertThresholds"/> のコメント参照）。</summary>
    public static AlertLevel DriveCapacity(double usedPercent, ulong freeBytes)
    {
        double freeGb = freeBytes / 1024.0 / 1024.0 / 1024.0;

        if (usedPercent >= AlertThresholds.DriveUsedPercentCritical && freeGb < AlertThresholds.DriveFreeGbCritical)
        {
            return AlertLevel.Critical;
        }

        if (usedPercent >= AlertThresholds.DriveUsedPercentCaution && freeGb < AlertThresholds.DriveFreeGbCaution)
        {
            return AlertLevel.Caution;
        }

        return AlertLevel.None;
    }

    /// <summary>枯渇: メモリのコミット量（使用/上限）。</summary>
    public static AlertLevel MemoryCommit(ulong committedBytes, ulong commitLimitBytes)
    {
        if (commitLimitBytes == 0)
        {
            return AlertLevel.None;
        }

        double percent = (double)committedBytes / commitLimitBytes * 100.0;
        return FromThreshold(percent, AlertThresholds.CommitPercentCaution, AlertThresholds.CommitPercentCritical);
    }

    /// <summary>温度: CPU (Tctl/Tdie 相当のパッケージ温度)。</summary>
    public static AlertLevel CpuTemperature(double? temperatureC) =>
        FromThreshold(temperatureC, AlertThresholds.CpuTempCautionC, AlertThresholds.CpuTempCriticalC);

    /// <summary>温度: GPU コア。</summary>
    public static AlertLevel GpuCoreTemperature(double? temperatureC) =>
        FromThreshold(temperatureC, AlertThresholds.GpuCoreTempCautionC, AlertThresholds.GpuCoreTempCriticalC);

    /// <summary>温度: GPU ホットスポット。</summary>
    public static AlertLevel GpuHotspotTemperature(double? temperatureC) =>
        FromThreshold(temperatureC, AlertThresholds.GpuHotspotTempCautionC, AlertThresholds.GpuHotspotTempCriticalC);

    /// <summary>温度: ディスク。ドライブ自身の申告値を優先し、無ければ既定値にフォールバックする
    /// （二段フォールバックの詳細は <see cref="AlertThresholds"/> のコメント参照）。</summary>
    /// <param name="temperatureC">現在のディスク温度（<see cref="Models.DiskDeviceSnapshot.TemperatureC"/>）。</param>
    /// <param name="warningTemperatureC">ドライブ申告の警告温度（<see cref="Models.DiskDeviceSnapshot.WarningTemperatureC"/>）。</param>
    /// <param name="criticalTemperatureC">ドライブ申告の臨界温度（<see cref="Models.DiskDeviceSnapshot.CriticalTemperatureC"/>）。</param>
    /// <param name="isSsd">申告値が無い場合の既定値選択に使う（<see cref="Models.DiskDeviceSnapshot.IsSsd"/>）。</param>
    public static AlertLevel DiskTemperature(double? temperatureC, double? warningTemperatureC, double? criticalTemperatureC, bool isSsd)
    {
        if (temperatureC is not double temp)
        {
            return AlertLevel.None;
        }

        double caution;
        double critical;

        if (warningTemperatureC is double warning)
        {
            // 1. 申告値がある場合はそれを使う。
            caution = warning;
            // 2. 臨界温度だけ申告が無い機種は「警告温度 + マージン」で補う。
            critical = criticalTemperatureC ?? (warning + AlertThresholds.DiskTempCriticalFallbackMarginC);
        }
        else
        {
            // 3. どちらの申告も無ければ SSD/HDD の既定値。
            caution = isSsd ? AlertThresholds.DiskTempDefaultSsdCautionC : AlertThresholds.DiskTempDefaultHddCautionC;
            critical = isSsd ? AlertThresholds.DiskTempDefaultSsdCriticalC : AlertThresholds.DiskTempDefaultHddCriticalC;
        }

        return FromThreshold(temp, caution, critical);
    }

    /// <summary>障害: 冷却異常。CPU またはマザーボード温度が注意域を超えていて、かつ認識しているファンが
    /// 1つ以上ありそのすべてが 0 RPM の場合に Caution（このカテゴリに Critical は無い）。
    /// 最近のマザーボードはアイドル時にファンを止める(Zero RPM)のが正常なので、RPM 0 単独では
    /// 絶対に警告にしない。温度条件との AND が必須。
    /// またファンを1つも認識していない場合（非管理者起動時など、<see cref="Models.ThermalSnapshot.Fans"/> が
    /// 空の場合）は判定自体を行わない。「1台も認識していない」と「認識した全台が0RPM」は区別できない情報から
    /// 後者だけを誤って異常と判定してしまうのを防ぐため。</summary>
    public static AlertLevel CoolingFault(double? cpuTemperatureC, double? motherboardTemperatureC, IReadOnlyList<SensorReading> fans)
    {
        if (fans.Count == 0)
        {
            return AlertLevel.None;
        }

        bool allFansStopped = true;
        foreach (SensorReading fan in fans)
        {
            if (fan.Value > 0)
            {
                allFansStopped = false;
                break;
            }
        }

        if (!allFansStopped)
        {
            return AlertLevel.None;
        }

        bool cpuHot = cpuTemperatureC is double cpu && cpu >= AlertThresholds.CpuTempCautionC;
        bool boardHot = motherboardTemperatureC is double board && board >= AlertThresholds.MotherboardTempCautionC;

        return cpuHot || boardHot ? AlertLevel.Caution : AlertLevel.None;
    }

    /// <summary>障害: ネットワークドライブ切断。呼び出し側で <see cref="Models.VolumeKind.Network"/> の
    /// ボリュームに限定して渡すこと（ローカルドライブの IsReady=false は対象外）。</summary>
    public static AlertLevel NetworkDriveDisconnected(bool isReady) =>
        isReady ? AlertLevel.None : AlertLevel.Caution;

    private static AlertLevel FromThreshold(double? value, double caution, double critical)
    {
        if (value is not double v)
        {
            return AlertLevel.None;
        }

        return FromThreshold(v, caution, critical);
    }

    private static AlertLevel FromThreshold(double value, double caution, double critical)
    {
        if (value >= critical)
        {
            return AlertLevel.Critical;
        }

        if (value >= caution)
        {
            return AlertLevel.Caution;
        }

        return AlertLevel.None;
    }
}
