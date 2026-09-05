using Monitor.Core.Models;

namespace Monitor.Core.Alerts;

/// <summary>
/// 物理ディスクごとの高 Busy 継続状態を追跡するステートフル部品。
/// 状態は <see cref="DiskDeviceSnapshot.PhysicalDriveNumber"/> をキーに保持し、
/// 新しいディスクサンプルが得られた時だけ <see cref="Update"/> を呼び出して更新する。
/// 警告レベルの決定自体は <see cref="AlertEvaluator.DiskSustainedBusy(double, TimeSpan, TimeSpan, AlertLevel)"/>
/// の純粋関数へ委譲する。
/// </summary>
public sealed class DiskBusyAlertTracker
{
    public static readonly TimeSpan DefaultMaxSampleGap = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _maxSampleGap;
    private readonly Dictionary<int, DiskBusyState> _states = new();

    public DiskBusyAlertTracker(TimeSpan? maxSampleGap = null)
    {
        _maxSampleGap = maxSampleGap ?? DefaultMaxSampleGap;
    }

    /// <summary>
    /// ディスクスナップショットと前回サンプルからの実経過時間を渡し、各物理ディスクの
    /// 高 Busy 継続警告レベルを更新した新しいスナップショットを返す。
    /// </summary>
    public DiskSnapshot Update(DiskSnapshot snapshot, TimeSpan elapsed)
    {
        if (snapshot.Devices.Count == 0)
        {
            _states.Clear();
            return snapshot;
        }

        // 許容間隔を超過した場合はスリープ・サスペンド復帰等の長時間停止とみなし、全ディスクの連続性をリセット
        bool isGap = elapsed <= TimeSpan.Zero || elapsed > _maxSampleGap;
        if (isGap)
        {
            _states.Clear();
        }

        // スナップショットから消失したディスクの状態を削除
        var activeDriveNumbers = new HashSet<int>(snapshot.Devices.Count);
        foreach (DiskDeviceSnapshot device in snapshot.Devices)
        {
            activeDriveNumbers.Add(device.PhysicalDriveNumber);
        }

        var removedKeys = new List<int>();
        foreach (int key in _states.Keys)
        {
            if (!activeDriveNumbers.Contains(key))
            {
                removedKeys.Add(key);
            }
        }

        foreach (int key in removedKeys)
        {
            _states.Remove(key);
        }

        var updatedDevices = new DiskDeviceSnapshot[snapshot.Devices.Count];
        for (int i = 0; i < snapshot.Devices.Count; i++)
        {
            DiskDeviceSnapshot device = snapshot.Devices[i];
            AlertLevel level = UpdateDevice(device, elapsed, isGap);
            updatedDevices[i] = device with { BusyAlertLevel = level };
        }

        return snapshot with { Devices = updatedDevices };
    }

    /// <summary>特定の物理ディスクの現在の警告レベルを取得する（テスト・確認用）。</summary>
    public AlertLevel GetAlertLevel(int physicalDriveNumber)
    {
        return _states.TryGetValue(physicalDriveNumber, out DiskBusyState? state) ? state.CurrentLevel : AlertLevel.None;
    }

    /// <summary>全ディスクの追跡状態をリセットする。</summary>
    public void Reset()
    {
        _states.Clear();
    }

    private AlertLevel UpdateDevice(DiskDeviceSnapshot device, TimeSpan elapsed, bool isGap)
    {
        if (device.PhysicalDriveNumber < 0)
        {
            return AlertLevel.None;
        }

        double busy = device.BusyPercent;
        if (double.IsNaN(busy) || double.IsInfinity(busy) || busy < 0)
        {
            _states.Remove(device.PhysicalDriveNumber);
            return AlertLevel.None;
        }

        if (!_states.TryGetValue(device.PhysicalDriveNumber, out DiskBusyState? state))
        {
            state = new DiskBusyState();
            _states[device.PhysicalDriveNumber] = state;
        }

        if (isGap)
        {
            state.Reset();
        }

        // 継続時間の更新
        if (busy >= AlertThresholds.DiskBusyCriticalPercent)
        {
            state.CautionDuration += elapsed;
            state.CriticalDuration += elapsed;
            state.RecoveryDuration = TimeSpan.Zero;
        }
        else if (busy >= AlertThresholds.DiskBusyCautionPercent)
        {
            state.CautionDuration += elapsed;
            state.CriticalDuration = TimeSpan.Zero;
            state.RecoveryDuration = TimeSpan.Zero;
        }
        else if (busy >= AlertThresholds.DiskBusyRecoveryPercent)
        {
            // 85%〜95%のヒステリシス帯:
            // 発報前なら継続時間をリセット、発報後なら現在レベルを維持
            state.CautionDuration = TimeSpan.Zero;
            state.CriticalDuration = TimeSpan.Zero;
            state.RecoveryDuration = TimeSpan.Zero;
        }
        else
        {
            // 85%未満: 解除候補時間を進める
            state.CautionDuration = TimeSpan.Zero;
            state.CriticalDuration = TimeSpan.Zero;
            if (state.CurrentLevel != AlertLevel.None)
            {
                state.RecoveryDuration += elapsed;
            }
            else
            {
                state.RecoveryDuration = TimeSpan.Zero;
            }
        }

        AlertLevel newLevel = AlertEvaluator.DiskSustainedBusy(
            busy,
            state.CautionDuration,
            state.CriticalDuration,
            state.RecoveryDuration,
            state.CurrentLevel);

        state.CurrentLevel = newLevel;

        if (newLevel == AlertLevel.None)
        {
            state.RecoveryDuration = TimeSpan.Zero;
        }

        return newLevel;
    }

    private sealed class DiskBusyState
    {
        public AlertLevel CurrentLevel { get; set; } = AlertLevel.None;
        public TimeSpan CautionDuration { get; set; } = TimeSpan.Zero;
        public TimeSpan CriticalDuration { get; set; } = TimeSpan.Zero;
        public TimeSpan RecoveryDuration { get; set; } = TimeSpan.Zero;

        public void Reset()
        {
            CurrentLevel = AlertLevel.None;
            CautionDuration = TimeSpan.Zero;
            CriticalDuration = TimeSpan.Zero;
            RecoveryDuration = TimeSpan.Zero;
        }
    }
}
