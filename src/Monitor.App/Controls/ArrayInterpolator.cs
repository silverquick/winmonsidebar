namespace Monitor.App.Controls;

/// <summary>
/// 複数数値配列（セグメント・コアグリッド等）の時間ベース指数追従状態を管理するヘルパー。
/// フレームごとのメモリ割り当てを行わない再利用バッファ設計。
/// </summary>
public sealed class ArrayInterpolator
{
    private double[] _current = Array.Empty<double>();
    private double[] _targets = Array.Empty<double>();
    private bool _hasValue;
    private bool _isComplete = true;

    public ReadOnlySpan<double> CurrentValues => _current;
    public ReadOnlySpan<double> TargetValues => _targets;
    public int Count => _current.Length;
    public bool IsComplete => _isComplete;
    public bool HasValue => _hasValue;

    public double GetCurrent(int index) => (uint)index < (uint)_current.Length ? _current[index] : 0.0;
    public double GetTarget(int index) => (uint)index < (uint)_targets.Length ? _targets[index] : 0.0;

    /// <summary>
    /// 即座に指定配列へスナップし、追従を完了状態にする。
    /// 要素数が変わった場合や非表示からの復帰時に使用する。
    /// </summary>
    public void SnapTo(IReadOnlyList<double>? newValues)
    {
        int count = newValues?.Count ?? 0;
        EnsureCapacity(count);

        for (int i = 0; i < count; i++)
        {
            double v = newValues![i];
            if (!double.IsFinite(v))
            {
                v = 0.0;
            }

            _current[i] = v;
            _targets[i] = v;
        }

        _isComplete = true;
        _hasValue = count > 0;
    }

    /// <summary>
    /// 新しい目標値配列を設定する。要素数が前回と異なる場合は即座にスナップする。
    /// 入力内の非有限値は前回の目標値を維持する。
    /// </summary>
    public void SetTargets(IReadOnlyList<double>? targets)
    {
        int count = targets?.Count ?? 0;
        if (!_hasValue || count != _current.Length)
        {
            SnapTo(targets);
            return;
        }

        bool anyDiff = false;
        for (int i = 0; i < count; i++)
        {
            double v = targets![i];
            if (!double.IsFinite(v))
            {
                v = _targets[i];
            }

            _targets[i] = v;
            if (Math.Abs(_current[i] - v) > 1e-9)
            {
                anyDiff = true;
            }
        }

        _isComplete = !anyDiff;
    }

    /// <summary>
    /// 時間を経過させ、全要素の値を指数追従で更新する。
    /// </summary>
    /// <param name="deltaSeconds">経過秒数。</param>
    /// <param name="tauSeconds">平滑化時定数 τ（秒）。</param>
    /// <param name="threshold">停止判定の残差閾値。</param>
    /// <returns>追従が完了した場合は true、まだ継続中の場合は false。</returns>
    public bool Update(double deltaSeconds, double tauSeconds, double threshold = 0.1)
    {
        if (_isComplete)
        {
            return true;
        }

        Follow(_current.AsSpan(), _targets.AsSpan(), deltaSeconds, tauSeconds, threshold, out _isComplete);
        return _isComplete;
    }

    /// <summary>
    /// 配列全体の指数追従計算を行う純粋関数。
    /// </summary>
    public static void Follow(
        Span<double> current,
        ReadOnlySpan<double> targets,
        double deltaSeconds,
        double tauSeconds,
        double threshold,
        out bool isComplete)
    {
        int count = Math.Min(current.Length, targets.Length);
        if (count == 0)
        {
            isComplete = true;
            return;
        }

        if (tauSeconds <= 0.0)
        {
            targets[..count].CopyTo(current);
            isComplete = true;
            return;
        }

        if (deltaSeconds <= 0.0)
        {
            bool allWithin = true;
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(targets[i] - current[i]) > threshold)
                {
                    allWithin = false;
                    break;
                }
            }
            if (allWithin)
            {
                targets[..count].CopyTo(current);
                isComplete = true;
            }
            else
            {
                isComplete = false;
            }
            return;
        }

        double factor = 1.0 - Math.Exp(-deltaSeconds / tauSeconds);
        bool allComplete = true;

        for (int i = 0; i < count; i++)
        {
            double target = targets[i];
            double next = current[i] + (target - current[i]) * factor;

            if (Math.Abs(target - next) <= threshold)
            {
                current[i] = target;
            }
            else
            {
                current[i] = next;
                allComplete = false;
            }
        }

        isComplete = allComplete;
    }

    private void EnsureCapacity(int count)
    {
        if (_current.Length != count)
        {
            _current = count > 0 ? new double[count] : Array.Empty<double>();
            _targets = count > 0 ? new double[count] : Array.Empty<double>();
        }
    }
}
