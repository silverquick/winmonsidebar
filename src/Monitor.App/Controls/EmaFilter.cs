namespace Monitor.App.Controls;

/// <summary>
/// 表示値の揺れを抑える指数移動平均 (EMA: Exponential Moving Average) 計算ヘルパー。
/// コントロール内部の描画値にのみ適用され、生値データは変更しない。
/// </summary>
public static class EmaFilter
{
    public const double DefaultSmoothing = 0.5;

    /// <summary>
    /// 単一値の次の EMA 目標値を計算する。
    /// 式: 描画目標値 = 前の目標値 + α * (新値 - 前の目標値)
    /// </summary>
    /// <param name="previousTarget">前回の EMA 目標値。</param>
    /// <param name="newValue">新しく届いた生値（非有限値の場合は前回の目標値を維持）。</param>
    /// <param name="alpha">平滑化係数（0.0〜1.0。1.0 で平滑化なし）。</param>
    /// <param name="isFirst">初回値か否か。初回のときは生値をそのまま採用する。</param>
    /// <returns>描画目標値。</returns>
    public static double ComputeNext(double previousTarget, double newValue, double alpha, bool isFirst)
    {
        double prev = double.IsFinite(previousTarget) ? previousTarget : 0.0;

        if (!double.IsFinite(newValue))
        {
            newValue = prev;
        }

        if (isFirst)
        {
            return newValue;
        }

        if (!double.IsFinite(alpha) || alpha >= 1.0)
        {
            return newValue;
        }

        if (alpha <= 0.0)
        {
            return prev;
        }

        return prev + alpha * (newValue - prev);
    }

    /// <summary>
    /// 時系列配列（最古〜最新）に対して EMA を適用した系列を計算する（再利用バッファ用）。
    /// 非有限値（NaN/Infinity）は直前の有効値で置き換えて平滑化する。
    /// </summary>
    /// <param name="source">入力の時系列配列。</param>
    /// <param name="destination">計算結果を格納するバッファ。</param>
    /// <param name="alpha">平滑化係数（0.0〜1.0。1.0 で平滑化なし）。</param>
    public static void ComputeSeries(ReadOnlySpan<float> source, Span<float> destination, double alpha)
    {
        if (source.IsEmpty || destination.IsEmpty)
        {
            return;
        }

        int count = Math.Min(source.Length, destination.Length);
        float first = float.IsFinite(source[0]) ? source[0] : 0f;
        destination[0] = first;

        if (!double.IsFinite(alpha) || alpha >= 1.0)
        {
            float last = first;
            for (int i = 0; i < count; i++)
            {
                float s = source[i];
                float valid = float.IsFinite(s) ? s : last;
                destination[i] = valid;
                last = valid;
            }
            return;
        }

        float a = (float)Math.Clamp(alpha, 0.0, 1.0);
        if (a <= 0.0f)
        {
            for (int i = 1; i < count; i++)
            {
                destination[i] = destination[0];
            }
            return;
        }

        float lastValid = first;
        for (int i = 1; i < count; i++)
        {
            float raw = float.IsFinite(source[i]) ? source[i] : lastValid;
            lastValid = raw;
            destination[i] = destination[i - 1] + a * (raw - destination[i - 1]);
        }
    }
}
