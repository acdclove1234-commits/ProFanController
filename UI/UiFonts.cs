// ==================================================
// UiFonts.cs - 埋め込みフォント (Montserrat + Zen Maru Gothic) をコードから使うための共通定義
// Dependencies: WPF / .NET 8 (MIT License), Montserrat / ProFan Maru（Zen Maru Gothic の改変版）(SIL OFL 1.1)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows.Media;

namespace ProFanController.UI;

public static class UiFonts
{
    /// <summary>グラフなど OnRender で描く文字用。XAML の UiFont と同じ組み合わせ</summary>
    public static readonly FontFamily Family = new(
        new Uri("pack://application:,,,/"),
        "./Assets/Fonts/#Montserrat, ./Assets/Fonts/#ProFan Maru, Yu Gothic UI");
}
