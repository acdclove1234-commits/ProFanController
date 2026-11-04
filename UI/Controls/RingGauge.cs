// ==================================================
// RingGauge.cs - 円形ゲージ (下側が開いた 270° の弧で値を表示し、中央に文字を出す)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterTextProperty = DependencyProperty.Register(
        nameof(CenterText), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0〜100</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string CenterText { get => (string)GetValue(CenterTextProperty); set => SetValue(CenterTextProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private const double StartAngle = 135;  // 左下から
    private const double SweepAngle = 270;  // 右下まで
    private static readonly Pen TrackPen = CreatePen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x42)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF6)));
    private static readonly Brush SubBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0xA8)));
    private static readonly Brush DefaultStroke = Frozen(new LinearGradientBrush(Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0x22, 0xC7, 0xFF), 0));

    /// <summary>モニターの倍率が変わった（別のモニターへ移した）ら、文字をその倍率で描き直す</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double thickness = Math.Max(3, size * 0.08);
        double radius = size / 2 - thickness / 2 - 1;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawGeometry(null, Pen(TrackPen.Brush, thickness), Arc(center, radius, SweepAngle));
        double v = Math.Clamp(Value, 0, 100);
        if (v > 0.5)
        {
            dc.DrawGeometry(null, Pen(Stroke ?? DefaultStroke, thickness), Arc(center, radius, SweepAngle * v / 100));
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var main = Text(CenterText, size * 0.24, FontWeights.SemiBold, TextBrush, dpi);
        var sub = Text(SubText, size * 0.13, FontWeights.Medium, SubBrush, dpi);
        double total = main.Height + (SubText.Length > 0 ? sub.Height : 0);
        double y = center.Y - total / 2;
        dc.DrawText(main, new Point(center.X - main.Width / 2, y));
        if (SubText.Length > 0) dc.DrawText(sub, new Point(center.X - sub.Width / 2, y + main.Height - size * 0.02));
    }

    private static Geometry Arc(Point c, double r, double sweep)
    {
        Point At(double deg)
        {
            double rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(StartAngle), false, false);
            ctx.ArcTo(At(StartAngle + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static FormattedText Text(string s, double size, FontWeight weight, Brush brush, double dpi)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(UiFonts.Family, FontStyles.Normal, weight, FontStretches.Normal), Math.Max(8, size), brush, dpi);

    private static Pen Pen(Brush b, double w)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    private static Pen CreatePen(Brush b)
    {
        b.Freeze();
        var p = new Pen(b, 1);
        p.Freeze();
        return p;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
