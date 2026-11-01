namespace Monitor.App.Controls;

/// <summary>
/// 単一数値の時間ベース指数追従（平滑化・補間）を管理するヘルパー。
/// 式: display += (target - display) * (1 - exp(-dt / τ))
/// 純粋な計算ロジックとして動作し、WPF 描画コンテキストに依存しない。
/// </summary>
public struct ValueInterpolator
{
    private double _current;
    private double _target;
    private bool _hasValue;
    private bool _isComplete;

    public ValueInterpolator()
    {
        _current = 0.0;
        _target = 0.0;
        _hasValue = false;
        _isComplete = true;
    }

    public double CurrentValue => _current;
    public double TargetValue => _target;
    public bool HasValue => _hasValue;
    public bool IsComplete => _isComplete;

    /// <summary>
    /// 即座に指定値へスナップし、追従を完了状態にする（初回値や非表示からの復帰時）。
    /// 非有限値（NaN/Infinity）の場合は 0.0 とする。
    /// </summary>
    public void SnapTo(double value)
    {
        if (!double.IsFinite(value))
        {
            value = 0.0;
        }

        _current = value;
        _target = value;
        _hasValue = true;
        _isComplete = true;
    }

    /// <summary>
    /// 新しい目標値を設定する。入力が非有限値の場合は前回の目標値を維持する。
    /// </summary>
    public void SetTarget(double target)
    {
        if (!double.IsFinite(target))
        {
            target = _hasValue ? _target : 0.0;
        }

        if (!_hasValue)
        {
            SnapTo(target);
            return;
        }

        _target = target;
        _isComplete = false;
    }

    /// <summary>
    /// 時間を経過させ、指数追従により値を更新する。
    /// </summary>
    /// <param name="deltaSeconds">経過秒数。</param>
    /// <param name="tauSeconds">平滑化時定数 τ（秒）。0 以下のときは目標値へ即座にスナップする。</param>
    /// <param name="threshold">停止判定の残差閾値。残差がこの値以下になると目標値へスナップして完了する。</param>
    /// <returns>追従が完了した場合は true、まだ継続中の場合は false。</returns>
    public bool Update(double deltaSeconds, double tauSeconds, double threshold = 0.1)
    {
        if (_isComplete)
        {
            return true;
        }

        _current = Follow(_current, _target, deltaSeconds, tauSeconds, threshold, out _isComplete);
        return _isComplete;
    }

    /// <summary>
    /// 時間ベースの指数追従計算を行う純粋関数。
    /// 式: display += (target - display) * (1 - exp(-dt / τ))
    /// </summary>
    public static double Follow(double current, double target, double deltaSeconds, double tauSeconds, double threshold, out bool isComplete)
    {
        if (tauSeconds <= 0.0)
        {
            isComplete = true;
            return target;
        }

        if (deltaSeconds <= 0.0)
        {
            isComplete = Math.Abs(target - current) <= threshold;
            return isComplete ? target : current;
        }

        double factor = 1.0 - Math.Exp(-deltaSeconds / tauSeconds);
        double next = current + (target - current) * factor;

        if (Math.Abs(target - next) <= threshold)
        {
            isComplete = true;
            return target;
        }

        isComplete = false;
        return next;
    }
}
