// ==================================================
// Sparkline.cs - カード用の小さな推移グラフ (直近の値を自動スケールで描画)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly SolidColorBrush DefaultStroke = CreateDefaultStroke();

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(SolidColorBrush), typeof(Sparkline),
        new FrameworkPropertyMetadata(DefaultStroke, FrameworkPropertyMetadataOptions.AffectsRender));

    private static SolidColorBrush CreateDefaultStroke()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));
        b.Freeze();
        return b;
    }

    /// <summary>描画する値の列。毎回新しい配列を渡すと再描画される（XAML の制約で配列型ではなく IReadOnlyList にしている）</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public SolidColorBrush Stroke
    {
        get => (SolidColorBrush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values;
        if (v is null || v.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        double min = v.Min(), max = v.Max();
        double range = Math.Max(max - min, 5); // 値がほぼ一定でも線が暴れないよう最低幅を持たせる
        double mid = (max + min) / 2;
        min = mid - range / 2 - range * 0.1;
        max = mid + range / 2 + range * 0.1;

        double w = ActualWidth, h = ActualHeight;
        Point P(int i) => new(i / (double)(v.Count - 1) * w, h - (v[i] - min) / (max - min) * h);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (int i = 1; i < v.Count; i++) ctx.LineTo(P(i), true, true);
        }
        line.Freeze();

        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < v.Count; i++) ctx.LineTo(P(i), false, false);
            ctx.LineTo(new Point(w, h), false, false);
        }
        fill.Freeze();

        var c = Stroke.Color;
        var fillBrush = new LinearGradientBrush(Color.FromArgb(0x40, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        fillBrush.Freeze();
        var pen = new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        dc.DrawGeometry(fillBrush, null, fill);
        dc.DrawGeometry(null, pen, line);
    }
}
