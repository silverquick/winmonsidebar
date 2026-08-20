using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Monitor.App.Converters;

/// <summary>
/// bool を Visibility へ変換するが、標準の <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>
/// とは逆に true → Collapsed / false → Visible とする。「管理者権限が無いときだけ表示する」ような
/// ブロックの表示切り替えに使う。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
