namespace Monitor.App.Controls;

/// <summary>
/// 新配列と前回配列の差分関係。
/// </summary>
public enum SparklineArrayRelation
{
    /// <summary>全再計算・スナップ（長さ不整合や不連続な変化時）。</summary>
    Reset,
    /// <summary>リングバッファ充填中の末尾追加（前方一致かつ長さ +1）。</summary>
    Append,
    /// <summary>リングバッファ満杯時の1要素シフト（同じ長さで new[i] == prev[i+1]）。</summary>
    Slide,
}

/// <summary>
/// スパークラインの時系列データ判定、インクリメンタル平滑化、動的スケール追従を行う純粋ロジックヘルパー。
/// WPF 描画コンテキストに依存せず単体テスト可能。
/// </summary>
public static class SparklineLogic
{
    /// <summary>
    /// 前回の生配列と今回の生配列を比較し、関係（Slide / Append / Reset）を判定する。
    /// </summary>
    public static SparklineArrayRelation DetectRelation(ReadOnlySpan<float> prev, ReadOnlySpan<float> current)
    {
        if (prev.IsEmpty || current.IsEmpty)
        {
            return SparklineArrayRelation.Reset;
        }

        // 履歴充填中: 長さ +1 かつ前方一致
        if (current.Length == prev.Length + 1)
        {
            for (int i = 0; i < prev.Length; i++)
            {
                if (Math.Abs(prev[i] - current[i]) > 1e-6f)
                {
                    return SparklineArrayRelation.Reset;
                }
            }
            return SparklineArrayRelation.Append;
        }

        // 1 スライド: 同じ長さで new[i] == prev[i+1]
        if (current.Length == prev.Length)
        {
            for (int i = 0; i < current.Length - 1; i++)
            {
                if (Math.Abs(current[i] - prev[i + 1]) > 1e-6f)
                {
                    return SparklineArrayRelation.Reset;
                }
            }
            return SparklineArrayRelation.Slide;
        }

        return SparklineArrayRelation.Reset;
    }

    /// <summary>
    /// 平滑化系列をインクリメンタルに更新する。
    /// スライド時は前回系列を 1 つずらしてコピーし新サンプルのみ ComputeNext。
    /// 追記時は末尾のみ計算。リセット時のみ全再計算。
    /// </summary>
    public static void UpdateIncrementalSeries(
        ReadOnlySpan<float> currentValues,
        Span<float> smoothedBuffer,
        ref int smoothedCount,
        SparklineArrayRelation relation,
        double smoothing)
    {
        int count = currentValues.Length;
        if (count == 0)
        {
            smoothedCount = 0;
            return;
        }

        if (relation == SparklineArrayRelation.Slide && smoothedCount >= count && count > 1)
        {
            // 前回平滑系列を 1 つ左へずらす（左端の古い値は遡って変化しない）
            smoothedBuffer.Slice(1, count - 1).CopyTo(smoothedBuffer[..(count - 1)]);

            float raw = currentValues[count - 1];
            float prevSmoothed = smoothedBuffer[count - 2];
            float validRaw = float.IsFinite(raw) ? raw : prevSmoothed;

            smoothedBuffer[count - 1] = (float)EmaFilter.ComputeNext(prevSmoothed, validRaw, smoothing, isFirst: false);
            smoothedCount = count;
            return;
        }

        if (relation == SparklineArrayRelation.Append && smoothedCount == count - 1 && count > 1)
        {
            // 履歴充填中: 既存の平滑系列はそのまま維持し、末尾サンプルのみ計算
            float raw = currentValues[count - 1];
            float prevSmoothed = smoothedBuffer[count - 2];
            float validRaw = float.IsFinite(raw) ? raw : prevSmoothed;

            smoothedBuffer[count - 1] = (float)EmaFilter.ComputeNext(prevSmoothed, validRaw, smoothing, isFirst: false);
            smoothedCount = count;
            return;
        }

        // リセット時: 全再計算
        float first = currentValues[0];
        float lastValid = float.IsFinite(first) ? first : 0f;
        smoothedBuffer[0] = lastValid;

        for (int i = 1; i < count; i++)
        {
            float raw = currentValues[i];
            float validRaw = float.IsFinite(raw) ? raw : lastValid;
            lastValid = validRaw;
            smoothedBuffer[i] = (float)EmaFilter.ComputeNext(smoothedBuffer[i - 1], validRaw, smoothing, isFirst: false);
        }

        smoothedCount = count;
    }

    /// <summary>
    /// AutoScale の縦スケール上限を計算する。
    /// 観測最大値が上がる時は即採用、下がる時はサンプルごとに緩やかに追従（0.3）させて秒ごとのスケール跳ねを抑制する。
    /// リセット時は即時更新。
    /// </summary>
    public static double UpdateScale(
        double currentMaxScale,
        double observedMax,
        SparklineArrayRelation relation,
        bool hasMaxScale)
    {
        double targetMax = Math.Max(observedMax * 1.15, 1.0);

        if (!hasMaxScale || relation == SparklineArrayRelation.Reset)
        {
            return targetMax;
        }

        if (targetMax >= currentMaxScale)
        {
            // 上がる時は即採用
            return targetMax;
        }

        // 下がる時は緩やかに追従 (例: 新スケール = 旧 + 0.3 * (観測 - 旧))
        return currentMaxScale + 0.3 * (targetMax - currentMaxScale);
    }
}
