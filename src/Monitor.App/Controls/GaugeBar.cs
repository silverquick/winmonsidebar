using System.Windows;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// 横棒ゲージ。値の更新時に時間ベースの指数追従アニメーションを行い、滑らかに表示幅を変化させる。
/// </summary>
public sealed class GaugeBar : FrameworkElement, IAnimatableControl
{
    private static readonly Brush DefaultForegroundBrush = CreateFrozenSolid(Color.FromRgb(0x4E, 0xC9, 0xF5));
    private static readonly Brush DefaultTrackBrush = CreateFrozenSolid(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(GaugeBar),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty SmoothingTimeProperty = DependencyProperty.Register(
        nameof(SmoothingTime),
        typeof(TimeSpan),
        typeof(GaugeBar),
        new FrameworkPropertyMetadata(TimeSpan.FromSeconds(0.5)));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(Brush),
        typeof(GaugeBar),
        new FrameworkPropertyMetadata(DefaultForegroundBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(GaugeBar),
        new FrameworkPropertyMetadata(DefaultTrackBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(double),
        typeof(GaugeBar),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RectangleGeometry _clipGeometry = new();
    private double _current;
    private double _target;
    private bool _hasTarget;
    private bool _isUnavailable;
    private bool _isComplete = true;

    public GaugeBar()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>入力の生値（0〜100%）。NaN は取得不能（トラックのみ即時表示）。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>指数追従の平滑化時定数 τ（既定 0.5 秒。TimeSpan.Zero で即時反映）。</summary>
    public TimeSpan SmoothingTime
    {
        get => (TimeSpan)GetValue(SmoothingTimeProperty);
        set => SetValue(SmoothingTimeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

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

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GaugeBar bar)
        {
            double raw = (double)e.NewValue;
            if (double.IsNaN(raw))
            {
                bar._isUnavailable = true;
                bar._current = bar._target = 0;
                bar._isComplete = true;
                FrameAnimationClock.Unregister(bar);
                bar.UpdateClipRect();
                bar.InvalidateVisual();
                return;
            }

            bool recovered = bar._isUnavailable;
            bar._isUnavailable = false;
            double sanitized = double.IsFinite(raw) ? Math.Clamp(raw, 0.0, 100.0) : (bar._hasTarget ? bar._target : 0.0);
            bar._target = sanitized;
            bar._hasTarget = true;

            if (recovered || !bar.IsLoaded || !bar.IsVisible || bar.SmoothingTime <= TimeSpan.Zero)
            {
                bar._current = sanitized;
                bar._isComplete = true;
                bar.UpdateClipRect();
                bar.InvalidateVisual();
                return;
            }

            bar._isComplete = false;
            FrameAnimationClock.Register(bar);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasTarget)
        {
            _current = _target;
            _isComplete = true;
        }
        UpdateClipRect();
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hasTarget)
        {
            _current = _target;
            _isComplete = true;
        }
        FrameAnimationClock.Unregister(this);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            if (_hasTarget)
            {
                _current = _target;
                _isComplete = true;
            }
            FrameAnimationClock.Unregister(this);
        }
        else
        {
            if (_hasTarget)
            {
                _current = _target;
                _isComplete = true;
            }
            UpdateClipRect();
            InvalidateVisual();
        }
    }

    bool IAnimatableControl.OnAnimationFrame(double deltaSeconds)
    {
        if (_isComplete)
        {
            return false;
        }

        double width = RenderSize.Width;
        double tau = SmoothingTime.TotalSeconds;
        double threshold = width > 0 ? (0.5 / width) * 100.0 : 0.1;

        _current = ValueInterpolator.Follow(_current, _target, deltaSeconds, tau, threshold, out _isComplete);

        // フレームごとの OnRender を回避するため、保持している非 Freeze の _clipGeometry.Rect だけを更新する。
        // WPF 描画データが同一インスタンスを参照するため、OnRender なしで反映される。
        UpdateClipRect();

        return !_isComplete;
    }

    private void UpdateClipRect()
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (_isUnavailable || width <= 0 || height <= 0)
        {
            _clipGeometry.Rect = Rect.Empty;
            return;
        }

        double displayValue = _hasTarget ? _current : Value;
        double percent = Math.Clamp(displayValue, 0.0, 100.0);
        double fillWidth = width * (percent / 100.0);

        _clipGeometry.Rect = new Rect(0, 0, Math.Max(0, fillWidth), height);
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

        Rect trackRect = new(0, 0, width, height);
        dc.DrawRoundedRectangle(Track, null, trackRect, radius, radius);

        UpdateClipRect();

        // 前景も同じ角丸半径で描き、幅だけをクリップして丸角を保つ。
        // フレームごとの GC アロケーションを防ぐため _clipGeometry を再利用する。
        dc.PushClip(_clipGeometry);
        Rect fullRect = new(0, 0, width, height);
        dc.DrawRoundedRectangle(Foreground, null, fullRect, radius, radius);
        dc.Pop();
    }

    private static Brush CreateFrozenSolid(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
