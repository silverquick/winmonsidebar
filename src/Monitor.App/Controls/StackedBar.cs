using System.Windows;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// 複数セグメントを横一列に並べて塗る積み上げバー（Task Manager の「メモリ構成」相当）。
/// 値の更新時に各セグメントおよびオーバーレイ値に対して時間ベースの指数追従アニメーションを行い、
/// 滑らかに比率と幅を変化させる。
/// </summary>
public sealed class StackedBar : FrameworkElement, IAnimatableControl
{
    private static readonly Brush DefaultTrackBrush = CreateFrozenSolid(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DefaultSegmentBrush = CreateFrozenSolid(Color.FromRgb(0x4E, 0xC9, 0xF5));
    private static readonly Brush DefaultOverlayBrush = CreateFrozenSolid(Color.FromRgb(0x2E, 0x8B, 0x57));
    private static readonly IReadOnlyList<Brush> DefaultSegmentBrushes = new[] { DefaultSegmentBrush };

    private const double GapSize = 1.0;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(Array.Empty<double>(), OnValuesChanged));

    public static readonly DependencyProperty OverlayValueProperty = DependencyProperty.Register(
        nameof(OverlayValue),
        typeof(double),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(0.0, OnOverlayValueChanged));

    public static readonly DependencyProperty SmoothingTimeProperty = DependencyProperty.Register(
        nameof(SmoothingTime),
        typeof(TimeSpan),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(TimeSpan.FromSeconds(0.5)));

