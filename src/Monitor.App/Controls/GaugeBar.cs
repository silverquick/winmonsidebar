using System.Windows;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// 横棒ゲージ。値の変化に追従して即座に幅を再描画する（アニメーションなし）。
/// </summary>
public sealed class GaugeBar : FrameworkElement
{
    private static readonly Brush DefaultForegroundBrush = CreateFrozenSolid(Color.FromRgb(0x4E, 0xC9, 0xF5));
    private static readonly Brush DefaultTrackBrush = CreateFrozenSolid(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(GaugeBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
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

        double percent = Math.Clamp(Value, 0.0, 100.0);
        double fillWidth = width * (percent / 100.0);
        if (fillWidth <= 0)
        {
            return;
        }

        // 前景も同じ角丸半径で描き、幅だけをクリップして丸角を保つ。
        Rect fillClip = new(0, 0, fillWidth, height);
        dc.PushClip(new RectangleGeometry(fillClip));
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
