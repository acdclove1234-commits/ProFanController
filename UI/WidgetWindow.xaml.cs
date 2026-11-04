// ==================================================
// WidgetWindow.xaml.cs - ミニウィジェットの動作 (位置と不透明度の保存)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProFanController.UI;

public partial class WidgetWindow : Window
{
    /// <summary>位置や不透明度が変わったとき（設定に保存する）</summary>
    public event Action<double, double, double>? LayoutChanged;
    public event Action? OpenMainRequested;
    public event Action? CloseRequested;

    public event Action<string>? CommandRequested;

    public WidgetWindow(IEnumerable temps, IEnumerable metrics, IEnumerable fans, WidgetConfig cfg)
    {
        InitializeComponent();
        TempList.ItemsSource = temps;
        MetricList.ItemsSource = metrics;
        FanList.ItemsSource = fans;
        Root.Opacity = cfg.Opacity;

        if (cfg.Left is double left && cfg.Top is double top && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            // 初回は画面右上に出す
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - 280;
            Top = SystemParameters.WorkArea.Top + 40;
        }
    }

    public void SetProfile(string text) => ProfileText.Text = text;

    public void SetQuickStatus(string? text)
    {
        QuickText.Text = text ?? "";
        QuickText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCommand(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string cmd }) CommandRequested?.Invoke(cmd);
    }

    private static bool IsOnScreen(double left, double top)
        => left >= SystemParameters.VirtualScreenLeft - 50 && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
           && top >= SystemParameters.VirtualScreenTop - 10 && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        LayoutChanged?.Invoke(Left, Top, Root.Opacity);
    }

    private void OnOpacity(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            Root.Opacity = v;
            LayoutChanged?.Invoke(Left, Top, v);
        }
    }

    private void OnOpenMain(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
