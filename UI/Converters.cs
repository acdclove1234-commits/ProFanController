// ==================================================
// Converters.cs - XAML のバインド用の変換 (ラジオボタンと文字列の値を結び付ける)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows.Data;

namespace ProFanController.UI;

/// <summary>
/// 値が ConverterParameter と同じなら true。ラジオボタンで選ばれたときは、その ConverterParameter を書き戻す。
/// 例: IsChecked="{Binding Mode, Converter={StaticResource Equals}, ConverterParameter=Target}"
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter?.ToString() : Binding.DoNothing;
}
