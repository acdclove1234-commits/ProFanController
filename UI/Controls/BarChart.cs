// ==================================================
// BarChart.cs - 日ごとの棒グラフ (電気代・電力量の推移。マウスを乗せると値を表示)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

public sealed record BarItem(string Label, double Value, string Tip);

public sealed class BarChart : FrameworkElement
{
    // 背景・目盛り・文字・棒の色は、いまのテーマの色（ThemePalette）を使う
    private static Brush BgBrush => ThemePalette.ChartBg;
    private static Pen GridPen => ThemePalette.ChartGrid;
    private static Brush LabelBrush => ThemePalette.ChartLabel;
    private static Brush BarBrush => ThemePalette.Cached("bar.fill", c => new LinearGradientBrush(c.Accent2, c.Accent, 90));
    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)));
    private static Brush TipBg => ThemePalette.TipBg;
    private static Brush TextBrush => ThemePalette.Text;
    private const double PadL = 44, PadR = 12, PadT = 12, PadB = 26;

    private IReadOnlyList<BarItem> _items = Array.Empty<BarItem>();
    private int _hover = -1;

    public IReadOnlyList<BarItem> Items
    {
        get => _items;
        set
        {
            _items = value;
            InvalidateVisual();
        }
    }

    /// <summary>縦軸の単位（例: 円）</summary>
    public string Unit { get; set; } = "";

    public BarChart() => ClipToBounds = true;

    private Rect Plot => new(PadL, PadT, Math.Max(1, ActualWidth - PadL - PadR), Math.Max(1, ActualHeight - PadT - PadB));

    /// <summary>モニターの倍率が変わった（別のモニターへ移した）ら、文字をその倍率で描き直す</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
        if (_items.Count == 0) return;
        var r = Plot;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double max = Math.Max(1e-6, _items.Max(i => i.Value));
        double step = NiceStep(max / 4);
        double top = Math.Ceiling(max / step) * step;
        for (int i = 0; i <= 4; i++)
        {
            double v = top * i / 4;
            double y = r.Bottom - v / top * r.Height;
            dc.DrawLine(GridPen, new Point(r.Left, y), new Point(r.Right, y));
            Text(dc, v >= 10 ? $"{v:N0}" : $"{v:0.#}", new Point(r.Left - 8, y - 8), dpi, LabelBrush, right: true);
        }

        double slot = r.Width / _items.Count;
        double w = Math.Max(2, slot * 0.62);
        for (int i = 0; i < _items.Count; i++)
        {
            double h = _items[i].Value / top * r.Height;
            double x = r.Left + i * slot + (slot - w) / 2;
            if (h > 0) dc.DrawRoundedRectangle(i == _hover ? HoverBrush : BarBrush, null, new Rect(x, r.Bottom - h, w, h), Math.Min(3, w / 2), Math.Min(3, w / 2));
            // ラベルは間引いて表示（重ならないように）
            int every = Math.Max(1, (int)Math.Ceiling(40 / slot));
            if (i % every == 0 || i == _items.Count - 1)
                Text(dc, _items[i].Label, new Point(r.Left + i * slot + slot / 2, r.Bottom + 6), dpi, LabelBrush, center: true);
        }

        if (_hover >= 0 && _hover < _items.Count)
        {
            var ft = Format(_items[_hover].Tip, dpi, TextBrush, 12, FontWeights.SemiBold);
            double tx = Math.Clamp(r.Left + _hover * slot + slot / 2 - ft.Width / 2 - 8, 4, Math.Max(4, ActualWidth - ft.Width - 20));
            dc.DrawRoundedRectangle(TipBg, null, new Rect(tx, r.Top, ft.Width + 16, ft.Height + 10), 6, 6);
            dc.DrawText(ft, new Point(tx + 8, r.Top + 5));
        }
    }

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
        double n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    private static FormattedText Format(string text, double dpi, Brush brush, double size, FontWeight weight)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(UiFonts.Family, FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi);

    private static void Text(DrawingContext dc, string text, Point at, double dpi, Brush brush, bool right = false, bool center = false)
    {
        var ft = Format(text, dpi, brush, 12, FontWeights.Medium);
        double x = right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
        dc.DrawText(ft, new Point(x, at.Y));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var r = Plot;
        double x = e.GetPosition(this).X;
        int hover = _items.Count == 0 || x < r.Left || x > r.Right ? -1 : Math.Clamp((int)((x - r.Left) / (r.Width / _items.Count)), 0, _items.Count - 1);
        if (hover == _hover) return;
        _hover = hover;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
