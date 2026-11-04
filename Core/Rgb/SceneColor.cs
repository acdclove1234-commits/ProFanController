// ==================================================
// SceneColor.cs - 速いデバイスがいま光っている色から、全体の代表の色を 1 つ作る
//                 (グラボ・メモリなど更新の遅いデバイスを、ほかのデバイスの色に合わせるため)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 代表の色: LED ごとの色を OKLab にして、明るさ（いちばん強い成分）で重みを付けて平均する。
///   色相 = 重み付きの (a, b) の向き、鮮やかさ = 重み付きの鮮やかさの平均 × まとまり具合（色相がばらばらなら白に近づく）、L = 重み付きの平均
///   明るさ = LED の明るさの平均（0〜1）
/// 結果の色は、いちばん強い成分が 1 になるようにそろえる（明るさは別に返す）
/// </summary>
public struct SceneColor
{
    /// <summary>これより暗い LED は数えない（消えている LED で色が薄まらないように）</summary>
    public const float MinLevel = 0.02f;

    private double _w, _l, _a, _b, _c, _level;
    private int _count;

    /// <summary>LED の色を足す（光の量。明るさを掛けた後の値）</summary>
    public void Add(ReadOnlySpan<LinColor> light)
    {
        foreach (var c in light)
        {
            float level = Math.Max(c.R, Math.Max(c.G, c.B));
            _count++;
            _level += Math.Clamp(level, 0f, 1f);
            if (level < MinLevel) continue;
            var lab = OkLab.FromLinear(c.R / level, c.G / level, c.B / level);
            double chroma = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
            _w += level;
            _l += level * lab.L;
            _a += level * lab.A;
            _b += level * lab.B;
            _c += level * chroma;
        }
    }

    /// <summary>代表の色（いちばん強い成分が 1）と明るさ。光っている LED がなければ false</summary>
    public readonly bool TryGet(out LinColor color, out float level)
    {
        color = LinColor.Black;
        level = _count > 0 ? (float)(_level / _count) : 0f;
        if (_w < 1e-6) return false;
        double a = _a / _w, b = _b / _w, meanChroma = _c / _w;
        double resultant = Math.Sqrt(a * a + b * b);
        // 色相がそろっているほど鮮やかに（全部が同じ色なら、その色の鮮やかさのまま）
        double chroma = meanChroma <= 1e-9 ? 0 : meanChroma * Math.Sqrt(Math.Clamp(resultant / meanChroma, 0, 1));
        double hue = Math.Atan2(b, a);
        // 明るさ（OKLab の L）も平均にそろえる（決まった値にすると、赤・青など暗めの色が色域からはみ出て薄くなる）
        var lab = OkLab.FitGamut(Lab.FromLch((float)(_l / _w), (float)chroma, (float)hue));
        OkLab.ToLinear(lab, out float r, out float g, out float bl);
        var c = new LinColor(Math.Max(0, r), Math.Max(0, g), Math.Max(0, bl));
        float m = c.MaxChannel;
        if (!(m > 0)) return false;
        color = c * (1 / m);
        return true;
    }
}
