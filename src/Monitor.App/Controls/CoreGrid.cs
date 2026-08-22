using System.Windows;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// CPU の論理コアごとの使用率を格子状のセルで表示する（Task Manager の「論理プロセッサ」表示の簡易版）。
/// </summary>
public sealed class CoreGrid : FrameworkElement
{
    private static readonly Brush DefaultLowBrush = CreateFrozenSolid(Color.FromRgb(0x2D, 0x7D, 0x46));
    private static readonly Brush DefaultHighBrush = CreateFrozenSolid(Color.FromRgb(0xE0, 0x3A, 0x3A));

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(CoreGrid),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

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

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
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

    protected override void OnRender(DrawingContext dc)
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        IReadOnlyList<double> values = Values;
        int count = values?.Count ?? 0;
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

        Brush low = LowBrush;
        Brush high = HighBrush;

        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int col = i % columns;

            double x = col * (cellWidth + gap);
            double y = row * (cellHeight + gap);

            double value = Math.Clamp(values![i], 0.0, 100.0);
            double t = value / 100.0;
            Color cellColor = Lerp(GetColor(low), GetColor(high), t);

            Rect cellRect = new(x, y, cellWidth, cellHeight);

            // セル全体を淡い色で塗って土台にする。
            Brush baseBrush = CreateFrozenSolid(Color.FromArgb(0x40, cellColor.R, cellColor.G, cellColor.B));
            dc.DrawRectangle(baseBrush, null, cellRect);

            // 使用率バーを下から上に伸ばして描く。
            double barHeight = cellHeight * t;
            if (barHeight > 0)
            {
                Rect barRect = new(x, y + (cellHeight - barHeight), cellWidth, barHeight);
                Brush barBrush = CreateFrozenSolid(cellColor);
                dc.DrawRectangle(barBrush, null, barRect);
            }
        }
    }

    private int ResolveColumns(int count, double width, double height, double gap)
    {
        int columns = Columns;
        if (columns > 0)
        {
            return Math.Min(columns, count);
        }

        double minCellWidth = Math.Max(1.0, MinCellWidth);

        // まず横一列に収まるかを試す。論理コアの一覧は横一列が最も読みやすく、
        // 「12 個中 11 個までが 1 行目、12 個目だけ 2 行目」のような半端な折り返しを避けたい。
        // 以前はセルが正方形に近くなる列数（sqrt(count * 幅/高さ)）だけを見ていたため、
        // 幅 316 / 高さ 30 / 12 コアで 11 列となり、まさにその折り返しが起きていた。
        if ((width - gap * (count - 1)) / count >= minCellWidth)
        {
            return count;
        }

        // 1 行に入らない場合は正方形に近い列数を起点にしつつ、最終行が極端に空く配置を避ける。
        double aspect = width / Math.Max(height, 1.0);
        int ideal = Math.Clamp((int)Math.Round(Math.Sqrt(count * aspect)), 1, count);

        int best = ideal;
        int bestWaste = int.MaxValue;
        for (int c = Math.Max(1, ideal - 3); c <= Math.Min(count, ideal + 3); c++)
        {
            if ((width - gap * (c - 1)) / c < minCellWidth)
            {
                continue;
            }

            int rows = (int)Math.Ceiling(count / (double)c);
            int waste = (c * rows) - count;
            if (waste < bestWaste ||
                (waste == bestWaste && Math.Abs(c - ideal) < Math.Abs(best - ideal)))
            {
                best = c;
                bestWaste = waste;
            }
        }

        return best;
    }

    private static Color GetColor(Brush brush)
    {
        return brush is SolidColorBrush solid ? solid.Color : Colors.Gray;
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte r = (byte)Math.Round(from.R + (to.R - from.R) * t);
        byte g = (byte)Math.Round(from.G + (to.G - from.G) * t);
        byte b = (byte)Math.Round(from.B + (to.B - from.B) * t);
        return Color.FromRgb(r, g, b);
    }

    private static Brush CreateFrozenSolid(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
