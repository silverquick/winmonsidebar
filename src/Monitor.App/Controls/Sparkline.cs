using System.Windows;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// 時系列の折れ線 + 塗りつぶしを自前描画するスパークライン。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private static readonly Brush DefaultStrokeBrush = CreateFrozenSolid(Color.FromRgb(0x4E, 0xC9, 0xF5));
    private static readonly Brush DefaultFillBrush = CreateDefaultFillBrush();
    private static readonly Pen GridLinePen = CreateFrozenPen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 1.0);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(float[]),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<float>(), FrameworkPropertyMetadataOptions.AffectsRender));

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

    protected override void OnRender(DrawingContext dc)
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 背景の薄い水平グリッド線 (25% / 50% / 75%)。
        DrawGridLine(dc, height * 0.25, width);
        DrawGridLine(dc, height * 0.50, width);
        DrawGridLine(dc, height * 0.75, width);

        float[] values = Values;
        if (values is null || values.Length < 2)
        {
            return;
        }

        double min = Minimum;
        double max = Maximum;

        if (AutoScale)
        {
            double observedMax = double.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > observedMax)
                {
                    observedMax = values[i];
                }
            }

            max = Math.Max(observedMax * 1.15, 1.0);
        }

        double range = max - min;
        if (range <= 0)
        {
            range = 1;
        }

        int count = values.Length;
        double stepX = width / (count - 1);

        Point[] points = new Point[count];
        for (int i = 0; i < count; i++)
        {
            double normalized = (values[i] - min) / range;
            normalized = Math.Clamp(normalized, 0.0, 1.0);
            double x = i * stepX;
            double y = height - normalized * height;
            points[i] = new Point(x, y);
        }

        StreamGeometry fillGeometry = new();
        using (StreamGeometryContext ctx = fillGeometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, height), isFilled: true, isClosed: true);
            ctx.LineTo(points[0], isStroked: false, isSmoothJoin: false);
            for (int i = 1; i < count; i++)
            {
                ctx.LineTo(points[i], isStroked: false, isSmoothJoin: false);
            }

            ctx.LineTo(new Point(points[count - 1].X, height), isStroked: false, isSmoothJoin: false);
        }

        fillGeometry.Freeze();
        dc.DrawGeometry(Fill, null, fillGeometry);

        StreamGeometry strokeGeometry = new();
        using (StreamGeometryContext ctx = strokeGeometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (int i = 1; i < count; i++)
            {
                ctx.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }

        strokeGeometry.Freeze();
        Pen strokePen = new(Stroke, StrokeThickness);
        if (strokePen.CanFreeze)
        {
            strokePen.Freeze();
        }

        dc.DrawGeometry(null, strokePen, strokeGeometry);
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
