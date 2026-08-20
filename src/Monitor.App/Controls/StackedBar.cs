using System.Windows;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// 複数セグメントを横一列に並べて塗る積み上げバー（Task Manager の「メモリ構成」相当）。
/// <see cref="Values"/> の各要素を合計に対する比率で幅配分し、対応する <see cref="SegmentBrushes"/> で塗る。
/// セグメント間には 1px の隙間を入れる。
/// 加えて、先頭セグメント（通常「使用中」）の内側に <see cref="OverlayValue"/> 分を右詰めで
/// <see cref="OverlayBrush"/> で重ね塗りできる（Task Manager の「使用中 (圧縮)」の見せ方）。
/// </summary>
public sealed class StackedBar : FrameworkElement
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
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentBrushesProperty = DependencyProperty.Register(
        nameof(SegmentBrushes),
        typeof(IReadOnlyList<Brush>),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(DefaultSegmentBrushes, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverlayValueProperty = DependencyProperty.Register(
        nameof(OverlayValue),
        typeof(double),
        typeof(StackedBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

    protected override void OnRender(DrawingContext dc)
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        IReadOnlyList<double> values = Values;
        double radius = Math.Min(CornerRadius, Math.Min(width, height) / 2.0);
        Rect fullRect = new(0, 0, width, height);

        dc.PushClip(new RectangleGeometry(fullRect, radius, radius));
        dc.DrawRectangle(Track, null, fullRect);

        double total = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] > 0)
            {
                total += values[i];
            }
        }

        if (total > 0)
        {
            IReadOnlyList<Brush> brushes = SegmentBrushes;
            double x = 0;
            double firstSegmentX = 0;
            double firstSegmentWidth = 0;

            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                if (v <= 0)
                {
                    continue;
                }

                double segmentWidth = width * (v / total);
                if (i == 0)
                {
                    firstSegmentX = x;
                    firstSegmentWidth = segmentWidth;
                }

                double drawWidth = Math.Max(0, segmentWidth - GapSize);
                if (drawWidth > 0)
                {
                    Brush brush = i < brushes.Count ? brushes[i] : Brushes.Transparent;
                    dc.DrawRectangle(brush, null, new Rect(x, 0, drawWidth, height));
                }

                x += segmentWidth;
            }

            double overlay = OverlayValue;
            if (overlay > 0 && firstSegmentWidth > 0)
            {
                double overlayWidth = Math.Min(overlay, values[0]) / total * width;
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
