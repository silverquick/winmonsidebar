using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Monitor.App.Controls;

/// <summary>
/// 時系列の折れ線 + 塗りつぶしを自前描画するスパークライン。
/// 系列のインクリメンタル EMA 平滑化と、コンポジションによる 1 秒間の滑らかな左スクロールアニメーションを行う。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private static readonly Brush DefaultStrokeBrush = CreateFrozenSolid(Color.FromRgb(0x4E, 0xC9, 0xF5));
    private static readonly Brush DefaultFillBrush = CreateDefaultFillBrush();
    private static readonly Pen GridLinePen = CreateFrozenPen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 1.0);

    private readonly TranslateTransform _lineTransform = new();
    private float[] _smoothedBuffer = Array.Empty<float>();
    private int _smoothedCount;
    private float[] _lastRawBuffer = Array.Empty<float>();
    private int _lastRawCount;
    private double _lastStepX;
    private bool _hasPreviousRender;
    private bool _valuesReferenceChanged;
    private bool _shouldSnapToStepX = true;
    private double _currentMaxScale;
    private bool _hasMaxScale;

    private Brush? _cachedStrokeBrush;
    private double _cachedStrokeThickness;
    private Pen? _cachedStrokePen;
    private RectangleGeometry? _cachedClipGeometry;
    private Size _lastClipSize;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(float[]),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<float>(), FrameworkPropertyMetadataOptions.AffectsRender, OnValuesPropertyChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale),
        typeof(bool),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SmoothingProperty = DependencyProperty.Register(
        nameof(Smoothing),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnimationDurationProperty = DependencyProperty.Register(
        nameof(AnimationDuration),
        typeof(TimeSpan),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(TimeSpan.FromSeconds(1)));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(DefaultStrokeBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(DefaultFillBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public Sparkline()
    {
        ClipToBounds = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public float[] Values
    {
        get => (float[])GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    /// <summary>系列の EMA 平滑化係数（0.0〜1.0。1.0 で平滑化なし、既定 1.0）。波形の細かな変動を残すため既定では平滑化しない。</summary>
    public double Smoothing
    {
        get => (double)GetValue(SmoothingProperty);
        set => SetValue(SmoothingProperty, value);
    }

    /// <summary>新サンプル到着時の左スクロールアニメーション所要時間（既定 1 秒）。</summary>
    public TimeSpan AnimationDuration
    {
        get => (TimeSpan)GetValue(AnimationDurationProperty);
        set => SetValue(AnimationDurationProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private static void OnValuesPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Sparkline sparkline && !ReferenceEquals(e.OldValue, e.NewValue))
        {
            sparkline._valuesReferenceChanged = true;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _shouldSnapToStepX = true;
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lineTransform.BeginAnimation(TranslateTransform.XProperty, null);
        _lineTransform.X = 0;
        _hasPreviousRender = false;
        _shouldSnapToStepX = true;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _lineTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _lineTransform.X = 0;
            _hasPreviousRender = false;
            _shouldSnapToStepX = true;
        }
        else
        {
            _shouldSnapToStepX = true;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 背景の薄い水平グリッド線 (25% / 50% / 75%)。
        // グリッド線はスクロールさせず固定位置に描画する。
        DrawGridLine(dc, height * 0.25, width);
        DrawGridLine(dc, height * 0.50, width);
        DrawGridLine(dc, height * 0.75, width);

        float[] values = Values;
        if (values is null || values.Length < 2)
        {
            _lineTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _lineTransform.X = 0;
            _hasPreviousRender = false;
            return;
        }

        int count = values.Length;
        double stepX = count >= 3 ? width / (count - 2) : (count > 1 ? width / (count - 1) : width);

        SparklineArrayRelation relation = SparklineArrayRelation.Reset;

        if (_valuesReferenceChanged)
        {
            _valuesReferenceChanged = false;

            // 新配列と前回配列の関係（Slide / Append / Reset）を判定
            relation = SparklineLogic.DetectRelation(_lastRawBuffer.AsSpan(0, _lastRawCount), values);

            // インクリメンタル EMA 平滑化（再利用バッファで毎フレームの配列アロケーションを防止）
            if (_smoothedBuffer.Length < count)
            {
                _smoothedBuffer = new float[count];
            }

            SparklineLogic.UpdateIncrementalSeries(values, _smoothedBuffer, ref _smoothedCount, relation, Smoothing);

            // 前回の生配列バッファを更新
            if (_lastRawBuffer.Length < count)
            {
                _lastRawBuffer = new float[count];
            }
            values.CopyTo(_lastRawBuffer, 0);
            _lastRawCount = count;

            if (AutoScale)
            {
                double observedMax = double.MinValue;
                for (int i = 0; i < count; i++)
                {
                    if (_smoothedBuffer[i] > observedMax)
                    {
                        observedMax = _smoothedBuffer[i];
                    }
                }
                _currentMaxScale = SparklineLogic.UpdateScale(_currentMaxScale, observedMax, relation, _hasMaxScale);
                _hasMaxScale = true;
            }

            // スクロールアニメーションの開始判定:
            // Values の参照が変わり、かつ「1 スライド」で count >= 3 の場合のみスクロールアニメーションを行う。
            // 履歴充填中 (Append) やリセット、count < 3 の場合は即座に目標位置へスナップ。
            if (!IsLoaded || !IsVisible || AnimationDuration <= TimeSpan.Zero || _shouldSnapToStepX || relation != SparklineArrayRelation.Slide || count < 3)
            {
                _lineTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _lineTransform.X = count >= 3 ? -stepX : 0.0;
                _hasPreviousRender = true;
                _lastStepX = stepX;
                _shouldSnapToStepX = false;
            }
            else
            {
                // 到着間隔の揺れ（遅延・連続到着）時でも見た目が跳ねないよう、現在のトランスフォーム位置から連続性を保つ
                double currentX = _lineTransform.X;
                double startX = _hasPreviousRender && _lastStepX > 0
                    ? Math.Clamp(currentX + _lastStepX, -stepX, stepX)
                    : 0.0;

                _lastStepX = stepX;
                _hasPreviousRender = true;

                DoubleAnimation anim = new()
                {
                    From = startX,
                    To = -stepX,
                    Duration = AnimationDuration,
                    FillBehavior = FillBehavior.HoldEnd
                };
                _lineTransform.BeginAnimation(TranslateTransform.XProperty, anim);
            }
        }
        else if (_shouldSnapToStepX)
        {
            // リサイズや Style 適用、Loaded 等の Values 変化以外の再描画
            _lineTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _lineTransform.X = count >= 3 ? -stepX : 0.0;
            _hasPreviousRender = true;
            _lastStepX = stepX;
            _shouldSnapToStepX = false;
        }

        double min = Minimum;
        double max = AutoScale ? (_hasMaxScale ? _currentMaxScale : Math.Max(Maximum, 1.0)) : Maximum;

        double range = max - min;
        if (range <= 0)
        {
            range = 1;
        }

        // count >= 3 のとき、最新サンプル（count - 1）を 1 目盛り分右にはみ出させてジオメトリを作成する。
        // count - 2 番目の点がちょうど width に位置し、アニメーション（0 → -stepX）によって
        // 次の 1 秒かけて最新サンプルが右端（width）へ滑らかに入り込んでくる。
        // count < 3 のときは静的描画（スクロールなし、stepX = width / (count - 1)）。
        StreamGeometry fillGeometry = new();
        StreamGeometry strokeGeometry = new();
        using (StreamGeometryContext fill = fillGeometry.Open())
        using (StreamGeometryContext stroke = strokeGeometry.Open())
        {
            Point first = ToPoint(0);
            fill.BeginFigure(new Point(first.X, height), isFilled: true, isClosed: true);
            fill.LineTo(first, isStroked: false, isSmoothJoin: false);
            stroke.BeginFigure(first, isFilled: false, isClosed: false);
            Point last = first;
            for (int i = 1; i < count; i++)
            {
                last = ToPoint(i);
                fill.LineTo(last, isStroked: false, isSmoothJoin: false);
                stroke.LineTo(last, isStroked: true, isSmoothJoin: true);
            }
            fill.LineTo(new Point(last.X, height), isStroked: false, isSmoothJoin: false);

            Point ToPoint(int index)
            {
                float val = index < _smoothedCount ? _smoothedBuffer[index] : (index < values.Length ? values[index] : 0f);
                double normalized = Math.Clamp((val - min) / range, 0.0, 1.0);
                return new Point(index * stepX, height - normalized * height);
            }
        }

        fillGeometry.Freeze();
        strokeGeometry.Freeze();

        // クリップジオメトリをキャッシュ
        if (_cachedClipGeometry is null || _lastClipSize != RenderSize)
        {
            var clip = new RectangleGeometry(new Rect(0, 0, width, height));
            clip.Freeze();
            _cachedClipGeometry = clip;
            _lastClipSize = RenderSize;
        }

        dc.PushClip(_cachedClipGeometry);
        dc.PushTransform(_lineTransform);
        dc.DrawGeometry(Fill, null, fillGeometry);
        dc.DrawGeometry(null, GetStrokePen(), strokeGeometry);
        dc.Pop();
        dc.Pop();
    }

    private Pen GetStrokePen()
    {
        if (_cachedStrokePen is not null && ReferenceEquals(_cachedStrokeBrush, Stroke) && _cachedStrokeThickness == StrokeThickness)
        {
            return _cachedStrokePen;
        }

        var pen = new Pen(Stroke, StrokeThickness);
        if (pen.CanFreeze) pen.Freeze();
        _cachedStrokeBrush = Stroke;
        _cachedStrokeThickness = StrokeThickness;
        _cachedStrokePen = pen;
        return pen;
    }

    private static void DrawGridLine(DrawingContext dc, double y, double width)
    {
        dc.DrawLine(GridLinePen, new Point(0, y), new Point(width, y));
    }

    private static Brush CreateFrozenSolid(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }

    private static Brush CreateDefaultFillBrush()
    {
        Color baseColor = Color.FromRgb(0x4E, 0xC9, 0xF5);
        Color top = Color.FromArgb((byte)Math.Round(255 * 0.22), baseColor.R, baseColor.G, baseColor.B);
        Color bottom = Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B);

        LinearGradientBrush brush = new()
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(top, 0.0));
        brush.GradientStops.Add(new GradientStop(bottom, 1.0));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        Pen pen = new(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