    public static readonly DependencyProperty SegmentBrushesProperty = DependencyProperty.Register(
        nameof(SegmentBrushes),
        typeof(IReadOnlyList<Brush>),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(DefaultSegmentBrushes, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverlayBrushProperty = DependencyProperty.Register(
        nameof(OverlayBrush),
        typeof(Brush),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(DefaultOverlayBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(DefaultTrackBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(double),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly ArrayInterpolator _interpolator = new();
    private ValueInterpolator _overlayInterpolator = new();
    private double[] _sanitizedBuffer = Array.Empty<double>();

    private RectangleGeometry? _cachedClipGeometry;
    private Size _lastClipSize;
    private double _lastClipRadius;

    public StackedBar()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>各セグメントのバイト数（または任意の比率値）。負値・0 のセグメントは幅を持たない。</summary>
    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary><see cref="Values"/> と同じ並び順のブラシ。要素が足りない分は透明として扱う。</summary>
    public IReadOnlyList<Brush> SegmentBrushes
    {
        get => (IReadOnlyList<Brush>)GetValue(SegmentBrushesProperty);
        set => SetValue(SegmentBrushesProperty, value);
    }

    /// <summary>先頭セグメント（<see cref="Values"/>[0]）の内側に右詰めで重ね塗りする値（例: 圧縮済みバイト数）。
    /// 0 以下、または先頭セグメントが無ければ何も描画しない。</summary>
    public double OverlayValue
    {
        get => (double)GetValue(OverlayValueProperty);
        set => SetValue(OverlayValueProperty, value);
    }

    public Brush OverlayBrush
    {
        get => (Brush)GetValue(OverlayBrushProperty);
        set => SetValue(OverlayBrushProperty, value);
    }

    /// <summary>セグメントで埋まらない残り領域の背景。</summary>
    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>指数追従の平滑化時定数 τ（既定 0.5 秒。TimeSpan.Zero で即時反映）。</summary>
    public TimeSpan SmoothingTime
    {
        get => (TimeSpan)GetValue(SmoothingTimeProperty);
        set => SetValue(SmoothingTimeProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StackedBar bar)
        {
            var newValues = (IReadOnlyList<double>?)e.NewValue;
            int count = newValues?.Count ?? 0;

            if (bar._sanitizedBuffer.Length != count)
            {
                bar._sanitizedBuffer = count > 0 ? new double[count] : Array.Empty<double>();
            }

            for (int i = 0; i < count; i++)
            {
                double v = newValues![i];
                bar._sanitizedBuffer[i] = double.IsFinite(v) ? v : bar._interpolator.GetTarget(i);
            }

            if (count != bar._interpolator.Count || !bar.IsLoaded || !bar.IsVisible || bar.SmoothingTime <= TimeSpan.Zero)
            {
                // 配列長変化時や未ロード・非表示・時定数 0 のときは即座にスナップ
                bar._interpolator.SnapTo(bar._sanitizedBuffer);
                bar.InvalidateVisual();
                return;
            }

            bar._interpolator.SetTargets(bar._sanitizedBuffer);
            bar.CheckStartAnimation();
        }
    }

    private static void OnOverlayValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StackedBar bar)
        {
            double raw = (double)e.NewValue;
            double sanitized = double.IsFinite(raw) ? raw : bar._overlayInterpolator.TargetValue;

            if (!bar.IsLoaded || !bar.IsVisible || bar.SmoothingTime <= TimeSpan.Zero)
            {
                bar._overlayInterpolator.SnapTo(sanitized);
                bar.InvalidateVisual();
                return;
            }

            bar._overlayInterpolator.SetTarget(sanitized);
            bar.CheckStartAnimation();
        }
    }

    private void CheckStartAnimation()
    {
        if (!_interpolator.IsComplete || !_overlayInterpolator.IsComplete)
        {
            FrameAnimationClock.Register(this);
        }
        else
        {
            InvalidateVisual();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SnapToTargets();
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SnapToTargets();
        FrameAnimationClock.Unregister(this);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            SnapToTargets();
            FrameAnimationClock.Unregister(this);
        }
        else
        {
            SnapToTargets();
            InvalidateVisual();
        }
    }

    private void SnapToTargets()
    {
        if (_interpolator.HasValue)
        {
            int count = _interpolator.Count;
            if (_sanitizedBuffer.Length != count)
            {
                _sanitizedBuffer = count > 0 ? new double[count] : Array.Empty<double>();
            }
            for (int i = 0; i < count; i++)
            {
                _sanitizedBuffer[i] = _interpolator.GetTarget(i);
            }
            _interpolator.SnapTo(_sanitizedBuffer);
        }
        if (_overlayInterpolator.HasValue)
        {
            _overlayInterpolator.SnapTo(_overlayInterpolator.TargetValue);
        }
    }

    bool IAnimatableControl.OnAnimationFrame(double deltaSeconds)
    {
        double width = RenderSize.Width;
        double total = 0.0;
        int count = _interpolator.Count;
        for (int i = 0; i < count; i++)
        {
            double v = _interpolator.GetTarget(i);
            if (v > 0) total += v;
        }

        // 残差が描画上 0.5px 未満になったら目標値へスナップ
        double threshold = (total > 0 && width > 0) ? Math.Max(1.0, (0.5 / width) * total) : 1e-4;
        double tau = SmoothingTime.TotalSeconds;

        bool vComplete = _interpolator.Update(deltaSeconds, tau, threshold);
        bool oComplete = _overlayInterpolator.Update(deltaSeconds, tau, threshold);

        InvalidateVisual();
        return !(vComplete && oComplete);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double radius = Math.Min(CornerRadius, Math.Min(width, height) / 2.0);
        Rect fullRect = new(0, 0, width, height);

        // クリップジオメトリをキャッシュして毎フレームの GC アロケーションを防止
        if (_cachedClipGeometry is null || _lastClipSize != RenderSize || Math.Abs(_lastClipRadius - radius) > 1e-4)
        {
            var geom = new RectangleGeometry(fullRect, radius, radius);
            geom.Freeze();
            _cachedClipGeometry = geom;
            _lastClipSize = RenderSize;
            _lastClipRadius = radius;
        }

        dc.PushClip(_cachedClipGeometry);
        dc.DrawRectangle(Track, null, fullRect);

        int count = _interpolator.HasValue ? _interpolator.Count : (Values?.Count ?? 0);

        double total = 0;
        for (int i = 0; i < count; i++)
        {
            double val = _interpolator.HasValue ? _interpolator.GetCurrent(i) : Values![i];
            if (val > 0)
            {
                total += val;
            }
        }

        if (total > 0)
        {
            IReadOnlyList<Brush> brushes = SegmentBrushes;
            double x = 0;
            double firstSegmentX = 0;
            double firstSegmentWidth = 0;
            double firstSegmentVal = 0;

            for (int i = 0; i < count; i++)
            {
                double val = _interpolator.HasValue ? _interpolator.GetCurrent(i) : Values![i];
                if (val <= 0)
                {
                    continue;
                }

                double segmentWidth = width * (val / total);
                if (firstSegmentWidth <= 0 && i == 0)
                {
                    firstSegmentX = x;
                    firstSegmentWidth = segmentWidth;
                    firstSegmentVal = val;
                }

                double drawWidth = Math.Max(0, segmentWidth - GapSize);
                if (drawWidth > 0)
                {
                    Brush brush = i < brushes.Count ? brushes[i] : Brushes.Transparent;
                    dc.DrawRectangle(brush, null, new Rect(x, 0, drawWidth, height));
                }

                x += segmentWidth;
            }

            double overlay = _overlayInterpolator.HasValue ? _overlayInterpolator.CurrentValue : OverlayValue;
            if (overlay > 0 && firstSegmentWidth > 0 && firstSegmentVal > 0)
            {
                double overlayWidth = Math.Min(overlay, firstSegmentVal) / total * width;
                overlayWidth = Math.Min(overlayWidth, firstSegmentWidth);
                if (overlayWidth > 0)
                {
                    double overlayX = firstSegmentX + firstSegmentWidth - overlayWidth;
                    dc.DrawRectangle(OverlayBrush, null, new Rect(overlayX, 0, overlayWidth, height));
                }
            }
        }

        dc.Pop();
    }

    private static Brush CreateFrozenSolid(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
