namespace Monitor.Core.Alerts;

/// <summary>
/// セクションが「ユーザーが手を打つべき状態」にあるかどうかの深刻度。
/// 使用率が高いこと自体は対象にしない（CPU/GPU 100% はビルド中・ゲーム中なら正常、ディスク Busy 100% は
/// コピー中なら正常、物理メモリ使用率が高いのは Windows がキャッシュで埋める正常な挙動）。
/// 警告するのは「枯渇」「温度」「障害」の3カテゴリだけ。判定ロジックは <see cref="AlertEvaluator"/> に、
/// 閾値の定数は <see cref="AlertThresholds"/> に集約する（各 ViewModel には散らばらせない）。
/// </summary>
public enum AlertLevel
{
    /// <summary>警告なし。既定値。</summary>
    None,

    /// <summary>注意。近いうちに手を打つべき状態。</summary>
    Caution,

    /// <summary>危険。すぐに手を打つべき状態。</summary>
    Critical,
}
