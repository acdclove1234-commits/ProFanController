// ==================================================
// LedPreview.cs - ライティング画面の LED のプレビュー (デバイスに最後に送った色を、横一列の帯で表示する)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProFanController.UI.Controls;

/// <summary>
/// LED 1 個 = 1 ピクセルの小さな画像を作り、横に引き伸ばして描く（にじませず、くっきり拡大する）。
/// 1 秒に 30 回ほど SetColors で色を渡す。画像とピクセルの配列は LED の数が変わったときだけ作り直すので、
/// ふだんは新しいオブジェクトを作らない。
/// LED に送る色は「LED の光り方に合わせた値」（ガンマ補正済み）なので、画面ではその逆の補正をかけて、
/// 実際の LED の見え方に近づける
/// </summary>
public sealed class LedPreview : FrameworkElement
{
    /// <summary>LED と LED の間に区切り線を描くのは、1 個あたりこの幅（ピクセル）以上あるとき</summary>
    private const double MinLedWidthForGaps = 4;
    /// <summary>角の丸み（ピクセル）</summary>
    private const double Corner = 3;

    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x30)));
    private static readonly Pen GapPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x11, 0x1A))), 1));
    /// <summary>ガンマ補正（2.2 乗）の逆: LED に送った値 → 画面で同じくらいに見える値</summary>
    private static readonly byte[] InverseGamma = CreateInverseGamma();

    private WriteableBitmap? _bitmap;
    private int[] _pixels = Array.Empty<int>();
    private int _count;
    private bool _hasColors;
    private RectangleGeometry? _clip;

    public LedPreview()
    {
        SnapsToDevicePixels = true;
        // 1 ピクセル = LED 1 個を、ぼかさずに拡大する
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>
    /// 表示する色を渡す（null なら「まだ送っていない」として空の帯を描く）。
    /// gamma が true なら、LED 用のガンマ補正を戻して画面向けの色にする
    /// </summary>
    public void SetColors(Rgb[]? colors, bool gamma)
    {
        if (colors is null || colors.Length == 0)
        {
            if (!_hasColors) return;
            _hasColors = false;
            InvalidateVisual();
            return;
        }
        if (_bitmap is null || colors.Length != _count)
        {
            // LED の数が変わったときだけ作り直す
            _count = colors.Length;
            _bitmap = new WriteableBitmap(_count, 1, 96, 96, PixelFormats.Bgr32, null);
            _pixels = new int[_count];
        }
        for (int i = 0; i < _count; i++)
        {
            var c = colors[i];
            int r = gamma ? InverseGamma[c.R] : c.R;
            int g = gamma ? InverseGamma[c.G] : c.G;
            int b = gamma ? InverseGamma[c.B] : c.B;
            _pixels[i] = (r << 16) | (g << 8) | b; // Bgr32 は 1 ピクセル = 0x00RRGGBB
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, _count, 1), _pixels, _count * 4, 0);
        _hasColors = true;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _clip = null; // 大きさが変わったら角丸の形を作り直す
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);
        if (!_hasColors || _bitmap is null)
        {
            dc.DrawRoundedRectangle(EmptyBrush, null, rect, Corner, Corner);
            return;
        }
        if (_clip is null)
        {
            _clip = new RectangleGeometry(rect, Corner, Corner);
            _clip.Freeze();
        }
        dc.PushClip(_clip);
        dc.DrawImage(_bitmap, rect);
        // LED が少ないときは、1 個ずつの区切りを描いて「粒」に見せる
        double each = w / _count;
        if (each >= MinLedWidthForGaps)
        {
            for (int i = 1; i < _count; i++)
            {
                double x = Math.Round(i * each) + 0.5;
                dc.DrawLine(GapPen, new Point(x, 0), new Point(x, h));
            }
        }
        dc.Pop();
    }

    private static byte[] CreateInverseGamma()
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(Math.Pow(i / 255.0, 1 / 2.2) * 255);
        return lut;
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
