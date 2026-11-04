// ==================================================
// EnergyEffect.cs - 光り方 E4「エナジー」 (低音・中音・高音の帯が、それぞれの強さの分だけ「始まり」から伸びる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// エナジー（LedFx 風）: 3 本の光の帯を重ねる。帯 g の長さ ℓ_g = e_g（低音・中音・高音の強さ）。
///   coverage_g(j) = clamp(ℓ_g × Ne − jx, 0, 1)   … 帯の先端の LED は端数の分だけ光る（なめらかに伸び縮みする）
///   LED の色 = Σ coverage_g × 色_g + F0 × 低音の色   （1 を超えたら色合いを保ったまま下げる）
/// 色は、いまの色（ビートで進む）からパレットの幅の 0・半分・全部ずらした 3 色。6 個未満のゾーンは「ミックス」で光る
/// </summary>
public sealed class EnergyEffect : IMusicEffect
{
    private readonly MixEffect _small = new();
    private readonly LinColor[] _col = new LinColor[3];
    private readonly float[] _len = new float[3];

    public string Name => MusicStyles.Energy;

    public void Reset(EffectContext c) => _small.Reset(c);

    public void Update(EffectContext c)
    {
        double half = c.Pal.Span * 0.5;
        for (int g = 0; g < 3; g++) _col[g] = c.ColorAt(g * half);
        _len[0] = Math.Clamp(c.Frame.BassE, 0f, 1f);
        _len[1] = Math.Clamp(c.Frame.MidE, 0f, 1f);
        _len[2] = Math.Clamp(c.Frame.TrebleE, 0f, 1f);
        if (c.HasSmallSegment) _small.Update(c);
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        float ne = s.Ne;
        var floor = _col[0] * c.F0;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            float jx = c.Jx[j];
            var sum = floor;
            for (int g = 0; g < 3; g++)
            {
                float cov = Math.Clamp(_len[g] * ne - jx, 0f, 1f);
                if (cov > 0) sum += _col[g] * cov;
            }
            dst[j] = sum.FitToOne();
        }
    }
}
