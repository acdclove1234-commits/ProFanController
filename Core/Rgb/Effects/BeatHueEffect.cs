// ==================================================
// BeatHueEffect.cs - 光り方 E2「ビートで色替え」 (ビートのたびにパレットの次の色へ切り替わり、ぱっと光って消えていく)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ビートで色替え: ビートの合図でパレットの次の色へ（OKLab でクロスフェード）。明るさはビートの光と低音。
///   I = max(Fl, 0.6 × BassE)
///   LED の色 = P(いまの色 + 0.08 × (p − 0.5)) × Ilin(I)   … ゾーンの中で色を少しずらし、のっぺりさせない
/// パレットに「選んだ色（Mono）」を使えば、選んだ色とその両隣の色で光る。
/// 遅いデバイス用（slow）では、ビートの光の消え方・色の切り替えを遅くする（BeatDriver が c.Slow を見て行う）
/// </summary>
public sealed class BeatHueEffect : IMusicEffect
{
    /// <summary>ゾーンの端から端での、パレットの位置のずれ</summary>
    public const float GradientWidth = 0.08f;
    /// <summary>低音を明るさに使う割合</summary>
    public const float BassShare = 0.6f;

    private float _i;

    public BeatHueEffect(bool slow = false) => IsSlow = slow;

    public bool IsSlow { get; }
    public string Name => MusicStyles.BeatHue;

    public void Reset(EffectContext c) => _i = 0;

    public void Update(EffectContext c) => _i = Math.Max(c.Fl, BassShare * c.Frame.BassE);

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        float k = c.Ilin(_i);
        for (int j = s.Start; j < s.Start + s.Count; j++)
            dst[j] = c.ColorAt(GradientWidth * (c.P[j] - 0.5f)) * k;
    }
}
