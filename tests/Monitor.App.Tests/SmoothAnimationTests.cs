using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Controls;

namespace Monitor.App.Tests;

[TestClass]
public sealed class SmoothAnimationTests
{
    // ==========================================
    // EmaFilter テスト
    // ==========================================

    [TestMethod]
    public void EmaFilter_FirstValue_ReturnsRawValueRegardlessOfPreviousOrAlpha()
    {
        double result = EmaFilter.ComputeNext(previousTarget: 50.0, newValue: 100.0, alpha: 0.5, isFirst: true);
        Assert.AreEqual(100.0, result, 1e-9);
    }

    [TestMethod]
    public void EmaFilter_AlphaOne_DisablesSmoothing()
    {
        double result = EmaFilter.ComputeNext(previousTarget: 20.0, newValue: 80.0, alpha: 1.0, isFirst: false);
        Assert.AreEqual(80.0, result, 1e-9);
    }

    [TestMethod]
    public void EmaFilter_AlphaZero_KeepsPreviousTarget()
    {
        double result = EmaFilter.ComputeNext(previousTarget: 42.0, newValue: 99.0, alpha: 0.0, isFirst: false);
        Assert.AreEqual(42.0, result, 1e-9);
    }

    [TestMethod]
    public void EmaFilter_NaNInput_MaintainsPreviousTarget()
    {
        double result = EmaFilter.ComputeNext(previousTarget: 55.0, newValue: double.NaN, alpha: 0.5, isFirst: false);
        Assert.AreEqual(55.0, result, 1e-9);

        double infResult = EmaFilter.ComputeNext(previousTarget: 55.0, newValue: double.PositiveInfinity, alpha: 0.5, isFirst: false);
        Assert.AreEqual(55.0, infResult, 1e-9);
    }

    [TestMethod]
    public void EmaFilter_ComputeSeries_HandlesNaNGracefully()
    {
        float[] source = [10.0f, float.NaN, 30.0f, float.PositiveInfinity];
        float[] dest = new float[4];

        EmaFilter.ComputeSeries(source, dest, alpha: 0.5);

        Assert.IsFalse(float.IsNaN(dest[1]));
        Assert.IsFalse(float.IsInfinity(dest[3]));
        Assert.AreEqual(10.0f, dest[0], 1e-5f);
        // NaN は直前の 10.0f で置き換えられるため dest[1] = 10 + 0.5 * (10 - 10) = 10
        Assert.AreEqual(10.0f, dest[1], 1e-5f);
    }

    // ==========================================
    // ValueInterpolator (指数追従) テスト
    // ==========================================

    [TestMethod]
    public void ValueInterpolator_TauZero_SnapsImmediately()
    {
        double current = 10.0;
        double target = 90.0;
        double next = ValueInterpolator.Follow(current, target, deltaSeconds: 0.016, tauSeconds: 0.0, threshold: 0.1, out bool isComplete);

        Assert.AreEqual(90.0, next, 1e-9);
        Assert.IsTrue(isComplete);
    }

    [TestMethod]
    public void ValueInterpolator_RepeatedSameInput_ConvergesToTarget()
    {
        // レビュー指摘 1: 同値の再通知でも毎フレーム指数追従が進み、生値へ収束すること
        var interpolator = new ValueInterpolator();
        interpolator.SnapTo(0.0);

        // 新しい目標値 100.0 を設定
        interpolator.SetTarget(100.0);
        Assert.IsFalse(interpolator.IsComplete);

        double tau = 0.5; // 0.5 秒の時定数
        double dt = 0.033; // ~30fps の 1 フレーム

        // 毎フレーム同じ目標値 100.0 のままで Update を進める
        int steps = 0;
        while (!interpolator.IsComplete && steps < 200)
        {
            interpolator.Update(dt, tau, threshold: 0.1);
            steps++;
        }

        Assert.IsTrue(interpolator.IsComplete, "目標値が同一でも指数追従により収束して完了すること");
        Assert.AreEqual(100.0, interpolator.CurrentValue, 1e-9);
        Assert.IsTrue(steps > 5 && steps < 150, $"ステップ数が妥当な範囲であること: {steps}");
    }

    [TestMethod]
    public void ValueInterpolator_NaNInput_MaintainsPreviousTarget()
    {
        // レビュー指摘 5: NaN 入力で目標維持
        var interpolator = new ValueInterpolator();
        interpolator.SnapTo(45.0);

        interpolator.SetTarget(double.NaN);
        Assert.AreEqual(45.0, interpolator.TargetValue, 1e-9);

        interpolator.SetTarget(double.PositiveInfinity);
        Assert.AreEqual(45.0, interpolator.TargetValue, 1e-9);
    }

