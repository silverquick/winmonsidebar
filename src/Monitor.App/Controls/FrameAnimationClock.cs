using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// フレームごとのアニメーション更新処理を提供するコントロールのインターフェイス。
/// </summary>
public interface IAnimatableControl
{
    /// <summary>
    /// フレーム更新処理。
    /// </summary>
    /// <param name="deltaSeconds">前フレームからの経過秒数。</param>
    /// <returns>アニメーションを継続する場合は true、完了してクロック購読を終了する場合は false。</returns>
    bool OnAnimationFrame(double deltaSeconds);
}

/// <summary>
/// 最大フレームレートに基づいてフレーム間引き（スロットリング）を行う純粋計算ヘルパー。
/// WPF 描画コンテキストに依存せず単体テスト可能。
/// </summary>
public struct FrameThrottler
{
    /// <summary>既定の最大フレームレート（30 fps）。</summary>
    public const double DefaultMaxFps = 30.0;

    private readonly double _minIntervalSeconds;
    private TimeSpan _lastTime;
    private bool _hasLastTime;

    public FrameThrottler(double maxFps = DefaultMaxFps)
    {
        _minIntervalSeconds = maxFps > 0 ? (1.0 / maxFps) : 0.0;
        _lastTime = TimeSpan.Zero;
        _hasLastTime = false;
    }

    public double MinIntervalSeconds => _minIntervalSeconds;
    public bool HasLastTime => _hasLastTime;

    /// <summary>
    /// スロットリング状態をリセットする。
    /// </summary>
    public void Reset()
    {
        _lastTime = TimeSpan.Zero;
        _hasLastTime = false;
    }

    /// <summary>
    /// 現在のレンダリング時刻をもとに、フレーム処理を実行すべきかを判定する。
    /// 最小間隔未満の場合はスキップして false を返し、前回の処理時刻は更新しない。
    /// 実行すべき場合は true を返し、実経過時間 deltaSeconds を出力して次回基準時刻を更新する。
    /// </summary>
    /// <param name="currentTime">現在のレンダリング時刻。</param>
    /// <param name="deltaSeconds">前回実行時からの実経過秒数（初回は 1/maxFps または 1/60 秒、最大 0.1 秒でクランプ）。</param>
    /// <returns>フレーム処理を実行する場合は true、間引く場合は false。</returns>
    public bool TryStep(TimeSpan currentTime, out double deltaSeconds)
    {
        if (!_hasLastTime)
        {
            _hasLastTime = true;
            _lastTime = currentTime;
            deltaSeconds = _minIntervalSeconds > 0 ? _minIntervalSeconds : (1.0 / 60.0);
            return true;
        }

        double elapsed = (currentTime - _lastTime).TotalSeconds;
        if (elapsed < _minIntervalSeconds)
        {
            deltaSeconds = 0.0;
            return false;
        }

        _lastTime = currentTime;
        deltaSeconds = elapsed > 0.1 ? 0.1 : elapsed;
        return true;
    }
}

/// <summary>
/// <see cref="CompositionTarget.Rendering"/> を共有し、アクティブなアニメーションが存在する時だけ
/// フレームイベントを購読して各コントロールへ配信する中央クロック。
/// 最大 ~30fps に間引き、アニメーションが完了すると購読を自動解除して不要な毎フレーム処理を完全にゼロにする。
/// </summary>
public static class FrameAnimationClock
{
    /// <summary>クロックの最大フレームレート上限。</summary>
    public const double MaxFps = FrameThrottler.DefaultMaxFps;

    private static readonly List<IAnimatableControl> _activeControls = new();
    private static readonly List<IAnimatableControl> _pendingRemoval = new();
    private static bool _isSubscribed;
    private static FrameThrottler _throttler = new(MaxFps);

    /// <summary>
    /// コントロールをアクティブなアニメーション対象として登録する。
    /// すでに登録済みの場合は再登録されない。
    /// </summary>
    public static void Register(IAnimatableControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (!_activeControls.Contains(control))
        {
            _activeControls.Add(control);
        }
        _pendingRemoval.Remove(control);

        EnsureSubscribed();
    }

    /// <summary>
    /// コントロールの登録を解除する。
    /// </summary>
    public static void Unregister(IAnimatableControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        _activeControls.Remove(control);
        _pendingRemoval.Remove(control);

        if (_activeControls.Count == 0)
        {
            EnsureUnsubscribed();
        }
    }

    private static void EnsureSubscribed()
    {
        if (!_isSubscribed && _activeControls.Count > 0)
        {
            _throttler.Reset();
            CompositionTarget.Rendering += OnRendering;
            _isSubscribed = true;
        }
    }

    private static void EnsureUnsubscribed()
    {
        if (_isSubscribed && _activeControls.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isSubscribed = false;
            _throttler.Reset();
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (_activeControls.Count == 0)
        {
            EnsureUnsubscribed();
            return;
        }

        TimeSpan current = (e is RenderingEventArgs r) ? r.RenderingTime : TimeSpan.Zero;
        if (!_throttler.TryStep(current, out double deltaSeconds))
        {
            // 最大 ~30fps に間引くため、規定インターバル未満のフレームはスキップ
            return;
        }

        _pendingRemoval.Clear();
        for (int i = 0; i < _activeControls.Count; i++)
        {
            IAnimatableControl control = _activeControls[i];
            bool continueAnimating = control.OnAnimationFrame(deltaSeconds);
            if (!continueAnimating)
            {
                _pendingRemoval.Add(control);
            }
        }

        if (_pendingRemoval.Count > 0)
        {
            for (int i = 0; i < _pendingRemoval.Count; i++)
            {
                _activeControls.Remove(_pendingRemoval[i]);
            }
            _pendingRemoval.Clear();

            if (_activeControls.Count == 0)
            {
                EnsureUnsubscribed();
            }
        }
    }
}
