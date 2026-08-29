using Monitor.Core.Models;

namespace Monitor.Core.Abstractions;

/// <summary>
/// 1種類のメトリクスを供給する。Windows API / ベンダーAPI など実装を差し替え可能にするための境界。
/// </summary>
public interface IMetricProvider<T> : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Initialize() 実行後に、このプロバイダが実際に値を取れるかどうか。
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 最初の Sample() より前に一度だけ呼ばれる。例外を投げてはならない。
    /// 失敗したら IsAvailable を false にして戻る。
    /// </summary>
    void Initialize();

    /// <summary>
    /// バックグラウンドスレッドから一定間隔で呼ばれる。例外を投げてはならない。
    /// elapsed は前回 Sample からの実経過時間。取得不能なら既定値を返す。
    /// </summary>
    T Sample(TimeSpan elapsed);
}

/// <summary>概要値と詳細値を別コストで取得できるプロバイダの任意拡張。
/// UI は表示状態をこの契約経由で通知し、Core は具体的な UI 型へ依存しない。</summary>
public interface IDetailSamplingProvider
{
    void SetDetailSamplingEnabled(bool enabled);
}

/// <summary>
/// 温度・ファン・電力など、ハードウェア/権限に依存して取れたり取れなかったりするセンサー群。
/// 実装を差し替え可能にするための境界（現状は LibreHardwareMonitor 実装のみ）。
/// </summary>
public interface IThermalProvider : IMetricProvider<ThermalSnapshot>
{
    /// <summary>
    /// このプロバイダが値を出すために管理者権限を必要とするか。
    /// </summary>
    bool RequiresElevation { get; }
}
