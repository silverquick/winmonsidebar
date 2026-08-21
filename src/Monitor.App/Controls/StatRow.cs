using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Monitor.App.Controls;

/// <summary>
/// ラベルと値を1行に収めるステータス行。ラベル左寄せ・値右寄せ。
/// これまで <c>StatLabelStyle</c> + <c>StatValueStyle</c> の TextBlock 2つ（2行）で
/// 書かれていた統計値表示を、密度優先で1行にまとめたもの。既定スタイルは
/// <c>Themes\Dark.xaml</c> に置く。
/// </summary>
public sealed class StatRow : Control
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(StatRow),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(string),
        typeof(StatRow),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush),
        typeof(Brush),
        typeof(StatRow),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(Brush),
        typeof(StatRow),
        new FrameworkPropertyMetadata(null));

    static StatRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatRow), new FrameworkPropertyMetadata(typeof(StatRow)));
    }

    /// <summary>左寄せで表示するラベル文字列。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>右寄せで表示する値文字列。</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>値テキストの色。未指定なら既定スタイル（Themes\Dark.xaml）が SidebarForegroundBrush を適用する。</summary>
    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>
    /// ラベルテキストの色。未指定なら既定スタイルが SidebarSubTextBrush を適用する。
    /// メモリの積み上げバーのように、行がグラフ上のセグメントに対応するとき、
    /// ラベルをそのセグメントと同じ色にして凡例の役割を持たせるために使う。
    /// </summary>
    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }
}