    [TestMethod]
    public void ValueInterpolator_StopCondition_SnapsWhenResidualWithinThreshold()
    {
        double current = 99.95;
        double target = 100.0;
        // 残差 0.05 は threshold 0.1 以下なので即スナップ完了
        double next = ValueInterpolator.Follow(current, target, deltaSeconds: 0.016, tauSeconds: 0.5, threshold: 0.1, out bool isComplete);

        Assert.AreEqual(100.0, next, 1e-9);
        Assert.IsTrue(isComplete);
    }

    // ==========================================
    // ArrayInterpolator テスト
    // ==========================================

    [TestMethod]
    public void ArrayInterpolator_UpdatesAllElementsAndCompletes()
    {
        var interpolator = new ArrayInterpolator();
        interpolator.SnapTo([0.0, 10.0, 20.0]);
        Assert.IsTrue(interpolator.IsComplete);

        interpolator.SetTargets([50.0, 60.0, 70.0]);
        Assert.IsFalse(interpolator.IsComplete);

        for (int i = 0; i < 150; i++)
        {
            interpolator.Update(0.033, 0.5, threshold: 0.1);
            if (interpolator.IsComplete) break;
        }

        Assert.IsTrue(interpolator.IsComplete);
        Assert.AreEqual(50.0, interpolator.GetCurrent(0), 1e-9);
        Assert.AreEqual(60.0, interpolator.GetCurrent(1), 1e-9);
        Assert.AreEqual(70.0, interpolator.GetCurrent(2), 1e-9);
    }

    [TestMethod]
    public void ArrayInterpolator_DifferentLength_SnapsImmediately()
    {
        var interpolator = new ArrayInterpolator();
        interpolator.SnapTo([10.0, 20.0]);
        Assert.AreEqual(2, interpolator.Count);

        // 要素数が変わった場合は即座にスナップ
        interpolator.SetTargets([10.0, 20.0, 30.0, 40.0]);
        Assert.AreEqual(4, interpolator.Count);
        Assert.IsTrue(interpolator.IsComplete);
        Assert.AreEqual(40.0, interpolator.GetCurrent(3), 1e-9);
    }

    [TestMethod]
    public void ArrayInterpolator_NaNElement_MaintainsPreviousTarget()
    {
        var interpolator = new ArrayInterpolator();
        interpolator.SnapTo([10.0, 20.0]);

        interpolator.SetTargets([double.NaN, 30.0]);
        Assert.AreEqual(10.0, interpolator.GetTarget(0), 1e-9);
        Assert.AreEqual(30.0, interpolator.GetTarget(1), 1e-9);
    }

    // ==========================================
    // SparklineLogic: 配列関係判定 (Slide / Append / Reset)
    // ==========================================

    [TestMethod]
    public void SparklineLogic_DetectRelation_DetectsSlide()
    {
        float[] prev = [1f, 2f, 3f, 4f];
        float[] curr = [2f, 3f, 4f, 5f]; // 長さ同じで new[i] == prev[i+1]

        var relation = SparklineLogic.DetectRelation(prev, curr);
        Assert.AreEqual(SparklineArrayRelation.Slide, relation);
    }

    [TestMethod]
    public void SparklineLogic_DetectRelation_DetectsAppend()
    {
        float[] prev = [1f, 2f, 3f];
        float[] curr = [1f, 2f, 3f, 4f]; // 長さ +1 かつ前方一致

        var relation = SparklineLogic.DetectRelation(prev, curr);
        Assert.AreEqual(SparklineArrayRelation.Append, relation);
    }

    [TestMethod]
    public void SparklineLogic_DetectRelation_DetectsReset()
    {
        float[] prev = [1f, 2f, 3f, 4f];
        float[] curr = [9f, 8f, 7f, 6f]; // 不一致

        var relation = SparklineLogic.DetectRelation(prev, curr);
        Assert.AreEqual(SparklineArrayRelation.Reset, relation);

        // 空配列も Reset
        Assert.AreEqual(SparklineArrayRelation.Reset, SparklineLogic.DetectRelation([], curr));
    }

    // ==========================================
    // SparklineLogic: インクリメンタル EMA 平滑化
    // ==========================================

