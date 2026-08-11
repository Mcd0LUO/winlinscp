using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinLinScp.Views;

/// <summary>double → GridLength（像素）。任务卡片区高度绑定用。</summary>
public sealed class DoubleToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new GridLength(value is double d && d >= 0 ? d : 84);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
