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

    /// <summary>ケースの枠の外に取る、目印の文字の余白（左に「背面」、右に「前」、上下に「上」「下」）</summary>
    private const double PadLeft = 30, PadRight = 20, PadTop = 18, PadBottom = 18;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 330 : Math.Min(available.Width, 400);
        return new Size(w, Math.Max(0, w - PadLeft - PadRight) * Aspect + PadTop + PadBottom);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth - PadLeft - PadRight, h = ActualHeight - PadTop - PadBottom;
        if (w <= 0 || h <= 0) return;
        double cw = Math.Min(w, h / Aspect), ch = cw * Aspect;
        var box = new Rect(PadLeft + (w - cw) / 2, PadTop + (h - ch) / 2, cw, ch);
        var frame = ThemePalette.Cached("case.frame", c => new Pen(new SolidColorBrush(c.Border), 2));
        dc.DrawRoundedRectangle(ThemePalette.ChartBg, frame, box, 10, 10);

        // 目印（前・背面・上・下）は枠の外に書く（LED の点と重ならないように）
        var label = ThemePalette.ChartLabel;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string s, double x, double y)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 11, label, dpi);
            dc.DrawText(ft, new Point(x - ft.Width / 2, y - ft.Height / 2));
        }
        Text(L("背面"), box.Left - PadLeft / 2, box.Top + box.Height / 2);
        Text(L("前"), box.Right + PadRight / 2, box.Top + box.Height / 2);
        Text(L("上"), box.Left + box.Width / 2, box.Top - PadTop / 2);
        Text(L("下"), box.Left + box.Width / 2, box.Bottom + PadBottom / 2);

        // LED を並べる範囲（枠の線に点が乗らないよう、少し内側）
        double inset = cw * 0.035;
        var area = new Rect(box.Left + inset, box.Top + inset, box.Width - 2 * inset, box.Height - 2 * inset);
        Point At(double x, double y) => new(area.Left + x * area.Width, area.Top + y * area.Height);
        double dot = Math.Max(2.5, cw / 110);

        // マザーボードの輪郭（向きの目安）
        var board = ThemePalette.Cached("case.board", c => new Pen(new SolidColorBrush(ThemeManager.WithAlpha(c.Border, 0x90)), 1) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) });
        dc.DrawRoundedRectangle(null, board, new Rect(At(0.12, 0.06), At(0.80, 0.86)), 6, 6);

        // 選んだ場所の形（ファンは円盤、グラボ・メモリ・テープは帯）。同じ場所に何ゾーン置いても 1 回だけ描く
        var body = ThemePalette.Track;
        var hub = ThemePalette.ChartBg;
        foreach (var place in _items.Select(i => i.Place).DistinctBy(p => p.Id))
        {
            if (place.IsRing)
            {
                int rings = Math.Max(1, place.Rings);
                for (int r = 0; r < rings; r++)
                {
                    double u = rings <= 1 ? 0 : r / (double)(rings - 1);
                    var c = At(place.X1 + (place.X2 - place.X1) * u, place.Y1 + (place.Y2 - place.Y1) * u);
                    double rx = place.Radius * area.Width, ry = place.Radius * area.Height;
                    dc.DrawEllipse(body, null, c, rx * 1.2, ry * 1.2);
                    dc.DrawEllipse(hub, null, c, rx * 0.4, ry * 0.4);
                }
            }
            else
            {
                double thick = place.Group == L("テープ") ? dot * 2.2 : place.Id.StartsWith("gpu") ? dot * 6 : dot * 4;
                var pen = new Pen(body, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                dc.DrawLine(pen, At(place.X1, place.Y1), At(place.X2, place.Y2));
            }
        }

        // 場所を決めた LED（最後に送った色。まだ送っていなければ灰色）
        var empty = ThemePalette.SubText;
        foreach (var it in _items)
        {
            var pts = CaseLayout.Points(it.Place, it.Count);
            for (int j = 0; j < pts.Length; j++)
            {
                var p = At(pts[j].X, pts[j].Y);
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
            Text(L("下の一覧で場所を選ぶと、ここに表示されます"), box.Left + box.Width / 2, box.Top + box.Height / 2);
    }

    private static byte[] CreateInverseGamma()
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(Math.Pow(i / 255.0, 1 / 2.2) * 255);
        return lut;
    }
}
