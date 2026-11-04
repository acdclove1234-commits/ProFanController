// ==================================================
// OkLab.cs - 色の計算 (LED に流す「光の量」の色 LinColor と、人の目に自然に色を混ぜられる OKLab 色空間)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 「光の量」で表した色（各成分 0〜1 の小数）。LED に流す電流（PWM の割合）にそのまま比例する値なので、
/// 足したり（光を重ねる）、混ぜたり（クロスフェード）してよい。
/// 画面の色（#RRGGBB）はガンマ補正済みの値なので、そのまま足したり混ぜたりすると暗い色が濁る。
/// 画面の色からの変換は OutputStage.Lin（ガンマ補正）で行う
/// </summary>
public readonly record struct LinColor(float R, float G, float B)
{
    public static readonly LinColor Black = new(0, 0, 0);
    public static readonly LinColor White = new(1, 1, 1);

    public static LinColor operator *(LinColor c, float k) => new(c.R * k, c.G * k, c.B * k);
    public static LinColor operator +(LinColor a, LinColor b) => new(a.R + b.R, a.G + b.G, a.B + b.B);

    /// <summary>a と b の間（t = 0 で a、1 で b）。光の量のまま混ぜるので、2 色の中間が暗く濁らない</summary>
    public static LinColor Lerp(LinColor a, LinColor b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    /// <summary>成分ごとに明るいほうを取る（光の輪が重なったときなど）</summary>
    public static LinColor Max(LinColor a, LinColor b) =>
        new(MathF.Max(a.R, b.R), MathF.Max(a.G, b.G), MathF.Max(a.B, b.B));

    /// <summary>いちばん強い成分の値</summary>
    public float MaxChannel => MathF.Max(R, MathF.Max(G, B));

    /// <summary>各成分を 0〜1 に収める（はみ出た分は切り捨て）</summary>
    public LinColor Clamp01() => new(Math.Clamp(R, 0f, 1f), Math.Clamp(G, 0f, 1f), Math.Clamp(B, 0f, 1f));

    /// <summary>いちばん強い成分が 1 を超えたら、色合いを変えずに全体を下げる（Clamp01 だと白っぽくなる）</summary>
    public LinColor FitToOne()
    {
        float m = MaxChannel;
        return m > 1 ? this * (1 / m) : this;
    }
}

/// <summary>
/// OKLab の色（L = 明るさ 0〜1、A = 緑〜赤、B = 青〜黄）。
/// この空間で 2 色の間を直線で結ぶと、明るさも色合いも目で見て自然に変わる（RGB で混ぜるとくすんだ灰色を通る）
/// </summary>
public readonly record struct Lab(float L, float A, float B)
{
    /// <summary>白（OKLab で L = 1）</summary>
    public static readonly Lab White = new(1, 0, 0);

    public static Lab Lerp(Lab x, Lab y, float t) =>
        new(x.L + (y.L - x.L) * t, x.A + (y.A - x.A) * t, x.B + (y.B - x.B) * t);

    /// <summary>鮮やかさ（OKLCh の C。0 なら灰色）</summary>
    public float Chroma => MathF.Sqrt(A * A + B * B);

    /// <summary>色相（OKLCh の h。ラジアン、−π〜π）</summary>
    public float Hue => MathF.Atan2(B, A);

    /// <summary>明るさ・鮮やかさ・色相（ラジアン）から作る</summary>
    public static Lab FromLch(float l, float c, float h) => new(l, c * MathF.Cos(h), c * MathF.Sin(h));
}

/// <summary>
/// OKLab（Björn Ottosson, 2020）と sRGB の変換。
/// 流れ: 画面の色（sRGB、ガンマ補正済み）⇔ 本当の光の量（linear sRGB）⇔ OKLab
/// </summary>
public static class OkLab
{
    /// <summary>光の量 → 画面の色 の変換表の細かさ（0〜1 を 4096 等分。間は直線で補う）</summary>
    private const int OetfSteps = 4096;
    private static readonly float[] OetfLut = BuildOetf();

    private static float[] BuildOetf()
    {
        var lut = new float[OetfSteps + 1];
        for (int i = 0; i <= OetfSteps; i++) lut[i] = (float)LinearToSrgb(i / (double)OetfSteps);
        return lut;
    }

    /// <summary>画面の色の値（0〜1）→ 本当の光の量（sRGB の規格どおりの曲線）</summary>
    public static double SrgbToLinear(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>本当の光の量（0〜1）→ 画面の色の値（sRGB の規格どおりの曲線）</summary>
    public static double LinearToSrgb(double l) =>
        l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;

    /// <summary>LinearToSrgb の速い版（表を引く。毎コマ LED ごとに呼んでもよい）。0〜1 に収めてから変換する</summary>
    public static float LinearToSrgbFast(float l)
    {
        if (!(l > 0)) return 0;   // NaN も 0 にする
        if (l >= 1) return 1;
        float u = l * OetfSteps;
        int i = (int)u;
        float f = u - i;
        return OetfLut[i] + (OetfLut[i + 1] - OetfLut[i]) * f;
    }

    /// <summary>本当の光の量（linear sRGB、0〜1）→ OKLab</summary>
    public static Lab FromLinear(double r, double g, double b)
    {
        double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
        double l_ = Math.Cbrt(l), m_ = Math.Cbrt(m), s_ = Math.Cbrt(s);
        return new Lab(
            (float)(0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_),
            (float)(1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_),
            (float)(0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_));
    }

    /// <summary>OKLab → 本当の光の量（linear sRGB）。色域の外の色は 0〜1 からはみ出たまま返す</summary>
    public static void ToLinear(Lab c, out float r, out float g, out float b)
    {
        float l_ = c.L + 0.3963377774f * c.A + 0.2158037573f * c.B;
        float m_ = c.L - 0.1055613458f * c.A - 0.0638541728f * c.B;
        float s_ = c.L - 0.0894841775f * c.A - 1.2914855480f * c.B;
        float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
    }

    /// <summary>画面の色（各成分 0〜1）→ OKLab</summary>
    public static Lab FromEncoded(double r, double g, double b) => FromLinear(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));

    /// <summary>画面の色（Rgb）→ OKLab</summary>
    public static Lab FromRgb(Rgb c) => FromEncoded(c.R / 255.0, c.G / 255.0, c.B / 255.0);

    /// <summary>OKLab → 画面の色（各成分 0〜1。色域の外は 0〜1 に切り詰める）</summary>
    public static void ToEncoded(Lab c, out float r, out float g, out float b)
    {
        ToLinear(c, out float lr, out float lg, out float lb);
        r = LinearToSrgbFast(lr);
        g = LinearToSrgbFast(lg);
        b = LinearToSrgbFast(lb);
    }

    /// <summary>OKLab → 画面の色（Rgb）</summary>
    public static Rgb ToRgb(Lab c)
    {
        ToEncoded(c, out float r, out float g, out float b);
        return new Rgb((byte)MathF.Round(r * 255), (byte)MathF.Round(g * 255), (byte)MathF.Round(b * 255));
    }

    /// <summary>
    /// OKLab → LED に送る「光の量」（毎コマ LED ごとに呼んでよい速さ）。
    /// いったん画面の色に戻してから、パレットの色と同じガンマ補正（OutputStage.Lin）をかけるので、
    /// クロスフェードの途中の色と、パレットの表から引いた色の見え方がそろう
    /// </summary>
    public static LinColor ToLed(Lab c, bool gamma)
    {
        ToEncoded(c, out float r, out float g, out float b);
        return new LinColor(OutputStage.Lin(r, gamma), OutputStage.Lin(g, gamma), OutputStage.Lin(b, gamma));
    }

    /// <summary>sRGB で表せる色か（少しの誤差 eps は許す）</summary>
    public static bool InGamut(Lab c, float eps = 1e-4f)
    {
        ToLinear(c, out float r, out float g, out float b);
        return r >= -eps && r <= 1 + eps && g >= -eps && g <= 1 + eps && b >= -eps && b <= 1 + eps;
    }

    /// <summary>
    /// sRGB で表せない色を、明るさと色相はそのままに鮮やかさだけ下げて、表せる色にする（二分探索 12 回）。
    /// 成分ごとに 0〜1 で切り詰めるより、色合いが変わらない
    /// </summary>
    public static Lab FitGamut(Lab c)
    {
        float l = Math.Clamp(c.L, 0f, 1f);
        var x = new Lab(l, c.A, c.B);
        if (InGamut(x)) return x;
        float h = c.Hue, lo = 0, hi = c.Chroma;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (InGamut(Lab.FromLch(l, mid, h))) lo = mid;
            else hi = mid;
        }
        return Lab.FromLch(l, lo, h);
    }
}
