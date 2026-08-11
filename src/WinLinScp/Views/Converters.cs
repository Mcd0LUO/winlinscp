using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WinLinScp.Views;

/// <summary>枚举 → bool（单选按钮用）：值与 ConverterParameter 相等时为 true；ConvertBack 由 true 回写枚举。</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Enum.Parse(targetType, (string)parameter!) : Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>true → 绿色（成功/已连接），false → 深灰（失败/未连接）。</summary>
public sealed class BooleanToBrushConverter : IValueConverter
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Green : Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