    [TestMethod]
    public void SparklineLogic_IncrementalSeries_Slide_DoesNotAlterPastPoints()
    {
        // 過去の平滑系列をシミュレート
        // 窓 [10, 20, 30, 40]
        float[] smoothed = [10.0f, 15.0f, 22.5f, 31.25f];
        int count = 4;

        // 次の生系列: [20, 30, 40, 50] (1 要素スライド)
        float[] nextRaw = [20.0f, 30.0f, 40.0f, 50.0f];

        SparklineLogic.UpdateIncrementalSeries(nextRaw, smoothed, ref count, SparklineArrayRelation.Slide, smoothing: 0.5);

        // インクリメンタル EMA では、古い要素 [0] は前回の [1] = 15.0f をそのまま保持
        Assert.AreEqual(15.0f, smoothed[0], 1e-5f);
        Assert.AreEqual(22.5f, smoothed[1], 1e-5f);
        Assert.AreEqual(31.25f, smoothed[2], 1e-5f);
        // 新しい末尾要素のみ ComputeNext(31.25, 50.0, 0.5) = 40.625
        Assert.AreEqual(40.625f, smoothed[3], 1e-5f);

        // 参考: 旧来の全再計算（ComputeSeries）では窓先頭の seed = 20.0f から始まるため、
        // 系列全体が異なり、左端が遡って変わってしまうことを検証
        float[] fullRecompute = new float[4];
        EmaFilter.ComputeSeries(nextRaw, fullRecompute, alpha: 0.5);
        // fullRecompute[0] は 20.0f であり、smoothed[0] の 15.0f と一致しない（＝左端が遡って変わらないことが確認できる）
        Assert.AreNotEqual(fullRecompute[0], smoothed[0]);
    }

    [TestMethod]
    public void SparklineLogic_IncrementalSeries_Append_OnlyUpdatesNewSample()
    {
        float[] smoothed = new float[5];
        smoothed[0] = 10f;
        smoothed[1] = 15f;
        int count = 2;

        float[] currRaw = [10f, 20f, 30f]; // 長さ 3 (Append)

        SparklineLogic.UpdateIncrementalSeries(currRaw, smoothed, ref count, SparklineArrayRelation.Append, smoothing: 0.5);

        Assert.AreEqual(3, count);
        Assert.AreEqual(10f, smoothed[0], 1e-5f);
        Assert.AreEqual(15f, smoothed[1], 1e-5f);
        // 新しい末尾: ComputeNext(15, 30, 0.5) = 22.5
        Assert.AreEqual(22.5f, smoothed[2], 1e-5f);
    }

    // ==========================================
    // SparklineLogic: AutoScale スケール追従
    // ==========================================

    [TestMethod]
    public void SparklineLogic_UpdateScale_FastUp_SlowDown()
    {
        double currentScale = 50.0;

        // 上がる時は即時採用 (observedMax = 100 -> targetMax = 100 * 1.15 = 115)
        double up = SparklineLogic.UpdateScale(currentScale, observedMax: 100.0, SparklineArrayRelation.Slide, hasMaxScale: true);
        Assert.AreEqual(115.0, up, 1e-9);

        // 下がる時は 0.3 の係数で緩やかに追従 (observedMax = 20 -> targetMax = 23)
        // newScale = 115 + 0.3 * (23 - 115) = 115 - 27.6 = 87.4
        double down = SparklineLogic.UpdateScale(up, observedMax: 20.0, SparklineArrayRelation.Slide, hasMaxScale: true);
        Assert.AreEqual(87.4, down, 1e-9);

        // リセット時は即時
        double reset = SparklineLogic.UpdateScale(down, observedMax: 20.0, SparklineArrayRelation.Reset, hasMaxScale: true);
        Assert.AreEqual(23.0, reset, 1e-9);
    }

    // ==========================================
    // FrameThrottler: 30fps 間引きテスト
    // ==========================================

    [TestMethod]
    public void FrameThrottler_UnderMinInterval_SkipsFrameAndKeepsLastTime()
    {
        var throttler = new FrameThrottler(maxFps: 30.0);
        // 30fps の最小間隔は約 0.0333 秒 (33.3ms)

        // 初回フレーム (t = 0ms) -> 実行
        bool f1 = throttler.TryStep(TimeSpan.FromMilliseconds(0), out double dt1);
        Assert.IsTrue(f1);
        Assert.IsTrue(dt1 > 0);

        // 2フレーム目 (t = 16ms): 差分 16ms < 33.3ms -> スキップ
        bool f2 = throttler.TryStep(TimeSpan.FromMilliseconds(16), out double dt2);
        Assert.IsFalse(f2);
        Assert.AreEqual(0.0, dt2, 1e-9);

        // 3フレーム目 (t = 34ms): 初回 (0ms) からの差分 34ms >= 33.3ms -> 実行！
        bool f3 = throttler.TryStep(TimeSpan.FromMilliseconds(34), out double dt3);
        Assert.IsTrue(f3);
        Assert.AreEqual(0.034, dt3, 1e-4);
    }

    [TestMethod]
    public void FrameThrottler_CapsDeltaAtMaxThreshold()
    {
        var throttler = new FrameThrottler(maxFps: 30.0);
        throttler.TryStep(TimeSpan.FromSeconds(0), out _);

        // 1秒間の巨大ギャップ (サスペンドや一時停止)
        bool step = throttler.TryStep(TimeSpan.FromSeconds(1.0), out double dt);
        Assert.IsTrue(step);
        // 最大 0.1 秒でクランプされていること
        Assert.AreEqual(0.1, dt, 1e-9);
    }
}
