using Monitor.Core.Models;

namespace Monitor.App.Diagnostics;

/// <summary>
/// 起動時に温度・ファンの取得結果を 1 度だけ記録するタイミングを決める。
/// SnapshotAvailable はどのレーンの更新でも発火するため、Thermal レーン（既定 5 秒周期）の初回サンプリング前は
/// hub の初期値である <see cref="ThermalSnapshot.Empty"/> そのもの（参照が同一）が入っている。
/// それを「取れていない」と誤記録しないよう、実際にサンプリングされた値が来るまで待つ。
/// プロバイダが利用不可でサンプリング結果が初期値のまま変わらない場合でも、
/// 一定回数の通知を受けたら打ち切って記録する（記録が永遠に出ない状態を避ける）。
/// 複数レーンのスレッドから同時に呼ばれるため lock で保護する。
/// </summary>
internal sealed class ThermalStartupLogGate
{
    /// <summary>既定の打ち切り回数。全レーン合計で毎秒 2 回弱の通知が来るため、おおよそ 15 秒強に相当する。</summary>
    public const int DefaultMaxSnapshots = 30;

    private readonly object _gate = new();
    private readonly int _maxSnapshots;
    private int _seen;
    private bool _done;

    public ThermalStartupLogGate(int maxSnapshots = DefaultMaxSnapshots)
    {
        _maxSnapshots = Math.Max(1, maxSnapshots);
    }

    /// <summary>
    /// 今回の値を記録すべきなら true を返す（true を返すのは生涯で 1 回だけ）。
    /// <paramref name="sampled"/> は値が実際のサンプリング結果か（false なら打ち切りによる初期値の記録）。
    /// </summary>
    public bool ShouldLog(ThermalSnapshot thermal, out bool sampled)
    {
        sampled = !ReferenceEquals(thermal, ThermalSnapshot.Empty);

        lock (_gate)
        {
            if (_done)
            {
                return false;
            }

            _seen++;
            if (!sampled && _seen < _maxSnapshots)
            {
                return false;
            }

            _done = true;
            return true;
        }
    }
}
