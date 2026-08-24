using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Monitor.Core.Alerts;

namespace Monitor.App.Converters;

/// <summary>
/// <see cref="AlertLevel"/> を警告色の Brush（<c>AlertCautionBrush</c> / <c>AlertCriticalBrush</c>、
/// <c>Themes\Dark.xaml</c> 参照）へ変換する。
///
/// <see cref="AlertLevel.None"/>（正常）のときは <see cref="Binding.DoNothing"/> を返す。
/// <see cref="DependencyProperty.UnsetValue"/> ではなくこちらを選んだ理由:
/// UnsetValue はバインディングの FallbackValue（未指定ならターゲットプロパティのメタデータ既定値）を
/// ローカル値スロットに書き込む動作になる。ローカル値はスタイルの Setter より優先順位が高いため、
/// 例えば StatRow.ValueBrush のようにメタデータ既定値が null のプロパティでは、Style が設定した
/// 通常時の色（SidebarForegroundBrush 等）を素通りしてローカル値が null になり、テキストが
/// 実質透明になってしまう。Binding.DoNothing はローカル値スロットへの書き込み自体を行わないため、
/// 値の優先順位が正しく Style.Setter まで降りて既定色が保たれる。
/// </summary>
public sealed class AlertLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AlertLevel level || level == AlertLevel.None)
        {
            return Binding.DoNothing;
        }

        string resourceKey = level == AlertLevel.Critical ? "AlertCriticalBrush" : "AlertCautionBrush";
        return Application.Current?.TryFindResource(resourceKey) ?? Binding.DoNothing;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
