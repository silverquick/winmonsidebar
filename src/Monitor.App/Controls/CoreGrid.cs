using System.Windows;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// CPU の論理コアごとの使用率を格子状のセルで表示する（Task Manager の「論理プロセッサ」表示の簡易版）。
/// 値の更新時に各コアの使用率に対して時間ベースの指数追従アニメーションを行い、
/// 滑らかにバーの高さを変化させる。
/// </summary>
public sealed class CoreGrid : FrameworkElement, IAnimatableControl
{
    private static readonly Brush DefaultLowBrush = CreateFrozenSolid(Color.FromRgb(0x2D, 0x7D, 0x46));
    private static readonly Brush DefaultHighBrush = CreateFrozenSolid(Color.FromRgb(0xE0, 0x3A, 0x3A));
    private Color _paletteLow;
    private Color _paletteHigh;
    private Brush[]? _basePalette;
    private Brush[]? _barPalette;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(Array.Empty<double>(), OnValuesChanged));

    public static readonly DependencyProperty SmoothingTimeProperty = DependencyProperty.Register(
        nameof(SmoothingTime),
        typeof(TimeSpan),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(TimeSpan.FromSeconds(0.15)));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowBrushProperty = DependencyProperty.Register(
        nameof(LowBrush),
        typeof(Brush),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(DefaultLowBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighBrushProperty = DependencyProperty.Register(
        nameof(HighBrush),
        typeof(Brush),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(DefaultHighBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CellGapProperty = DependencyProperty.Register(
        nameof(CellGap),
        typeof(double),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinCellWidthProperty = DependencyProperty.Register(
        nameof(MinCellWidth),
        typeof(double),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly ArrayInterpolator _interpolator = new();
    private double[] _sanitizedBuffer = Array.Empty<double>();

    public CoreGrid()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>指数追従の時定数 τ（既定 0.15 秒。コアごとの細かな変動を残すため短めにし、切り替わりの動きだけを滑らかにする。TimeSpan.Zero で即時反映）。</summary>
    public TimeSpan SmoothingTime
    {
        get => (TimeSpan)GetValue(SmoothingTimeProperty);
        set => SetValue(SmoothingTimeProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public Brush LowBrush
    {
        get => (Brush)GetValue(LowBrushProperty);
        set => SetValue(LowBrushProperty, value);
    }

    public Brush HighBrush
    {
        get => (Brush)GetValue(HighBrushProperty);
        set => SetValue(HighBrushProperty, value);
    }

    public double CellGap
    {
        get => (double)GetValue(CellGapProperty);
        set => SetValue(CellGapProperty, value);
    }

    /// <summary>
    /// 1 セルの最小幅。自動列数はこれを下回らない範囲で、できるだけ 1 行に収めようとする。
    /// </summary>
    public double MinCellWidth
    {
        get => (double)GetValue(MinCellWidthProperty);
        set => SetValue(MinCellWidthProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CoreGrid grid)
        {
            var newValues = (IReadOnlyList<double>?)e.NewValue;
            int count = newValues?.Count ?? 0;

            if (grid._sanitizedBuffer.Length != count)
            {
                grid._sanitizedBuffer = count > 0 ? new double[count] : Array.Empty<double>();
            }

            for (int i = 0; i < count; i++)
            {
                double v = newValues![i];
                double sanitized = double.IsFinite(v) ? Math.Clamp(v, 0.0, 100.0) : grid._interpolator.GetTarget(i);
                grid._sanitizedBuffer[i] = sanitized;
            }

            if (count != grid._interpolator.Count || !grid.IsLoaded || !grid.IsVisible || grid.SmoothingTime <= TimeSpan.Zero)
            {
                grid._interpolator.SnapTo(grid._sanitizedBuffer);
                grid.InvalidateVisual();
                return;
            }

            grid._interpolator.SetTargets(grid._sanitizedBuffer);
            if (!grid._interpolator.IsComplete)
            {
                FrameAnimationClock.Register(grid);
            }
            else
            {
                grid.InvalidateVisual();
            }
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
    }

    bool IAnimatableControl.OnAnimationFrame(double deltaSeconds)
    {
        double tau = SmoothingTime.TotalSeconds;
        // 0.1% 相当の閾値で目標値へスナップ
        bool complete = _interpolator.Update(deltaSeconds, tau, threshold: 0.1);
        InvalidateVisual();
        return !complete;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int count = _interpolator.HasValue ? _interpolator.Count : (Values?.Count ?? 0);
        if (count == 0)
        {
            return;
        }

        double gap = Math.Max(0, CellGap);
        int columns = ResolveColumns(count, width, height, gap);
        int rows = (int)Math.Ceiling(count / (double)columns);

        double cellWidth = (width - gap * (columns - 1)) / columns;
        double cellHeight = (height - gap * (rows - 1)) / rows;
        if (cellWidth <= 0 || cellHeight <= 0)
        {
            return;
        }

        (Brush[] basePalette, Brush[] barPalette) = GetPalettes();

        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int col = i % columns;

            double x = col * (cellWidth + gap);
            double y = row * (cellHeight + gap);

            double rawVal = _interpolator.HasValue ? _interpolator.GetCurrent(i) : (Values != null && i < Values.Count ? Values[i] : 0.0);
            double value = Math.Clamp(rawVal, 0.0, 100.0);
            double t = value / 100.0;
            int paletteIndex = (int)Math.Round(t * 100.0);
            paletteIndex = Math.Clamp(paletteIndex, 0, 100);

            // セル背景（薄い色）
            dc.DrawRectangle(basePalette[paletteIndex], null, new Rect(x, y, cellWidth, cellHeight));

            // セル内の使用率バー（下から上へ伸びる）
            double barHeight = cellHeight * t;
            if (barHeight > 0.5)
            {
                double barY = y + cellHeight - barHeight;
                dc.DrawRectangle(barPalette[paletteIndex], null, new Rect(x, barY, cellWidth, barHeight));
            }
        }
    }

    private (Brush[] basePalette, Brush[] barPalette) GetPalettes()
    {
        Color low = ResolveSolidColor(LowBrush, Color.FromRgb(0x2D, 0x7D, 0x46));
        Color high = ResolveSolidColor(HighBrush, Color.FromRgb(0xE0, 0x3A, 0x3A));

        if (_basePalette is not null && _barPalette is not null && _paletteLow == low && _paletteHigh == high)
        {
            return (_basePalette, _barPalette);
        }

        _basePalette = new Brush[101];
        _barPalette = new Brush[101];

        for (int i = 0; i <= 100; i++)
        {
            double t = i / 100.0;
            byte r = (byte)(low.R + (high.R - low.R) * t);
            byte g = (byte)(low.G + (high.G - low.G) * t);
            byte b = (byte)(low.B + (high.B - low.B) * t);

            // 背景は 15% 不透明度
            _basePalette[i] = CreateFrozenSolid(Color.FromArgb((byte)Math.Round(255 * 0.15), r, g, b));
            // バーは 90% 不透明度
            _barPalette[i] = CreateFrozenSolid(Color.FromArgb((byte)Math.Round(255 * 0.90), r, g, b));
        }

        _paletteLow = low;
        _paletteHigh = high;
        return (_basePalette, _barPalette);
    }

    private static Color ResolveSolidColor(Brush brush, Color fallback)
    {
        if (brush is SolidColorBrush scb)
        {
            return scb.Color;
        }
        return fallback;
    }

    private int ResolveColumns(int count, double width, double height, double gap)
    {
        if (Columns > 0)
        {
            return Math.Clamp(Columns, 1, count);
        }

        double minWidth = Math.Max(1.0, MinCellWidth);
        int maxPossible = (int)Math.Floor((width + gap) / (minWidth + gap));
        maxPossible = Math.Clamp(maxPossible, 1, count);

        // 1 行に収まるならそのまま
        if (maxPossible >= count)
        {
            return count;
        }

        // 収まらない場合は、アスペクト比を考慮して最適な列数を探す
        int bestCols = maxPossible;
        double bestPenalty = double.MaxValue;

        for (int cols = maxPossible; cols >= 1; cols--)
        {
            int rows = (int)Math.Ceiling(count / (double)cols);
            double cw = (width - gap * (cols - 1)) / cols;
            double ch = (height - gap * (rows - 1)) / rows;
            if (cw <= 0 || ch <= 0)
            {
                continue;
            }

            // 正方形に近いほどペナルティ小
            double ratio = cw / ch;
            double penalty = Math.Abs(Math.Log(ratio));
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestCols = cols;
            }
        }

        return Math.Max(1, bestCols);
    }

    private static Brush CreateFrozenSolid(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
