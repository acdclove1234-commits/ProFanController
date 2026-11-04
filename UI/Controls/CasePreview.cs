// ==================================================
// CasePreview.cs - ケースを横から見た絵に、場所を決めた LED の、最後に送った色を描く (ケースの中の配置の確認用)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

public sealed class CasePreview : FrameworkElement
{
    /// <summary>絵に描くゾーン 1 つ（場所・デバイスの中の最初の LED・LED の数・デバイスに最後に送った色）</summary>
    public sealed record Item(CasePlace Place, int Start, int Count, Rgb[]? Colors);

    /// <summary>ケースの縦横比（高さ ÷ 幅。ミドルタワーくらい）</summary>
    private const double Aspect = 1.05;
    private static readonly byte[] InverseGamma = CreateInverseGamma();

    private IReadOnlyList<Item> _items = Array.Empty<Item>();
    private bool _gamma = true;

    /// <summary>描くものを入れ替える（1 秒に何回か呼ばれる）</summary>
    public void SetItems(IReadOnlyList<Item> items, bool gamma)
    {
        _items = items;
        _gamma = gamma;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 320 : Math.Min(available.Width, 360);
        return new Size(w, w * Aspect);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double cw = Math.Min(w, h / Aspect), ch = cw * Aspect;
        var box = new Rect((w - cw) / 2, (h - ch) / 2, cw, ch);
        var frame = ThemePalette.Cached("case.frame", c => new Pen(new SolidColorBrush(c.Border), 2));
        dc.DrawRoundedRectangle(ThemePalette.ChartBg, frame, box, 10, 10);

        // 目印（前・背面・上・下）
        var label = ThemePalette.ChartLabel;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string s, double x, double y, bool center = true)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 11, label, dpi);
            dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y - ft.Height / 2));
        }
        Text("背面", box.Left + 18, box.Top + box.Height / 2);
        Text("前", box.Right - 12, box.Top + box.Height / 2);
        Text("上", box.Left + box.Width / 2, box.Top + 10);
        Text("下", box.Left + box.Width / 2, box.Bottom - 10);

        // 場所を決めた LED（最後に送った色。まだ送っていなければ灰色）
        var empty = ThemePalette.Track;
        double dot = Math.Max(2.5, cw / 110);
        foreach (var it in _items)
        {
            var pts = CaseLayout.Points(it.Place, it.Count);
            for (int j = 0; j < pts.Length; j++)
            {
                var p = new Point(box.Left + pts[j].X * box.Width, box.Top + pts[j].Y * box.Height);
                Brush brush = empty;
                int i = it.Start + j;
                if (it.Colors is { } c && i >= 0 && i < c.Length)
                {
                    var led = c[i];
                    byte r = _gamma ? InverseGamma[led.R] : led.R, g = _gamma ? InverseGamma[led.G] : led.G, b = _gamma ? InverseGamma[led.B] : led.B;
                    var sb = new SolidColorBrush(Color.FromRgb(r, g, b));
                    sb.Freeze();
                    brush = sb;
                }
                dc.DrawEllipse(brush, null, p, dot, dot);
            }
        }
        if (_items.Count == 0)
            Text("下の一覧で場所を選ぶと、ここに表示されます", box.Left + box.Width / 2, box.Top + box.Height / 2);
    }

    private static byte[] CreateInverseGamma()
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(Math.Pow(i / 255.0, 1 / 2.2) * 255);
        return lut;
    }
}
